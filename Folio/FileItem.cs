using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Folio;

public sealed class FileItem : INotifyPropertyChanged
{
    // Shared view state (set by the window)
    public static int ThumbPixels = 256;
    public static int IconPixels = 20;
    public static bool ShowExtensions = true;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsFolder { get; init; }
    public bool IsDrive { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public FileAttributes Attributes { get; init; }
    public string TypeName { get; init; } = "";
    public string? Extra { get; init; }

    public string DisplayName
    {
        get
        {
            if (IsFolder || ShowExtensions) return Name;
            var stem = Path.GetFileNameWithoutExtension(Name);
            return stem.Length > 0 ? stem : Name;
        }
    }
    public string SizeText => IsDrive ? Extra ?? "" : IsFolder ? "" : FormatSize(Size);
    public string ModifiedText => Modified == default ? "" : Modified.ToString("g");
    public double ItemOpacity => (Attributes & FileAttributes.Hidden) != 0 ? 0.55 : 1.0;
    public string ToolTipText
    {
        get
        {
            if (IsDrive) return $"{Name}\n{Extra}";
            var s = $"{Name}\nType: {TypeName}";
            if (!IsFolder) s += $"\nSize: {SizeText}";
            s += $"\nDate modified: {ModifiedText}\nLocation: {Path.GetDirectoryName(FullPath)}";
            return s;
        }
    }

    // ---- Large thumbnail (grid view), loaded lazily when a tile binds to it ----
    ImageSource? _thumb;
    int _thumbPx, _failedPx;
    volatile int _pendingPx;
    internal int PendingPx => _pendingPx;
    internal LinkedListNode<FileItem>? LruNode;
    internal long ThumbBytes;
    internal int Realized;

    public ImageSource? Thumbnail
    {
        get
        {
            int want = ThumbPixels;
            bool needs = _thumb == null || _thumbPx < want || _thumbPx > want * 2;
            if (needs && _pendingPx != want && _failedPx != want)
            {
                _pendingPx = want;
                App.Loader.Request(this, want, false);
            }
            return _thumb;
        }
    }

    // ---- Small icon (details view, tree) ----
    ImageSource? _icon;
    bool _iconRequested;
    public ImageSource? Icon
    {
        get
        {
            if (!_iconRequested) { _iconRequested = true; App.Loader.Request(this, IconPixels, true); }
            return _icon;
        }
    }

    internal void Deliver(BitmapSource? img, int px, bool icon)
    {
        if (icon) { _icon = img; Changed(nameof(Icon)); return; }
        if (_pendingPx != px) return; // superseded
        _pendingPx = 0;
        if (img == null) { _failedPx = px; return; }
        _thumb = img;
        _thumbPx = px;
        ThumbCache.Add(this, (long)img.PixelWidth * img.PixelHeight * 4);
        Changed(nameof(Thumbnail));
    }

    internal void CancelRequest(bool icon)
    {
        if (icon) _iconRequested = false; else _pendingPx = 0;
    }

    public void RefreshThumb() => Changed(nameof(Thumbnail));
    public void RefreshName() => Changed(nameof(DisplayName));

    internal void DropThumb()
    {
        _thumb = null; _thumbPx = 0; _pendingPx = 0; ThumbBytes = 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ---- Construction ----
    public static FileItem FromInfo(FileSystemInfo fsi)
    {
        bool dir = fsi is DirectoryInfo;
        long size = 0;
        if (!dir) { try { size = ((FileInfo)fsi).Length; } catch { } }
        return new FileItem
        {
            Name = fsi.Name,
            FullPath = fsi.FullName,
            IsFolder = dir,
            Size = size,
            Modified = fsi.LastWriteTime,
            Attributes = fsi.Attributes,
            TypeName = dir ? "File folder" : TypeOf(fsi.Extension),
        };
    }

    public static FileItem FromDrive(DriveInfo d)
    {
        string kind = d.DriveType switch
        {
            DriveType.Fixed => "Local Disk", DriveType.Removable => "USB Drive", DriveType.Network => "Network Drive",
            DriveType.CDRom => "CD Drive", _ => "Drive",
        };
        string label = "", extra = "";
        long total = 0;
        if (d.IsReady)
        {
            try { label = d.VolumeLabel; total = d.TotalSize; extra = $"{FormatSize(d.AvailableFreeSpace)} free of {FormatSize(total)}"; } catch { }
        }
        return new FileItem
        {
            Name = $"{(label.Length > 0 ? label : kind)} ({d.Name.TrimEnd('\\')})",
            FullPath = d.RootDirectory.FullName,
            IsFolder = true, IsDrive = true, Size = total, TypeName = kind, Extra = extra,
        };
    }

    static readonly ConcurrentDictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);
    static string TypeOf(string ext) => TypeNames.GetOrAdd(ext, e =>
    {
        var info = new SHFILEINFO();
        Native.SHGetFileInfo(e.Length == 0 ? "file" : e, Native.FILE_ATTRIBUTE_NORMAL, ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(), Native.SHGFI_TYPENAME | Native.SHGFI_USEFILEATTRIBUTES);
        if (!string.IsNullOrEmpty(info.szTypeName)) return info.szTypeName;
        return e.Length > 1 ? e[1..].ToUpperInvariant() + " File" : "File";
    });

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} bytes";
        string[] units = ["KB", "MB", "GB", "TB"];
        double v = bytes / 1024.0;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v >= 100 ? $"{v:0} {units[u]}" : v >= 10 ? $"{v:0.#} {units[u]}" : $"{v:0.##} {units[u]}";
    }
}

/// <summary>Keeps decoded thumbnails under a memory budget, evicting ones that are off-screen.</summary>
internal static class ThumbCache
{
    const long Budget = 768L * 1024 * 1024;
    static readonly LinkedList<FileItem> Lru = new();
    static long _bytes;

    public static void Add(FileItem item, long bytes)
    {
        Remove(item);
        item.ThumbBytes = bytes;
        item.LruNode = Lru.AddLast(item);
        _bytes += bytes;
        var node = Lru.First;
        while (_bytes > Budget && node != null)
        {
            var next = node.Next;
            if (node.Value.Realized == 0)
            {
                var victim = node.Value;
                Remove(victim);
                victim.DropThumb();
            }
            node = next;
        }
    }

    static void Remove(FileItem item)
    {
        if (item.LruNode == null) return;
        Lru.Remove(item.LruNode);
        item.LruNode = null;
        _bytes -= item.ThumbBytes;
    }
}
