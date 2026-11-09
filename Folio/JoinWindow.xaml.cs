using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace Folio;

/// <summary>Joins two or more images edge to edge into one seamless image: a row (side by side)
/// or a column (stacked). Images are scaled so the shared edges match exactly, so there's never
/// any background showing. Drag an image to reorder; it snaps into its slot on release.</summary>
public partial class JoinWindow : Window
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".jxr", ".wdp", ".ico",
    };

    public static bool IsImage(string path) => ImageExtensions.Contains(IOPath.GetExtension(path));

    sealed class Piece
    {
        public required string Path { get; init; }
        public required BitmapSource Full { get; init; }      // full resolution, EXIF orientation applied
        public required Image View { get; init; }
        public double X, Y, Scale = 1;                          // position/size in output pixels
        public double W => Full.PixelWidth * Scale;
        public double H => Full.PixelHeight * Scale;
    }

    const double Pad = 28;

    readonly List<string> _paths;
    List<Piece> _order = new();     // current join order
    bool _stacked;
    double _viewScale = 1;
    Point _viewOrigin;
    Piece? _drag;
    Point _grab;
    int _dropIndex;

    /// <summary>The file that was saved, if any.</summary>
    public string? SavedPath { get; private set; }

    public JoinWindow(IEnumerable<string> paths)
    {
        InitializeComponent();
        _paths = paths.ToList();
        Loaded += async (_, _) => await LoadAsync();
        Stage.SizeChanged += (_, _) => { if (_drag == null) Refit(animate: false); };
    }

    // =====================================================================
    // Loading
    // =====================================================================

    async Task LoadAsync()
    {
        SetBusy($"Loading {_paths.Count} images…");
        var errors = new List<string>();
        var loaded = await Task.Run(() =>
        {
            var list = new List<(string Path, BitmapSource Full, BitmapSource Preview)>();
            foreach (var p in _paths)
            {
                try
                {
                    var full = LoadOriented(p);
                    list.Add((p, full, MakePreview(full)));
                }
                catch (Exception ex) { errors.Add($"{IOPath.GetFileName(p)}: {ex.Message}"); }
            }
            return list;
        });

        if (errors.Count > 0)
            MessageBox.Show(this, "These files couldn't be opened as images and were left out:\n\n" + string.Join("\n", errors),
                "Join images", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (loaded.Count < 2)
        {
            MessageBox.Show(this, "Joining needs at least two images that can be opened.", "Join images", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
            return;
        }

        foreach (var (path, full, preview) in loaded)
        {
            var img = new Image { Source = preview, Stretch = Stretch.Fill, Cursor = Cursors.SizeAll, ToolTip = IOPath.GetFileName(path) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var piece = new Piece { Path = path, Full = full, View = img };
            img.Tag = piece;
            img.MouseLeftButtonDown += Piece_MouseDown;
            img.MouseMove += Piece_MouseMove;
            img.MouseLeftButtonUp += (s, _) => ((Image)s).ReleaseMouseCapture();
            img.LostMouseCapture += (_, _) => EndDrag();
            Board.Children.Add(img);
            _order.Add(piece);
        }

        // Default to a row for landscape-ish sets, a column if they're mostly wide images.
        _stacked = _order.Count(p => p.Full.PixelWidth > p.Full.PixelHeight * 1.3) > _order.Count / 2;
        Layout(_order);
        Refit(animate: false);
        SetBusy(null);
    }

    /// <summary>Decode an image fully into memory (so the file isn't locked) and apply its EXIF orientation.</summary>
    static BitmapSource LoadOriented(string path)
    {
        BitmapFrame frame;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }

        ushort orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata md && md.GetQuery("System.Photo.Orientation") is ushort o) orientation = o;
        }
        catch { }

        BitmapSource src = frame;
        if (orientation is 2 or 4 or 5 or 7) // mirrored variants
            src = new TransformedBitmap(src, new ScaleTransform(orientation is 4 ? 1 : -1, orientation is 4 ? -1 : 1));
        // after the horizontal mirror: 5 = transpose (mirror + 270°), 7 = transverse (mirror + 90°)
        double angle = orientation switch { 3 => 180, 6 or 7 => 90, 5 or 8 => 270, _ => 0 };
        if (angle != 0) src = new TransformedBitmap(src, new RotateTransform(angle));

        var wb = new WriteableBitmap(new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0));
        wb.Freeze();
        return wb;
    }

    static BitmapSource MakePreview(BitmapSource full)
    {
        double s = Math.Min(1.0, 2048.0 / Math.Max(full.PixelWidth, full.PixelHeight));
        if (s >= 1) return full;
        var wb = new WriteableBitmap(new TransformedBitmap(full, new ScaleTransform(s, s)));
        wb.Freeze();
        return wb;
    }

    void SetBusy(string? text)
    {
        BusyText.Text = text ?? "";
        BusyText.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        Toolbar.IsEnabled = SaveButton.IsEnabled = DeleteCheck.IsEnabled = text == null;
        Board.IsHitTestVisible = text == null;
    }

    // =====================================================================
    // Layout: a seamless strip
    // =====================================================================

    /// <summary>Scale every image to the strip's common height (row) or width (column) and
    /// place them back to back in the given order.</summary>
    void Layout(IList<Piece> order, Piece? skip = null, int gapAt = -1, double gapSize = 0)
    {
        // Common edge = the largest one, so no image loses resolution.
        double edge = _stacked ? _order.Max(p => p.Full.PixelWidth) : _order.Max(p => p.Full.PixelHeight);
        foreach (var p in _order) p.Scale = edge / (_stacked ? p.Full.PixelWidth : p.Full.PixelHeight);

        double pos = 0;
        int i = 0;
        foreach (var p in order)
        {
            if (p == skip) continue;
            if (i == gapAt) pos += gapSize;
            if (_stacked) { p.X = 0; p.Y = pos; pos += Math.Round(p.H); }
            else { p.X = pos; p.Y = 0; pos += Math.Round(p.W); }
            i++;
        }
    }

    Size ResultSize()
    {
        double along = _order.Sum(p => Math.Round(_stacked ? p.H : p.W));
        double across = Math.Round(_stacked ? _order[0].W : _order[0].H);
        return _stacked ? new Size(across, along) : new Size(along, across);
    }

    /// <summary>Zoom and center so the whole result fits the window.</summary>
    void Refit(bool animate)
    {
        if (_order.Count == 0) return;
        double aw = Stage.ActualWidth - 2 * Pad, ah = Stage.ActualHeight - 2 * Pad;
        if (aw <= 0 || ah <= 0) return;
        var size = ResultSize();
        _viewScale = Math.Min(1.0, Math.Min(aw / size.Width, ah / size.Height));
        _viewOrigin = new Point(-(Stage.ActualWidth / _viewScale - size.Width) / 2, -(Stage.ActualHeight / _viewScale - size.Height) / 2);
        foreach (var p in _order) Place(p, animate);
        SizeText.Text = $"{_order.Count} images   Result: {size.Width:N0} × {size.Height:N0} px";
    }

    Point ToScreen(double x, double y) => new((x - _viewOrigin.X) * _viewScale, (y - _viewOrigin.Y) * _viewScale);
    Point ToDoc(Point s) => new(s.X / _viewScale + _viewOrigin.X, s.Y / _viewScale + _viewOrigin.Y);

    void Place(Piece p, bool animate)
    {
        var s = ToScreen(p.X, p.Y);
        p.View.Width = Math.Max(1, p.W * _viewScale);
        p.View.Height = Math.Max(1, p.H * _viewScale);
        if (animate)
        {
            var dur = new Duration(TimeSpan.FromMilliseconds(140));
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            p.View.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(s.X, dur) { EasingFunction = ease });
            p.View.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(s.Y, dur) { EasingFunction = ease });
        }
        else
        {
            p.View.BeginAnimation(Canvas.LeftProperty, null);
            p.View.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetLeft(p.View, s.X);
            Canvas.SetTop(p.View, s.Y);
        }
    }

    // =====================================================================
    // Drag to reorder
    // =====================================================================

    void Piece_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var img = (Image)sender;
        var p = (Piece)img.Tag;
        _drag = p;
        _dropIndex = _order.IndexOf(p);
        var m = ToDoc(e.GetPosition(Board));
        _grab = new Point(m.X - p.X, m.Y - p.Y);
        Panel.SetZIndex(img, 1);
        img.Opacity = 0.85;
        img.CaptureMouse();
        e.Handled = true;
    }

    void Piece_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == null || !ReferenceEquals(((Image)sender).Tag, _drag)) return;
        var m = ToDoc(e.GetPosition(Board));

        // The dragged image follows the mouse along the strip (and a little across it, for feel).
        double along = (_stacked ? m.Y - _grab.Y : m.X - _grab.X);
        double across = (_stacked ? m.X - _grab.X : m.Y - _grab.Y);
        across = Math.Clamp(across, -(_stacked ? _drag.W : _drag.H) * 0.15, (_stacked ? _drag.W : _drag.H) * 0.15);
        if (_stacked) { _drag.Y = along; _drag.X = across; } else { _drag.X = along; _drag.Y = across; }
        Place(_drag, animate: false);

        // Which slot is it over? Count the other images whose middle it has passed.
        double center = along + (_stacked ? _drag.H : _drag.W) / 2;
        var others = _order.Where(p => p != _drag).ToList();
        double pos = 0;
        int slot = 0;
        foreach (var o in others)
        {
            double size = Math.Round(_stacked ? o.H : o.W);
            if (center > pos + size / 2) slot++;
            pos += size;
        }

        if (slot != _dropIndex)
        {
            _dropIndex = slot;
            Layout(_order, skip: _drag, gapAt: slot, gapSize: Math.Round(_stacked ? _drag.H : _drag.W));
            foreach (var o in others) Place(o, animate: true);
        }
    }

    void EndDrag()
    {
        if (_drag == null) return;
        var p = _drag;
        _drag = null;
        p.View.Opacity = 1;
        Panel.SetZIndex(p.View, 0);
        _order.Remove(p);
        _order.Insert(Math.Clamp(_dropIndex, 0, _order.Count), p);
        Layout(_order);
        Refit(animate: true); // dragged image snaps into its slot
    }

    // =====================================================================
    // Toolbar
    // =====================================================================

    void SideBySide_Click(object sender, RoutedEventArgs e) { _stacked = false; Layout(_order); Refit(animate: false); }
    void Stacked_Click(object sender, RoutedEventArgs e) { _stacked = true; Layout(_order); Refit(animate: false); }
    void Reverse_Click(object sender, RoutedEventArgs e) { _order.Reverse(); Layout(_order); Refit(animate: true); }

    // =====================================================================
    // Saving
    // =====================================================================

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_order.Count < 2) return;
        string dir = IOPath.GetDirectoryName(_order[0].Path)!;
        bool allJpeg = _order.All(p => IOPath.GetExtension(p.Path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".jfif");
        string ext = allJpeg ? ".jpg" : ".png";
        var dlg = new SaveFileDialog
        {
            Title = "Save joined image",
            InitialDirectory = dir,
            FileName = UniqueName(dir, "Joined image", ext),
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            FilterIndex = allJpeg ? 2 : 1,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        string path = dlg.FileName;
        bool jpeg = IOPath.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";
        var parts = _order.Select(p => (p.Full, p.X, p.Y, p.Scale)).ToList();
        bool stacked = _stacked;

        SetBusy("Saving…");
        try { await Task.Run(() => Compose(parts, stacked, path, jpeg)); }
        catch (Exception ex)
        {
            SetBusy(null);
            MessageBox.Show(this, "Couldn't save the joined image:\n\n" + ex.Message, "Join images", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (DeleteCheck.IsChecked == true)
        {
            var originals = _order.Select(p => p.Path)
                .Where(p => !string.Equals(IOPath.GetFullPath(p), IOPath.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (originals.Length > 0)
                FileOps.Run(Native.FO_DELETE, originals, null, Native.FOF_ALLOWUNDO, new WindowInteropHelper(this).Handle, background: false);
        }

        SavedPath = path;
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); } // not shown as a dialog
    }

    static string UniqueName(string dir, string stem, string ext)
    {
        string name = stem + ext;
        for (int n = 2; File.Exists(IOPath.Combine(dir, name)); n++) name = $"{stem} ({n}){ext}";
        return name;
    }

    /// <summary>Scale each image to the shared edge and write them back to back into one bitmap.
    /// The canvas is exactly the size of the images, so nothing but image pixels ends up in the file.</summary>
    internal static void Compose(List<(BitmapSource Full, double X, double Y, double Scale)> parts, bool stacked, string path, bool jpeg)
    {
        var scaled = parts.Select(p =>
        {
            BitmapSource s = p.Full;
            if (Math.Abs(p.Scale - 1) > 1e-6) s = new TransformedBitmap(s, new ScaleTransform(p.Scale, p.Scale));
            if (s.Format != PixelFormats.Pbgra32) s = new FormatConvertedBitmap(s, PixelFormats.Pbgra32, null, 0);
            return s;
        }).ToList();

        // Shared edge: rounding can leave a scaled image a pixel off, so crop everything to the smallest.
        int across = stacked ? scaled.Min(s => s.PixelWidth) : scaled.Min(s => s.PixelHeight);
        long along = scaled.Sum(s => (long)(stacked ? s.PixelHeight : s.PixelWidth));
        long width = stacked ? across : along, height = stacked ? along : across;
        if (width * height * 4 > int.MaxValue - 1024)
            throw new InvalidOperationException($"The result would be {width:N0} × {height:N0} pixels, which is too large to save. Try joining fewer images.");
        int W = (int)width, H = (int)height, stride = W * 4;
        var buf = new byte[stride * H];

        int offset = 0;
        bool anyAlpha = false;
        foreach (var src in scaled)
        {
            int pw = stacked ? across : src.PixelWidth, ph = stacked ? src.PixelHeight : across, ps = pw * 4;
            var pix = new byte[ps * ph];
            src.CopyPixels(new Int32Rect(0, 0, pw, ph), pix, ps, 0);
            int dx = stacked ? 0 : offset, dy = stacked ? offset : 0;
            for (int r = 0; r < ph; r++)
                Buffer.BlockCopy(pix, r * ps, buf, (dy + r) * stride + dx * 4, ps);
            if (!anyAlpha) for (int i = 3; i < pix.Length; i += 4) if (pix[i] != 255) { anyAlpha = true; break; }
            offset += stacked ? ph : pw;
        }

        BitmapSource result = BitmapSource.Create(W, H, 96, 96, PixelFormats.Pbgra32, null, buf, stride);
        BitmapEncoder encoder;
        if (jpeg)
        {
            if (anyAlpha) // JPEG can't store transparency: flatten see-through pixels onto white
            {
                for (int i = 0; i < buf.Length; i += 4)
                {
                    int inv = 255 - buf[i + 3];
                    buf[i] = (byte)(buf[i] + inv); buf[i + 1] = (byte)(buf[i + 1] + inv); buf[i + 2] = (byte)(buf[i + 2] + inv); buf[i + 3] = 255;
                }
                result = BitmapSource.Create(W, H, 96, 96, PixelFormats.Pbgra32, null, buf, stride);
            }
            encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            result = new FormatConvertedBitmap(result, PixelFormats.Bgr24, null, 0);
        }
        else
        {
            encoder = new PngBitmapEncoder();
            result = new FormatConvertedBitmap(result, anyAlpha ? PixelFormats.Bgra32 : PixelFormats.Bgr24, null, 0);
        }
        encoder.Frames.Add(BitmapFrame.Create(result));

        string tmp = path + ".folio-tmp";
        using (var fs = File.Create(tmp)) encoder.Save(fs);
        File.Move(tmp, path, overwrite: true);
    }
}
