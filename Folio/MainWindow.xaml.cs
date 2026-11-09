using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static Folio.Native;

namespace Folio;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    public const string ThisPC = "This PC";
    const double MinThumb = 32, MaxThumb = 1600;
    static readonly StringComparer PathCmp = StringComparer.OrdinalIgnoreCase;

    readonly Settings _settings;
    readonly string? _startPath;
    string? _current;
    readonly List<string> _back = new(), _forward = new();
    List<FileItem> _items = new();
    IList<FileItem> _shown = new List<FileItem>();
    ObservableCollection<FileItem>? _searchResults;
    CancellationTokenSource? _searchCts;
    bool _searching, _suppressSearch, _ready;
    int _navId;
    FileSystemWatcher? _watcher;
    readonly DispatcherTimer _refreshTimer, _thumbTimer;
    double _dpi = 1;
    bool _details;
    string _sortBy = "Name";
    bool _sortDesc;
    Point _dragStart;
    FileItem? _dragItem, _clickSelect;
    ImageSource? _folderIcon;

    // ---------- bindable view properties ----------
    double _thumbSize;
    public double ThumbSize
    {
        get => _thumbSize;
        set
        {
            value = Math.Clamp(Math.Round(value), MinThumb, MaxThumb);
            if (value == _thumbSize) return;
            _thumbSize = value;
            Changed(nameof(ThumbSize)); Changed(nameof(TileWidth)); Changed(nameof(TileHeight)); Changed(nameof(ThumbSizeText));
            _thumbTimer?.Stop(); _thumbTimer?.Start();
        }
    }
    public double TileWidth => ThumbSize + 28;
    public double TileHeight => ThumbSize + 64;
    public string ThumbSizeText => $"{ThumbSize:0} px";

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    ListBox ActiveList => _details ? DetailsList : TileList;
    IntPtr Hwnd => new WindowInteropHelper(this).Handle;
    bool InRealFolder => _current != null && _current != ThisPC && _searchResults == null;

    public MainWindow(string? startPath = null)
    {
        _settings = Settings.Load();
        _startPath = startPath;
        _thumbSize = Math.Clamp(_settings.ThumbSize, MinThumb, MaxThumb);
        _sortBy = _settings.SortBy;
        _sortDesc = _settings.SortDesc;
        FileItem.ShowExtensions = _settings.ShowExtensions;

        InitializeComponent();
        DataContext = this;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refreshTimer.Tick += async (_, _) => { _refreshTimer.Stop(); await Reload(); };
        _thumbTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _thumbTimer.Tick += (_, _) => { _thumbTimer.Stop(); ApplyThumbPixels(); };

        Width = _settings.Width; Height = _settings.Height;
        if (_settings.Maximized) WindowState = WindowState.Maximized;
        TreeColumn.Width = new GridLength(Math.Clamp(_settings.TreeWidth, 120, 600));
        HiddenCheck.IsChecked = _settings.ShowHidden;
        ExtCheck.IsChecked = _settings.ShowExtensions;
        SortBox.SelectedItem = SortBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _sortBy) ?? SortBox.Items[0];
        UpdateSortDirGlyph();
        SetViewMode(_settings.Details);

        foreach (var list in new ListBox[] { TileList, DetailsList })
        {
            list.MouseDoubleClick += List_MouseDoubleClick;
            list.MouseRightButtonUp += List_MouseRightButtonUp;
            list.PreviewMouseLeftButtonDown += List_PreviewMouseLeftButtonDown;
            list.PreviewMouseLeftButtonUp += List_PreviewMouseLeftButtonUp;
            list.PreviewMouseMove += List_PreviewMouseMove;
            list.PreviewMouseWheel += List_PreviewMouseWheel;
            list.SelectionChanged += (_, _) => UpdateStatus();
            list.DragOver += List_DragOver;
            list.Drop += List_Drop;
        }

        SourceInitialized += (_, _) => HwndSource.FromHwnd(Hwnd)?.AddHook(ShellContextMenu.WndProc);
        Loaded += async (_, _) =>
        {
            _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            ApplyDpi();
            BuildTree();
            _ready = true;
            string start = _startPath ?? _settings.LastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (start != ThisPC && !Directory.Exists(start)) start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            await Navigate(start, addHistory: false);
            ActiveList.Focus();
        };
        Closing += (_, _) => SaveSettings();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpi = newDpi.DpiScaleX;
        ApplyDpi();
    }

    void ApplyDpi()
    {
        App.Loader.DpiScale = _dpi;
        FileItem.IconPixels = (int)Math.Round(20 * _dpi);
        ApplyThumbPixels();
    }

    void ApplyThumbPixels()
    {
        FileItem.ThumbPixels = (int)Math.Round(ThumbSize * _dpi);
        foreach (var it in _shown) it.RefreshThumb();
    }

    void SaveSettings()
    {
        _settings.ThumbSize = ThumbSize;
        _settings.Details = _details;
        _settings.ShowHidden = HiddenCheck.IsChecked == true;
        _settings.ShowExtensions = ExtCheck.IsChecked == true;
        _settings.LastFolder = _current;
        _settings.SortBy = _sortBy;
        _settings.SortDesc = _sortDesc;
        _settings.Maximized = WindowState == WindowState.Maximized;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.Width = b.Width; _settings.Height = b.Height;
        _settings.TreeWidth = TreeColumn.ActualWidth;
        _settings.Save();
    }

    // =====================================================================
    // Navigation
    // =====================================================================

    static string? Normalize(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0) return null;
        if (path.Equals(ThisPC, StringComparison.OrdinalIgnoreCase) || path.Equals("Computer", StringComparison.OrdinalIgnoreCase)) return ThisPC;
        try
        {
            path = Environment.ExpandEnvironmentVariables(path);
            if (path.StartsWith('~')) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
            if (path.Length == 2 && path[1] == ':') path += "\\";
            path = Path.GetFullPath(path);
            var root = Path.GetPathRoot(path);
            if (!PathCmp.Equals(path, root)) path = path.TrimEnd('\\', '/');
            return path;
        }
        catch { return null; }
    }

    static bool PathEq(string? a, string? b) => a != null && b != null && PathCmp.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'));

    async Task Navigate(string? input, bool addHistory = true, string? selectPath = null)
    {
        if (string.IsNullOrWhiteSpace(input)) return;
        var path = Normalize(input);
        if (path == null) { ShowError($"Windows can't find '{input}'. Check the spelling and try again."); UpdateChrome(); return; }

        if (path != ThisPC && !Directory.Exists(path))
        {
            if (File.Exists(path)) { OpenPath(path); UpdateChrome(); return; }
            ShowError($"Windows can't find '{input}'. Check the spelling and try again.");
            UpdateChrome();
            return;
        }

        int id = ++_navId;
        CancelSearch(clearBox: true);
        bool showHidden = HiddenCheck.IsChecked == true;
        List<FileItem> items;
        try { items = await Task.Run(() => LoadItems(path, showHidden, null)); }
        catch (Exception ex) { if (id == _navId) { ShowError(ex.Message); UpdateChrome(); } return; }
        if (id != _navId) return;

        if (addHistory && _current != null && !PathEq(_current, path)) { _back.Add(_current); _forward.Clear(); }
        _current = path;
        _items = items;
        App.Loader.Clear();
        Watch(path);
        ApplyView();
        ScrollToTop();
        if (selectPath != null) SelectAndReveal(selectPath);
        UpdateChrome();
    }

    static List<FileItem> LoadItems(string path, bool showHidden, Dictionary<string, FileItem>? old)
    {
        var list = new List<FileItem>();
        if (path == ThisPC)
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try { list.Add(FileItem.FromDrive(d)); } catch { }
            }
            return list;
        }
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) throw new DirectoryNotFoundException($"'{path}' is no longer available.");
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var fsi in dir.EnumerateFileSystemInfos("*", opts))
        {
            if (!showHidden && (fsi.Attributes & FileAttributes.Hidden) != 0) continue;
            if (old != null && old.TryGetValue(fsi.FullName, out var prev) && prev.Modified == fsi.LastWriteTime
                && (prev.IsFolder || (fsi is FileInfo fi && fi.Length == prev.Size)))
            {
                list.Add(prev); // keep its loaded thumbnail
                continue;
            }
            list.Add(FileItem.FromInfo(fsi));
        }
        return list;
    }

    /// <summary>Re-read the current folder in place, keeping thumbnails, selection and scroll.</summary>
    async Task Reload(string? selectPath = null)
    {
        if (_current == null || _searchResults != null) return;
        int id = _navId;
        string path = _current;
        var old = new Dictionary<string, FileItem>(PathCmp);
        foreach (var i in _items) old[i.FullPath] = i;
        var selected = ActiveList.SelectedItems.Cast<FileItem>().Select(i => i.FullPath).ToHashSet(PathCmp);
        if (selectPath != null) { selected.Clear(); selected.Add(selectPath); }
        bool showHidden = HiddenCheck.IsChecked == true;
        List<FileItem> items;
        try { items = await Task.Run(() => LoadItems(path, showHidden, old)); }
        catch (DirectoryNotFoundException)
        {
            if (id == _navId) await Navigate(Directory.GetParent(path)?.FullName ?? ThisPC, false);
            return;
        }
        catch { return; }
        if (id != _navId) return;
        _items = items;
        ApplyView();
        var list = ActiveList;
        foreach (var it in _shown) if (selected.Contains(it.FullPath)) list.SelectedItems.Add(it);
        if (selectPath != null) SelectAndReveal(selectPath);
    }

    void Watch(string path)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (path == ThisPC) return;
        try
        {
            var w = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
            w.Created += h; w.Deleted += h; w.Changed += h;
            w.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch { }
    }

    async Task GoBack()
    {
        if (_back.Count == 0) return;
        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        if (_current != null) _forward.Add(_current);
        await Navigate(target, addHistory: false);
    }

    async Task GoForward()
    {
        if (_forward.Count == 0) return;
        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        if (_current != null) _back.Add(_current);
        await Navigate(target, addHistory: false);
    }

    async Task GoUp()
    {
        if (_current == null || _current == ThisPC) return;
        var parent = Directory.GetParent(_current)?.FullName ?? ThisPC;
        await Navigate(parent, selectPath: _current);
    }

    void UpdateChrome()
    {
        if (_current == null) return;
        AddressBox.Text = _current;
        string name = _current == ThisPC ? ThisPC : (Path.GetFileName(_current) is { Length: > 0 } n ? n : _current);
        Title = $"{name} - Folio";
        BackButton.IsEnabled = _back.Count > 0;
        ForwardButton.IsEnabled = _forward.Count > 0;
        UpButton.IsEnabled = _current != ThisPC;
    }

    // =====================================================================
    // View: sorting, filtering, binding
    // =====================================================================

    void ApplyView()
    {
        if (_searchResults != null)
        {
            _shown = _searchResults;
            Bind(_searchResults);
        }
        else
        {
            IEnumerable<FileItem> src = _items;
            var filter = SearchBox.Text.Trim();
            if (filter.Length > 0 && SubfolderCheck.IsChecked != true)
                src = src.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
            var list = src.ToList();
            list.Sort(Compare);
            _shown = list;
            Bind(list);
        }
        EmptyText.Text = _searchResults != null ? (_searching ? "Searching…" : "No items match your search.")
                       : SearchBox.Text.Trim().Length > 0 ? "No items match your search." : "This folder is empty.";
        EmptyText.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatus();
    }

    void Bind(IEnumerable<FileItem> items)
    {
        TileList.ItemsSource = items;
        DetailsList.ItemsSource = items;
    }

    int Compare(FileItem a, FileItem b)
    {
        if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
        if (a.IsDrive && b.IsDrive) return string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase);
        int r = _sortBy switch
        {
            "Date" => a.Modified.CompareTo(b.Modified),
            "Size" => a.Size.CompareTo(b.Size),
            "Type" => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };
        if (r == 0) r = StrCmpLogicalW(a.Name, b.Name);
        return _sortDesc ? -r : r;
    }

    void UpdateStatus()
    {
        var sel = ActiveList.SelectedItems.Cast<FileItem>().ToList();
        var s = $"{_shown.Count:N0} item{(_shown.Count == 1 ? "" : "s")}";
        if (sel.Count > 0)
        {
            s += $"    {sel.Count:N0} selected";
            var files = sel.Where(i => !i.IsFolder).ToList();
            if (files.Count > 0) s += $"  {FileItem.FormatSize(files.Sum(i => i.Size))}";
        }
        if (_searching) s += "    Searching…";
        StatusText.Text = s;
        if (_shown.Count > 0 && EmptyText.Visibility == Visibility.Visible) EmptyText.Visibility = Visibility.Collapsed;
    }

    void SetViewMode(bool details)
    {
        var selected = ActiveList.SelectedItems.Cast<FileItem>().ToList();
        _details = details;
        TileList.Visibility = details ? Visibility.Collapsed : Visibility.Visible;
        DetailsList.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
        GridToggle.IsChecked = !details;
        DetailsToggle.IsChecked = details;
        var list = ActiveList;
        list.SelectedItems.Clear();
        foreach (var i in selected) list.SelectedItems.Add(i);
        if (selected.Count > 0) list.ScrollIntoView(selected[0]);
        UpdateStatus();
    }

    void ScrollToTop()
    {
        foreach (var l in new ListBox[] { TileList, DetailsList })
            FindChild<ScrollViewer>(l)?.ScrollToTop();
    }

    void SelectAndReveal(string path)
    {
        var item = _shown.FirstOrDefault(i => PathEq(i.FullPath, path));
        if (item == null) return;
        var list = ActiveList;
        list.SelectedItem = item;
        list.ScrollIntoView(item);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            (list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem)?.Focus();
        });
    }

    void UpdateSortDirGlyph() => SortDirButton.Content = _sortDesc ? "" : "";

    // =====================================================================
    // Search
    // =====================================================================

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearch || !_ready) return;
        if (SubfolderCheck.IsChecked == true)
        {
            if (SearchBox.Text.Trim().Length == 0 && _searchResults != null) { CancelSearch(false); ApplyView(); }
            return; // recursive search runs on Enter
        }
        ApplyView();
    }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (SubfolderCheck.IsChecked == true) StartRecursiveSearch(SearchBox.Text.Trim());
            ActiveList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelSearch(clearBox: true);
            ApplyView();
            ActiveList.Focus();
            e.Handled = true;
        }
    }

    void SubfolderCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        CancelSearch(false);
        if (SubfolderCheck.IsChecked == true && SearchBox.Text.Trim().Length > 0) StartRecursiveSearch(SearchBox.Text.Trim());
        else ApplyView();
    }

    void StartRecursiveSearch(string term)
    {
        CancelSearch(false);
        if (_current == null || _current == ThisPC || term.Length == 0) { ApplyView(); return; }
        var cts = _searchCts = new CancellationTokenSource();
        var results = _searchResults = new ObservableCollection<FileItem>();
        _searching = true;
        ApplyView();
        ScrollToTop();
        string root = _current;
        bool showHidden = HiddenCheck.IsChecked == true;

        Task.Run(() =>
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive,
                AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
            };
            var batch = new List<FileItem>();
            var sw = Stopwatch.StartNew();
            void Flush()
            {
                var b = batch;
                batch = new List<FileItem>();
                sw.Restart();
                Dispatcher.BeginInvoke(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    foreach (var i in b) results.Add(i);
                    UpdateStatus();
                });
            }
            try
            {
                foreach (var fsi in new DirectoryInfo(root).EnumerateFileSystemInfos($"*{term}*", opts))
                {
                    if (cts.IsCancellationRequested) return;
                    batch.Add(FileItem.FromInfo(fsi));
                    if (batch.Count >= 200 || sw.ElapsedMilliseconds > 250) Flush();
                }
            }
            catch { }
            Flush();
            Dispatcher.BeginInvoke(() =>
            {
                if (cts.IsCancellationRequested) return;
                _searching = false;
                if (results.Count == 0) { EmptyText.Text = "No items match your search."; EmptyText.Visibility = Visibility.Visible; }
                UpdateStatus();
            });
        });
    }

    void CancelSearch(bool clearBox)
    {
        _searchCts?.Cancel();
        _searchCts = null;
        _searchResults = null;
        _searching = false;
        if (clearBox && SearchBox.Text.Length > 0)
        {
            _suppressSearch = true;
            SearchBox.Text = "";
            _suppressSearch = false;
        }
    }

    // =====================================================================
    // Opening items
    // =====================================================================

    async void OpenItems(IList<FileItem> items)
    {
        if (items.Count == 0) return;
        if (items.Count == 1 && items[0].IsFolder) { await Navigate(items[0].FullPath); return; }
        foreach (var i in items.Where(i => !i.IsFolder).Take(15)) OpenPath(i.FullPath);
    }

    async void OpenPath(string path)
    {
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && FileOps.ResolveShortcut(path) is { } target && Directory.Exists(target))
        {
            await Navigate(target);
            return;
        }
        try { FileOps.Open(path); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    List<FileItem> Selected() => ActiveList.SelectedItems.Cast<FileItem>().ToList();

    // =====================================================================
    // File operations
    // =====================================================================

    void CopySelected(bool cut)
    {
        var paths = Selected().Where(i => !i.IsDrive).Select(i => i.FullPath).ToArray();
        if (paths.Length == 0) return;
        try
        {
            var data = new DataObject();
            var sc = new StringCollection();
            sc.AddRange(paths);
            data.SetFileDropList(sc);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
            Clipboard.SetDataObject(data, true);
        }
        catch (Exception ex) { ShowError("Couldn't use the clipboard: " + ex.Message); }
    }

    void Paste()
    {
        if (!InRealFolder) return;
        try
        {
            if (!Clipboard.ContainsFileDropList()) return;
            var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
            if (files.Length == 0) return;
            bool move = false;
            if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
            {
                var b = new byte[4];
                ms.ReadExactly(b, 0, 4);
                move = (BitConverter.ToInt32(b) & 2) != 0;
            }
            ushort flags = FOF_ALLOWUNDO;
            if (!move && files.Any(f => PathEq(Path.GetDirectoryName(f), _current))) flags |= FOF_RENAMEONCOLLISION;
            FileOps.Run(move ? FO_MOVE : FO_COPY, files, _current, flags, Hwnd, background: true);
            if (move) Clipboard.Clear();
        }
        catch (Exception ex) { ShowError("Couldn't paste: " + ex.Message); }
    }

    void DeleteSelected(bool permanent)
    {
        var paths = Selected().Where(i => !i.IsDrive).Select(i => i.FullPath).ToArray();
        if (paths.Length == 0) return;
        FileOps.Run(FO_DELETE, paths, null, permanent ? (ushort)0 : FOF_ALLOWUNDO, Hwnd, background: false);
        _ = Reload();
    }

    async void RenameSelected()
    {
        var item = Selected().FirstOrDefault();
        if (item == null || item.IsDrive) return;
        var name = InputDialog.Show(this, "Rename", "New name:", item.Name, selectStem: !item.IsFolder);
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowError("A file name can't contain any of the following characters:\n\\ / : * ? \" < > |");
            return;
        }
        var dest = Path.Combine(Path.GetDirectoryName(item.FullPath)!, name);
        FileOps.Run(FO_RENAME, [item.FullPath], dest, FOF_ALLOWUNDO, Hwnd, background: false);
        await Reload(selectPath: dest);
    }

    async void NewFolder()
    {
        if (!InRealFolder) return;
        string name = "New folder";
        for (int n = 2; Directory.Exists(Path.Combine(_current!, name)) || File.Exists(Path.Combine(_current!, name)); n++)
            name = $"New folder ({n})";
        var input = InputDialog.Show(this, "New folder", "Folder name:", name, selectStem: false);
        if (string.IsNullOrWhiteSpace(input)) return;
        if (input.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowError("A folder name can't contain any of the following characters:\n\\ / : * ? \" < > |");
            return;
        }
        var path = Path.Combine(_current!, input);
        if (Directory.Exists(path) || File.Exists(path)) { ShowError($"'{input}' already exists."); return; }
        try { Directory.CreateDirectory(path); }
        catch (Exception ex) { ShowError(ex.Message); return; }
        await Reload(selectPath: path);
    }

    void ShowPropertiesFor(IList<FileItem> items)
    {
        var path = items.FirstOrDefault()?.FullPath ?? (_current != ThisPC ? _current : null);
        if (path != null) FileOps.ShowProperties(path, Hwnd);
    }

    void OpenTerminal()
    {
        if (_current == null || _current == ThisPC) return;
        try { Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{_current.TrimEnd('\\')}\\.\"") { UseShellExecute = true }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo("pwsh.exe") { WorkingDirectory = _current, UseShellExecute = true }); }
            catch { Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = _current, UseShellExecute = true }); }
        }
    }

    void ShowError(string msg) => MessageBox.Show(this, msg, "Folio", MessageBoxButton.OK, MessageBoxImage.Warning);

    // =====================================================================
    // Mouse: open, context menus, drag & drop, Ctrl+wheel zoom
    // =====================================================================

    static FileItem? ItemAt(ItemsControl list, object source)
    {
        var d = source as DependencyObject;
        while (d != null && d is not ListBoxItem)
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is ListBoxItem c && ItemsControl.ItemsControlFromItemContainer(c) == list) return c.DataContext as FileItem;
        return null;
    }

    void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var item = ItemAt((ListBox)sender, e.OriginalSource);
        if (item != null) OpenItems([item]);
    }

    void List_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        var item = ItemAt(list, e.OriginalSource);
        e.Handled = true;
        if (item == null)
        {
            list.UnselectAll();
            ShowBackgroundMenu(list);
            return;
        }
        if (!list.SelectedItems.Contains(item)) list.SelectedItem = item;
        var sel = Selected();
        var parent = Path.GetDirectoryName(item.FullPath);
        var paths = sel.All(i => PathCmp.Equals(Path.GetDirectoryName(i.FullPath) ?? "", parent ?? ""))
            ? sel.Select(i => i.FullPath).ToList()
            : [item.FullPath];
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        ShellContextMenu.Show(Hwnd, paths, shift, verb => HandleShellVerb(verb, sel));
    }

    bool HandleShellVerb(string verb, List<FileItem> sel)
    {
        switch (verb.ToLowerInvariant())
        {
            case "rename":
                RenameSelected();
                return true;
            case "open":
            case "explore":
                if (sel.Count == 1 && sel[0].IsFolder) { _ = Navigate(sel[0].FullPath); return true; }
                return false;
            case "opennewwindow":
                if (sel.Count == 1 && sel[0].IsFolder) { new MainWindow(sel[0].FullPath).Show(); return true; }
                return false;
        }
        return false;
    }

    void ShowBackgroundMenu(ListBox list)
    {
        var menu = new ContextMenu { PlacementTarget = list };
        MenuItem Item(string header, Action act, bool enabled = true, bool? check = null)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            if (check != null) { mi.IsCheckable = true; mi.IsChecked = check.Value; }
            mi.Click += (_, _) => act();
            return mi;
        }

        var view = new MenuItem { Header = "View" };
        view.Items.Add(Item("Thumbnails", () => SetViewMode(false), check: !_details));
        view.Items.Add(Item("Details", () => SetViewMode(true), check: _details));
        menu.Items.Add(view);

        var sort = new MenuItem { Header = "Sort by" };
        foreach (var (label, key) in new[] { ("Name", "Name"), ("Date modified", "Date"), ("Type", "Type"), ("Size", "Size") })
            sort.Items.Add(Item(label, () => SetSort(key, _sortDesc), check: _sortBy == key));
        sort.Items.Add(new Separator());
        sort.Items.Add(Item("Ascending", () => SetSort(_sortBy, false), check: !_sortDesc));
        sort.Items.Add(Item("Descending", () => SetSort(_sortBy, true), check: _sortDesc));
        menu.Items.Add(sort);
        menu.Items.Add(Item("Refresh", () => _ = Reload()));
        menu.Items.Add(new Separator());

        bool canPaste = false;
        try { canPaste = InRealFolder && Clipboard.ContainsFileDropList(); } catch { }
        menu.Items.Add(Item("Paste", Paste, canPaste));
        menu.Items.Add(Item("New folder", NewFolder, InRealFolder));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open in Terminal", OpenTerminal, _current != ThisPC));
        menu.Items.Add(Item("Open in Windows Explorer", () =>
            Process.Start("explorer.exe", _current == ThisPC ? "shell:MyComputerFolder" : $"\"{_current}\""), _current != null));
        menu.Items.Add(Item("Properties", () => ShowPropertiesFor([]), _current != ThisPC));
        menu.IsOpen = true;
    }

    void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        _dragStart = e.GetPosition(list);
        _dragItem = ItemAt(list, e.OriginalSource);
        _clickSelect = null;
        // Clicking an already-selected item in a multi-selection keeps the selection so it can be dragged.
        if (_dragItem != null && e.ClickCount == 1 && list.SelectedItems.Count > 1 && list.SelectedItems.Contains(_dragItem)
            && Keyboard.Modifiers == ModifierKeys.None)
        {
            _clickSelect = _dragItem;
            e.Handled = true;
        }
    }

    void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_clickSelect != null)
        {
            var list = (ListBox)sender;
            var item = _clickSelect;
            list.SelectedItem = item;
            (list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem)?.Focus();
        }
        _clickSelect = null;
        _dragItem = null;
    }

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var list = (ListBox)sender;
        var p = e.GetPosition(list);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var items = list.SelectedItems.Contains(_dragItem) ? Selected() : [_dragItem];
        _dragItem = null;
        _clickSelect = null;
        var paths = items.Where(i => !i.IsDrive).Select(i => i.FullPath).ToArray();
        if (paths.Length == 0) return;
        try { DragDrop.DoDragDrop(list, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
        catch { }
    }

    string? DropTarget(ListBox list, DragEventArgs e)
    {
        var over = ItemAt(list, e.OriginalSource);
        if (over != null && over.IsFolder) return over.FullPath;
        return InRealFolder ? _current : null;
    }

    static bool DropIsMove(DragEventArgs e, string[] files, string target)
    {
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) return false;
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) return true;
        return files.All(f => PathCmp.Equals(Path.GetPathRoot(f), Path.GetPathRoot(target)));
    }

    void List_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var target = DropTarget((ListBox)sender, e);
        if (target == null) return;
        if (files.Any(f => PathEq(f, target))) return;                          // onto itself
        if (files.All(f => PathEq(Path.GetDirectoryName(f), target))) return;  // already there
        e.Effects = DropIsMove(e, files, target) ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    void List_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var target = DropTarget((ListBox)sender, e);
        if (target == null || files.Any(f => PathEq(f, target)) || files.All(f => PathEq(Path.GetDirectoryName(f), target))) return;
        bool move = DropIsMove(e, files, target);
        FileOps.Run(move ? FO_MOVE : FO_COPY, files, target, FOF_ALLOWUNDO, Hwnd, background: true);
        e.Effects = move ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    void List_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        if (_details) { if (e.Delta > 0) SetViewMode(false); return; }
        ThumbSize = e.Delta > 0 ? Math.Max(ThumbSize + 8, ThumbSize * 1.12) : Math.Min(ThumbSize - 8, ThumbSize / 1.12);
    }

    void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FileItem item) item.Realized++;
    }

    void Tile_Unloaded(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FileItem item)
        {
            item.Realized = Math.Max(0, item.Realized - 1);
            if (item.Realized == 0) item.CancelRequest(false);
        }
    }

    void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) { _ = GoBack(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { _ = GoForward(); e.Handled = true; }
    }

    // =====================================================================
    // Keyboard
    // =====================================================================

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool inText = Keyboard.FocusedElement is TextBox;
        bool ctrl = mods == ModifierKeys.Control, alt = mods == ModifierKeys.Alt, shift = mods == ModifierKeys.Shift, none = mods == ModifierKeys.None;
        bool handled = true;

        if (alt && key == Key.Left || key == Key.BrowserBack) _ = GoBack();
        else if (alt && key == Key.Right || key == Key.BrowserForward) _ = GoForward();
        else if (alt && key == Key.Up) _ = GoUp();
        else if (alt && key == Key.D || ctrl && key == Key.L) { AddressBox.Focus(); AddressBox.SelectAll(); }
        else if (alt && key == Key.Enter) ShowPropertiesFor(Selected());
        else if (none && key == Key.F5 || ctrl && key == Key.R) _ = Reload();
        else if (ctrl && key == Key.F || none && key == Key.F3) { SearchBox.Focus(); SearchBox.SelectAll(); }
        else if (ctrl && key == Key.N) new MainWindow(_current).Show();
        else if (ctrl && key == Key.W) Close();
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.N) NewFolder();
        else if (ctrl && (key == Key.OemPlus || key == Key.Add)) ThumbSize *= 1.15;
        else if (ctrl && (key == Key.OemMinus || key == Key.Subtract)) ThumbSize /= 1.15;
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.D1) SetViewMode(false);
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.D6) SetViewMode(true);
        else if (inText) handled = false;
        else if (none && key == Key.Back) _ = GoBack();
        else if (none && key == Key.Enter) OpenItems(Selected());
        else if (none && key == Key.F2) RenameSelected();
        else if (none && key == Key.Delete) DeleteSelected(false);
        else if (shift && key == Key.Delete) DeleteSelected(true);
        else if (ctrl && key == Key.C) CopySelected(false);
        else if (ctrl && key == Key.X) CopySelected(true);
        else if (ctrl && key == Key.V) Paste();
        else if (none && key == Key.Escape) ActiveList.UnselectAll();
        else handled = false;

        if (handled) e.Handled = true;
    }

    void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = Navigate(AddressBox.Text).ContinueWith(_ => Dispatcher.BeginInvoke(() => ActiveList.Focus()));
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            UpdateChrome();
            ActiveList.Focus();
        }
    }

    void TextBox_SelectAllOnFocus(object sender, KeyboardFocusChangedEventArgs e) => ((TextBox)sender).SelectAll();

    void TextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var tb = (TextBox)sender;
        if (!tb.IsKeyboardFocusWithin) { tb.Focus(); e.Handled = true; }
    }

    // =====================================================================
    // Toolbar handlers
    // =====================================================================

    void Back_Click(object sender, RoutedEventArgs e) => _ = GoBack();
    void Forward_Click(object sender, RoutedEventArgs e) => _ = GoForward();
    void Up_Click(object sender, RoutedEventArgs e) => _ = GoUp();
    void Refresh_Click(object sender, RoutedEventArgs e) => _ = Reload();
    void NewFolder_Click(object sender, RoutedEventArgs e) => NewFolder();
    void Cut_Click(object sender, RoutedEventArgs e) => CopySelected(true);
    void Copy_Click(object sender, RoutedEventArgs e) => CopySelected(false);
    void Paste_Click(object sender, RoutedEventArgs e) => Paste();
    void Rename_Click(object sender, RoutedEventArgs e) => RenameSelected();
    void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected((Keyboard.Modifiers & ModifierKeys.Shift) != 0);
    void Terminal_Click(object sender, RoutedEventArgs e) => OpenTerminal();
    void GridToggle_Click(object sender, RoutedEventArgs e) => SetViewMode(false);
    void DetailsToggle_Click(object sender, RoutedEventArgs e) => SetViewMode(true);

    void SetSort(string by, bool desc)
    {
        _sortBy = by;
        _sortDesc = desc;
        UpdateSortDirGlyph();
        var match = SortBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == by);
        if (match != null && !ReferenceEquals(SortBox.SelectedItem, match)) SortBox.SelectedItem = match;
        if (_searchResults == null) ApplyView();
    }

    void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedItem is ComboBoxItem { Tag: string by } && by != _sortBy) SetSort(by, _sortDesc);
    }

    void SortDir_Click(object sender, RoutedEventArgs e) => SetSort(_sortBy, !_sortDesc);

    void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is GridViewColumnHeader { Tag: string by }) SetSort(by, by == _sortBy ? !_sortDesc : false);
    }

    void HiddenCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) _ = Reload();
    }

    void ExtCheck_Changed(object sender, RoutedEventArgs e)
    {
        FileItem.ShowExtensions = ExtCheck.IsChecked == true;
        foreach (var i in _shown) i.RefreshName();
    }

    // =====================================================================
    // Folder tree
    // =====================================================================

    static readonly object Dummy = new();

    void BuildTree()
    {
        _folderIcon = ShellImages.Get(Path.GetTempPath().TrimEnd('\\'), (int)Math.Round(16 * _dpi), true, _dpi);

        var quick = new TreeViewItem { Header = Header(null, "Quick access", ""), IsExpanded = true };
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var places = new (string? Path, string Name)[]
        {
            (home, Path.GetFileName(home)),
            (Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Desktop"),
            (KnownFolder(FOLDERID_Downloads), "Downloads"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pictures"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Music"),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Videos"),
        };
        foreach (var (p, n) in places)
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) quick.Items.Add(FolderNode(p, n, OwnIcon(p)));
        Tree.Items.Add(quick);

        var pc = new TreeViewItem { Header = Header(null, ThisPC, ""), Tag = ThisPC, IsExpanded = true };
        foreach (var d in DriveInfo.GetDrives())
        {
            try { var fi = FileItem.FromDrive(d); pc.Items.Add(FolderNode(fi.FullPath, fi.Name, OwnIcon(fi.FullPath))); } catch { }
        }
        Tree.Items.Add(pc);
    }

    ImageSource? OwnIcon(string path) => ShellImages.Get(path, (int)Math.Round(16 * _dpi), true, _dpi) ?? _folderIcon;

    object Header(ImageSource? icon, string text, string? glyph = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null) sp.Children.Add(new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 7, 0) });
        else if (glyph != null) sp.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    TreeViewItem FolderNode(string path, string name, ImageSource? icon)
    {
        var node = new TreeViewItem { Header = Header(icon, name), Tag = path };
        node.Items.Add(Dummy);
        node.Expanded += Node_Expanded;
        return node;
    }

    async void Node_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != sender) return;
        var node = (TreeViewItem)sender;
        if (node.Items.Count != 1 || node.Items[0] != Dummy) return;
        node.Items.Clear();
        var path = (string)node.Tag;
        bool showHidden = HiddenCheck.IsChecked == true;
        var dirs = await Task.Run(() =>
        {
            try
            {
                var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System };
                var l = new DirectoryInfo(path).EnumerateDirectories("*", opts).Select(d => (d.FullName, d.Name)).ToList();
                l.Sort((a, b) => StrCmpLogicalW(a.Name, b.Name));
                return l;
            }
            catch { return new List<(string FullName, string Name)>(); }
        });
        foreach (var d in dirs) node.Items.Add(FolderNode(d.FullName, d.Name, _folderIcon));
    }

    void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!_ready) return;
        if (Tree.SelectedItem is TreeViewItem { Tag: string p } && !PathEq(p, _current)) _ = Navigate(p);
    }

    void Tree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Clicking the already-selected node navigates back to it.
        var d = e.OriginalSource as DependencyObject;
        while (d != null && d is not TreeViewItem && d is not ToggleButton) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is TreeViewItem { IsSelected: true, Tag: string p } && !PathEq(p, _current)) _ = Navigate(p);
    }

    // =====================================================================

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } r) return r;
        }
        return null;
    }
}

internal static class InputDialog
{
    public static string? Show(Window owner, string title, string prompt, string initial, bool selectStem)
    {
        var w = new Window
        {
            Title = title, Owner = owner, Width = 440, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 18) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => w.DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = prompt });
        root.Children.Add(box);
        root.Children.Add(buttons);
        w.Content = root;
        w.Loaded += (_, _) =>
        {
            box.Focus();
            int len = selectStem ? Path.GetFileNameWithoutExtension(initial).Length : initial.Length;
            box.Select(0, len > 0 ? len : initial.Length);
        };
        return w.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
