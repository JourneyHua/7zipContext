using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ZipContext.UiTest;

/// <summary>
/// 在本进程内真实弹出某文件的经典右键菜单（IContextMenu + TrackPopupMenuEx），
/// 菜单显示期间用后台线程截屏 —— 这样可以捕捉到「运行时动态插入」的菜单项
/// （只在 WM_INITMENUPOPUP 阶段加进去、QueryContextMenu 之后看不到的那些）。
/// </summary>
internal static class MenuShot
{
    private static readonly Guid IID_IShellFolder = new("000214e6-0000-0000-c000-000000000046");
    private static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [ComImport, Guid("000214e6-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(IntPtr pici);
        [PreserveSig] int GetCommandString(int idCmd, uint uType, IntPtr pReserved, IntPtr pszName, int cchMax);
    }

    private static IntPtr BuildMenu(NativeResources resources, string zipPath, out IContextMenu? cm, out IShellFolder? folder)
    {
        cm = null;
        folder = null;
        var parseHr = SHParseDisplayName(zipPath, IntPtr.Zero, out var pidl, 0, out _);
        resources.Pidl(pidl);
        if (parseHr != 0) return IntPtr.Zero;
        var iidFolder = IID_IShellFolder;
        var bindHr = SHBindToParent(pidl, ref iidFolder, out var psfPtr, out var pidlChild);
        resources.ComPointer(psfPtr);
        if (bindHr != 0) return IntPtr.Zero;
        folder = resources.Rcw<IShellFolder>(psfPtr);
        resources.Release(psfPtr);

        var apidl = resources.Allocate(IntPtr.Size);
        Marshal.WriteIntPtr(apidl, pidlChild);
        var iidCm = IID_IContextMenu;
        var hr = folder.GetUIObjectOf(IntPtr.Zero, 1, apidl, ref iidCm, IntPtr.Zero, out var pcm);
        resources.ComPointer(pcm);
        resources.FreeHGlobal(apidl);
        if (hr != 0) return IntPtr.Zero;

        cm = resources.Rcw<IContextMenu>(pcm);
        resources.Release(pcm);

        var hMenu = resources.Menu(CreatePopupMenu());
        cm.QueryContextMenu(hMenu, 0, 1, 0x7FFF, 0);
        return hMenu;
    }

    /// <summary>显示菜单 → 截屏 → 关闭菜单。</summary>
    public static void ShowAndCapture(string zipPath, string outPng, Action<string> log)
    {
        using var resources = new NativeResources();
        var hMenu = BuildMenu(resources, zipPath, out var cm, out var folder);
        if (hMenu == IntPtr.Zero) { log("build menu failed"); return; }

        using var form = new Form
        {
            Text = "7zipContext menu host",
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(40, 40),
            Size = new Size(320, 200),
            ShowInTaskbar = false,
            TopMost = true,
        };
        form.Show();
        form.Activate();
        SetForegroundWindow(form.Handle);
        log($"host hwnd={form.Handle}");

        using var done = new ManualResetEventSlim();
        using var timer = new DrainingTimer(_ =>
        {
            try
            {
                var bounds = Screen.PrimaryScreen!.Bounds;
                using var bmp = new Bitmap(bounds.Width, bounds.Height);
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                bmp.Save(outPng, ImageFormat.Png);
                log($"screenshot saved: {outPng}");
            }
            catch (Exception ex) { log("capture failed: " + ex.Message); }
            finally
            {
                PostMessageW(form.Handle, 0x001F /* WM_CANCELMODE */, IntPtr.Zero, IntPtr.Zero);
                done.Set();
            }
        }, null, 1500, Timeout.Infinite);

        // TPM_RETURNCMD | TPM_LEFTALIGN
        var cmd = TrackPopupMenuEx(hMenu, 0x0100 | 0x0000, 60, 60, form.Handle, IntPtr.Zero);
        log($"TrackPopupMenuEx returned cmd={cmd}");
        done.Wait(3000);
        timer.Dispose();

        if (cm is not null) resources.ReleaseComObject(cm);
        if (folder is not null) resources.ReleaseComObject(folder);
        form.Close();
    }
}
