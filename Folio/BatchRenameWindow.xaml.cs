using System.Windows;
using System.Windows.Media;
using IOPath = System.IO.Path;

namespace Folio;

/// <summary>Rename many items at once: a new name with an optional running number, or random names.
/// Shows a live preview, refuses clashes up front, and renames in two passes so swaps work
/// and a failure part-way through can be rolled back.</summary>
public partial class BatchRenameWindow : Window
{
    public sealed class Row
    {
        public required string Old { get; init; }
        public required string New { get; init; }
        public string Status { get; init; } = "";
        public bool IsProblem { get; init; }
        public bool Unchanged { get; init; }
        public FontWeight Weight => Unchanged ? FontWeights.Normal : FontWeights.SemiBold;
        public Brush StatusBrush => IsProblem ? Brushes.IndianRed : Brushes.Gray;
    }

    readonly List<FileItem> _selected;
    readonly List<FileItem>? _folderItems;
    readonly Dictionary<string, string> _random = new(StringComparer.OrdinalIgnoreCase);
    List<(FileItem Item, string NewPath)> _plan = new();
    bool _ready;

    /// <summary>Full paths of the items after renaming.</summary>
    public List<string> RenamedPaths { get; } = new();

    /// <param name="selected">Selected items, in display order.</param>
    /// <param name="folderItems">Everything in the current folder in display order, or null when not browsing a single folder.</param>
    public BatchRenameWindow(IList<FileItem> selected, IList<FileItem>? folderItems)
    {
        InitializeComponent();
        _selected = selected.Where(i => !i.IsDrive).ToList();
        _folderItems = folderItems?.Where(i => !i.IsDrive).ToList();

        SelectedScope.Content = $"Selected items ({_selected.Count})";
        SelectedScope.IsEnabled = _selected.Count > 0;
        int folderFiles = _folderItems?.Count(i => !i.IsFolder) ?? 0;
        FolderScope.Content = _folderItems == null ? "Everything in this folder" : $"Everything in this folder ({folderFiles} files)";
        FolderScope.IsEnabled = _folderItems != null;
        if (_selected.Count == 0) FolderScope.IsChecked = true;

        if (_selected.Count == 1)
        {
            var one = _selected[0];
            NameBox.Text = one.IsFolder ? one.Name : IOPath.GetFileNameWithoutExtension(one.Name);
            NumberCheck.IsChecked = false;
        }

        Loaded += (_, _) =>
        {
            _ready = true;
            Update();
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    IEnumerable<FileItem> Targets()
    {
        if (FolderScope.IsChecked == true && _folderItems != null)
            return _folderItems.Where(i => IncludeFolders.IsChecked == true || !i.IsFolder);
        return _selected;
    }

    void Changed(object sender, RoutedEventArgs e) => Update();

    void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        _random.Clear();
        RandomMode.IsChecked = true;
        Update();
    }

    static string RandomName()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(12, 0, (span, _) => { for (int i = 0; i < span.Length; i++) span[i] = chars[Random.Shared.Next(chars.Length)]; });
    }

    string NumberedName(string name, int n, int digits)
    {
        string num = n.ToString().PadLeft(digits, '0');
        if (name.Length == 0) return num;
        return StyleBox.SelectedIndex switch
        {
            1 => $"{name} {num}",
            2 => $"{name}_{num}",
            3 => $"{name}-{num}",
            4 => $"{name}{num}",
            _ => $"{name} ({num})",
        };
    }

    void Update()
    {
        if (!_ready) return;
        bool random = RandomMode.IsChecked == true;
        NamePanel.IsEnabled = !random;
        RandomPanel.IsEnabled = random;
        IncludeFolders.IsEnabled = FolderScope.IsChecked == true;

        var items = Targets().ToList();
        HeaderText.Text = items.Count == 1 ? "Rename 1 item" : $"Rename {items.Count:N0} items";

        string name = NameBox.Text.Trim().TrimEnd('.');
        bool numbered = NumberCheck.IsChecked == true;
        string? globalProblem = null;
        if (!random)
        {
            if (name.IndexOfAny(IOPath.GetInvalidFileNameChars()) >= 0)
                globalProblem = "A name can't contain any of these characters: \\ / : * ? \" < > |";
            else if (name.Length == 0 && !numbered)
                globalProblem = "Type a new name, or turn on numbering.";
        }
        if (!int.TryParse(StartBox.Text.Trim(), out int start) || start < 0)
        {
            if (!random && numbered) globalProblem ??= "\"Start at\" needs to be a whole number.";
            start = 1;
        }
        int digits = PadBox.SelectedIndex + 1;

        // Work out every new name.
        var proposed = new List<(FileItem Item, string NewName)>();
        int n = start;
        foreach (var it in items)
        {
            string ext = it.IsFolder ? "" : IOPath.GetExtension(it.Name);
            string stem;
            if (random)
            {
                if (!_random.TryGetValue(it.FullPath, out var r)) _random[it.FullPath] = r = RandomName();
                stem = r;
            }
            else stem = numbered ? NumberedName(name, n++, digits) : name;
            proposed.Add((it, stem + ext));
        }

        // Check for clashes: two items getting the same name, or a name already taken by
        // something that isn't being renamed.
        var sources = new HashSet<string>(items.Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
        var targetCounts = proposed
            .GroupBy(p => IOPath.Combine(IOPath.GetDirectoryName(p.Item.FullPath)!, p.NewName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<Row>(proposed.Count);
        var plan = new List<(FileItem, string)>();
        int problems = 0, changes = 0;
        foreach (var (it, newName) in proposed)
        {
            string target = IOPath.Combine(IOPath.GetDirectoryName(it.FullPath)!, newName);
            string? problem = globalProblem;
            bool same = string.Equals(newName, it.Name, StringComparison.Ordinal);
            if (problem == null && !same)
            {
                if (targetCounts[target] > 1) problem = "Same name as another item";
                else if (!sources.Contains(target) && (File.Exists(target) || Directory.Exists(target))) problem = "Name already taken";
                else if (target.Length >= 32000) problem = "Name too long";
            }
            if (problem != null) problems++;
            else if (!same) { changes++; plan.Add((it, target)); }
            rows.Add(new Row
            {
                Old = it.Name,
                New = globalProblem != null ? "" : newName,
                Status = problem ?? (same ? "Unchanged" : ""),
                IsProblem = problem != null,
                Unchanged = same,
            });
        }
        _plan = plan;
        PreviewList.ItemsSource = rows;

        RenameButton.IsEnabled = problems == 0 && changes > 0;
        if (problems > 0 || globalProblem != null) SummaryText.Foreground = Brushes.IndianRed;
        else SummaryText.ClearValue(ForegroundProperty); // theme default (works in light and dark)
        SummaryText.Text = globalProblem
            ?? (problems > 0 ? $"{problems} name{(problems == 1 ? "" : "s")} can't be used. Fix them to continue."
            : changes == 0 ? "Nothing to rename."
            : $"{changes:N0} item{(changes == 1 ? "" : "s")} will be renamed.");
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_plan.Count == 0) return;
        var moves = _plan.Select(p => (Orig: p.Item.FullPath, Final: p.NewPath, Folder: p.Item.IsFolder)).ToList();

        // Pass 1: move everything to unique temporary names, so names can be swapped freely.
        var temps = new List<(string Orig, string Temp, string Final, bool Folder)>();
        try
        {
            foreach (var m in moves)
            {
                string temp = IOPath.Combine(IOPath.GetDirectoryName(m.Orig)!, $".folio-rename-{Guid.NewGuid():N}");
                Move(m.Orig, temp, m.Folder);
                temps.Add((m.Orig, temp, m.Final, m.Folder));
            }
        }
        catch (Exception ex)
        {
            var stuck = Rollback(temps.Select(t => (t.Temp, t.Orig, t.Folder)));
            Fail($"Couldn't rename \"{IOPath.GetFileName(moves[temps.Count].Orig)}\": {ex.Message}", stuck);
            return;
        }

        // Pass 2: move each temporary name to its final name.
        var finished = new List<(string Final, string Temp, bool Folder)>();
        try
        {
            foreach (var t in temps)
            {
                Move(t.Temp, t.Final, t.Folder);
                finished.Add((t.Final, t.Temp, t.Folder));
            }
        }
        catch (Exception ex)
        {
            var stuck = Rollback(finished.Select(f => (f.Final, f.Temp, f.Folder)));
            stuck.AddRange(Rollback(temps.Select(t => (t.Temp, t.Orig, t.Folder))));
            Fail($"Couldn't finish renaming: {ex.Message}", stuck);
            return;
        }

        RenamedPaths.AddRange(moves.Select(m => m.Final));
        DialogResult = true;
    }

    static void Move(string from, string to, bool folder)
    {
        if (folder) Directory.Move(from, to);
        else File.Move(from, to);
    }

    /// <summary>Undo moves in reverse order. Returns any paths that couldn't be put back.</summary>
    static List<string> Rollback(IEnumerable<(string Current, string Back, bool Folder)> moves)
    {
        var stuck = new List<string>();
        foreach (var (current, back, folder) in moves.Reverse())
        {
            try { if (File.Exists(current) || Directory.Exists(current)) Move(current, back, folder); }
            catch { stuck.Add($"{current}  (should be {IOPath.GetFileName(back)})"); }
        }
        return stuck;
    }

    void Fail(string message, List<string> stuck)
    {
        string text = message + "\n\nNothing was renamed; everything has been put back the way it was.";
        if (stuck.Count > 0)
            text = message + "\n\nMost changes were undone, but these couldn't be put back:\n" + string.Join("\n", stuck.Take(10));
        MessageBox.Show(this, text, "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
        Update();
    }
}
