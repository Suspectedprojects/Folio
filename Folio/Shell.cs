using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Folio.Native;

namespace Folio;

/// <summary>Gets thumbnails/icons from the Windows shell at any pixel size.</summary>
internal static class ShellImages
{
    const int SIIGBF_RESIZETOFIT = 0x0, SIIGBF_ICONONLY = 0x4;

    public static BitmapSource? Get(string path, int px, bool iconOnly, double dpiScale)
    {
        IShellItemImageFactory? factory = null;
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            int hr = factory.GetImage(new SIZE(px, px), iconOnly ? SIIGBF_ICONONLY : SIIGBF_RESIZETOFIT, out hbmp);
            if (hr != 0 || hbmp == IntPtr.Zero) return null;
            return FromHBitmap(hbmp, dpiScale);
        }
        catch { return null; }
        finally
        {
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }

    static BitmapSource? FromHBitmap(IntPtr hbmp, double dpiScale)
    {
        double dpi = 96 * dpiScale;
        if (GetObject(hbmp, Marshal.SizeOf<DIBSECTION>(), out var ds) == 0) return null;
        int w = ds.dsBm.bmWidth, h = Math.Abs(ds.dsBm.bmHeight), stride = w * 4;
        if (w <= 0 || h <= 0) return null;

        // Let GDI hand us the pixels as a top-down 32bpp buffer. It knows the bitmap's real
        // row order, which the shell doesn't report consistently across thumbnail providers.
        var bmi = new BITMAPINFO { bmiHeader = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        } };
        var px = new byte[stride * h];
        IntPtr hdc = GetDC(IntPtr.Zero);
        int lines;
        try { lines = GetDIBits(hdc, hbmp, 0, (uint)h, px, ref bmi, 0); }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
        if (lines != h) return null;

        bool hasAlpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { hasAlpha = true; break; }

        var src = BitmapSource.Create(w, h, dpi, dpi, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, px, stride);
        src.Freeze();
        return src;
    }
}

/// <summary>Shows the real Explorer right-click menu (Open with, Send to, 7-Zip, Properties...).</summary>
internal static class ShellContextMenu
{
    const uint CmdFirst = 1, CmdLast = 0x6FFF, ExtraFirst = 0x7000;
    const uint MF_BYPOSITION = 0x400, MF_STRING = 0x0, MF_SEPARATOR = 0x800;
    const uint CMF_NORMAL = 0, CMF_EXPLORE = 0x4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
    const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;
    const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_PTINVOKE = 0x20000000;
    const int WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_INITMENUPOPUP = 0x117, WM_MENUCHAR = 0x120;

    static IContextMenu2? _cm2;
    static IContextMenu3? _cm3;

    /// <summary>Forward owner-draw messages so submenus like "Open with" and "Send to" render.</summary>
    public static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)) return IntPtr.Zero;
        if (_cm3 != null)
        {
            if (_cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == 0) { handled = true; return result; }
        }
        else if (_cm2 != null && msg != WM_MENUCHAR)
        {
            if (_cm2.HandleMenuMsg((uint)msg, wParam, lParam) == 0) { handled = true; return IntPtr.Zero; }
        }
        return IntPtr.Zero;
    }

    /// <param name="intercept">Return true to handle a verb yourself instead of the shell.</param>
    /// <param name="extra">Folio's own commands, shown at the top of the menu.</param>
    public static void Show(IntPtr hwnd, IReadOnlyList<string> paths, bool extended, Func<string, bool> intercept,
        IReadOnlyList<(string Text, Action Run)>? extra = null)
    {
        Action? pick = null;
        ShowCore(hwnd, paths, extended, intercept, extra, a => pick = a);
        pick?.Invoke(); // run after the menu and its COM objects are gone
    }

    static void ShowCore(IntPtr hwnd, IReadOnlyList<string> paths, bool extended, Func<string, bool> intercept,
        IReadOnlyList<(string Text, Action Run)>? extra, Action<Action> picked)
    {
        var pidls = new List<IntPtr>();
        IntPtr hMenu = IntPtr.Zero;
        IShellFolder? folder = null;
        IContextMenu? cm = null;
        try
        {
            foreach (var p in paths)
                if (SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) == 0) pidls.Add(pidl);
            if (pidls.Count == 0) return;

            var iidFolder = typeof(IShellFolder).GUID;
            if (SHBindToParent(pidls[0], ref iidFolder, out var pFolder, out _) != 0) return;
            folder = (IShellFolder)Marshal.GetObjectForIUnknown(pFolder);
            Marshal.Release(pFolder);

            var children = pidls.Select(ILFindLastID).ToArray();
            var iidCm = typeof(IContextMenu).GUID;
            if (folder.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iidCm, IntPtr.Zero, out var pCm) != 0) return;
            cm = (IContextMenu)Marshal.GetObjectForIUnknown(pCm);
            Marshal.Release(pCm);
            _cm3 = cm as IContextMenu3;
            _cm2 = cm as IContextMenu2;

            hMenu = CreatePopupMenu();
            cm.QueryContextMenu(hMenu, 0, CmdFirst, CmdLast, CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME | (extended ? CMF_EXTENDEDVERBS : 0));
            if (extra is { Count: > 0 })
            {
                InsertMenu(hMenu, 0, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
                for (int i = extra.Count - 1; i >= 0; i--)
                    InsertMenu(hMenu, 0, MF_BYPOSITION | MF_STRING, (UIntPtr)(ExtraFirst + (uint)i), extra[i].Text);
            }
            GetCursorPos(out var pt);
            uint cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, hwnd, IntPtr.Zero);
            if (extra != null && cmd >= ExtraFirst && cmd < ExtraFirst + extra.Count)
            {
                picked(extra[(int)(cmd - ExtraFirst)].Run);
                return;
            }
            if (cmd < CmdFirst || cmd > CmdLast) return;

            uint id = cmd - CmdFirst;
            if (intercept(GetVerb(cm, id))) return;

            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE | (extended ? CMIC_MASK_SHIFT_DOWN : 0),
                hwnd = hwnd,
                lpVerb = (IntPtr)id,
                lpVerbW = (IntPtr)id,
                nShow = 1,
                ptInvoke = pt,
            };
            cm.InvokeCommand(ref info);
        }
        finally
        {
            _cm2 = null; _cm3 = null;
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (cm != null) Marshal.ReleaseComObject(cm);
            if (folder != null) Marshal.ReleaseComObject(folder);
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
        }
    }

    static string GetVerb(IContextMenu cm, uint id)
    {
        const uint GCS_VERBW = 4;
        var buf = Marshal.AllocHGlobal(1024);
        try
        {
            Marshal.WriteInt16(buf, 0);
            return cm.GetCommandString((UIntPtr)id, GCS_VERBW, IntPtr.Zero, buf, 512) == 0
                ? Marshal.PtrToStringUni(buf) ?? "" : "";
        }
        catch { return ""; }
        finally { Marshal.FreeHGlobal(buf); }
    }
}

/// <summary>Copy/move/delete/rename through the shell, so you get the native progress,
/// conflict and Recycle Bin dialogs plus Ctrl+Z undo in Explorer.</summary>
internal static class FileOps
{
    public static void Run(uint func, IEnumerable<string> from, string? to, ushort flags, IntPtr owner, bool background)
    {
        string pFrom = string.Join("\0", from) + "\0\0";
        string? pTo = to == null ? null : to + "\0\0";
        void Do()
        {
            var op = new SHFILEOPSTRUCT { hwnd = background ? IntPtr.Zero : owner, wFunc = func, pFrom = pFrom, pTo = pTo, fFlags = flags };
            SHFileOperation(ref op);
        }
        if (!background) { Do(); return; }
        var t = new Thread(Do) { IsBackground = false, Name = "FileOp" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1155) // no app associated
        {
            Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { /* user cancelled (e.g. UAC) */ }
    }

    public static void ShowProperties(string path, IntPtr owner)
    {
        const uint SEE_MASK_INVOKEIDLIST = 0xC;
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(), fMask = SEE_MASK_INVOKEIDLIST, hwnd = owner,
            lpVerb = "properties", lpFile = path, nShow = 1,
        };
        ShellExecuteEx(ref info);
    }

    public static string? ResolveShortcut(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(lnk);
            string target = sc.TargetPath;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch { return null; }
    }
}
