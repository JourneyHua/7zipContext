using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace ZipContext.UiTest;

/// <summary>
/// 真实弹出经典右键菜单，并在菜单显示期间（模态循环中）
/// 1) 逐项 dump（GetMenuItemInfoW + GetCommandString，能看清自绘项的真实身份）
/// 2) 截屏
/// 3) 可选：按序号 Invoke 某一项（在 UI 线程上执行，避免跨套间死锁）
/// </summary>
internal static class MenuLive
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
    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuItemInfoW(IntPtr hMenu, uint uItem, bool fByPosition, ref MENUITEMINFOW lpmii);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MENUITEMINFOW
    {
        public uint cbSize, fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData, dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct CMINVOKECOMMANDINFO
    {
        public uint cbSize, fMask;
        public IntPtr hwnd, lpVerb, lpParameters, lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
    }

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

    private const uint WM_APP_DUMP = 0x8001;
    private const uint WM_APP_INVOKE = 0x8002;

    private sealed class HostForm : Form
    {
        public IntPtr Menu;
        public IContextMenu? Cm;
        public Action<string> Log = _ => { };
        public string? OutPng;
        public int? InvokeIndex;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_APP_DUMP)
            {
                try
                {
                    Dump(Menu, Log, 0);
                    if (OutPng is not null)
                    {
                        var bounds = Screen.PrimaryScreen!.Bounds;
                        using var bmp = new Bitmap(bounds.Width, bounds.Height);
                        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                        bmp.Save(OutPng, ImageFormat.Png);
                        Log($"screenshot saved: {OutPng}");
                    }
                }
                catch (Exception ex) { Log("dump failed: " + ex.Message); }
                return;
            }
            if (m.Msg == WM_APP_INVOKE)
            {
                var idx = (uint)m.WParam.ToInt64();
                try { InvokeItem(idx); }
                catch (Exception ex) { Log("invoke failed: " + ex.Message); }
                return;
            }
            base.WndProc(ref m);
        }

        private void InvokeItem(uint index)
        {
            var buf = Marshal.AllocHGlobal(2048);
            try
            {
                var mii = new MENUITEMINFOW
                {
                    cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(),
                    fMask = 0x2 | 0x4 | 0x40 | 0x100,
                    dwTypeData = buf,
                    cch = 1024,
                };
                if (GetMenuItemInfoW(Menu, index, true, ref mii) == 0) { Log("invoke: GetMenuItemInfo failed"); return; }
                Log($"invoke index={index} id={mii.wID} type=0x{mii.fType:X}");
                var info = new CMINVOKECOMMANDINFO
                {
                    cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                    lpVerb = (IntPtr)(mii.wID - 1),
                    nShow = 1,
                };
                var p = Marshal.AllocHGlobal(Marshal.SizeOf<CMINVOKECOMMANDINFO>());
                try
                {
                    Marshal.StructureToPtr(info, p, false);
                    var hr = Cm!.InvokeCommand(p);
                    Log($"InvokeCommand hr=0x{hr:X8}");
                }
                finally { Marshal.FreeHGlobal(p); }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public void ListZipContextWindows()
        {
            EnumWindows((h, _) =>
            {
                var sb = new StringBuilder(512);
                GetWindowTextW(h, sb, 512);
                var t = sb.ToString();
                if (t.Contains("7zipContext"))
                {
                    GetWindowThreadProcessId(h, out var pid);
                    Log($"  window \"{t}\" pid={pid} visible={IsWindowVisible(h)}");
                }
                return true;
            }, IntPtr.Zero);
        }
    }

    public static void Run(string zipPath, string? outPng, int? invokeIndex, Action<string> log)
    {
        if (SHParseDisplayName(zipPath, IntPtr.Zero, out var pidl, 0, out _) != 0) { log("parse failed"); return; }
        var iidFolder = IID_IShellFolder;
        if (SHBindToParent(pidl, ref iidFolder, out var psfPtr, out var pidlChild) != 0) { log("bind failed"); return; }
        var folder = (IShellFolder)Marshal.GetObjectForIUnknown(psfPtr);
        Marshal.Release(psfPtr);
        var apidl = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(apidl, pidlChild);
        var iidCm = IID_IContextMenu;
        if (folder.GetUIObjectOf(IntPtr.Zero, 1, apidl, ref iidCm, IntPtr.Zero, out var pcm) != 0) { log("GetUIObjectOf failed"); return; }
        Marshal.FreeHGlobal(apidl);
        var cm = (IContextMenu)Marshal.GetObjectForIUnknown(pcm);
        Marshal.Release(pcm);

        var hMenu = CreatePopupMenu();
        var qhr = cm.QueryContextMenu(hMenu, 0, 1, 0x7FFF, 0);
        log($"QueryContextMenu hr=0x{qhr:X8} count={GetMenuItemCount(hMenu)}");

        using var form = new HostForm
        {
            Menu = hMenu,
            Cm = cm,
            Log = log,
            OutPng = outPng,
            InvokeIndex = invokeIndex,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(40, 40),
            Size = new Size(300, 180),
            ShowInTaskbar = false,
            TopMost = true,
        };
        form.Show();
        form.Activate();
        SetForegroundWindow(form.Handle);

        var timer = new System.Threading.Timer(_ =>
        {
            PostMessageW(form.Handle, WM_APP_DUMP, IntPtr.Zero, IntPtr.Zero);
            if (invokeIndex is int idx)
            {
                Thread.Sleep(400);
                PostMessageW(form.Handle, WM_APP_INVOKE, (IntPtr)idx, IntPtr.Zero);
                Thread.Sleep(3500);
                form.Invoke(() => form.ListZipContextWindows());
                PostMessageW(form.Handle, 0x001F, IntPtr.Zero, IntPtr.Zero);
            }
            else
            {
                Thread.Sleep(200);
                PostMessageW(form.Handle, 0x001F, IntPtr.Zero, IntPtr.Zero);
            }
        }, null, 1400, Timeout.Infinite);

        TrackPopupMenuEx(hMenu, 0x0100, 60, 60, form.Handle, IntPtr.Zero);
        timer.Dispose();
        Marshal.ReleaseComObject(cm);
        Marshal.ReleaseComObject(folder);
        form.Close();
        log("MENU CLOSED");
    }

    private static void Dump(IntPtr hMenu, Action<string> log, int depth)
    {
        var count = GetMenuItemCount(hMenu);
        for (uint i = 0; i < count; i++)
        {
            var buf = Marshal.AllocHGlobal(2048);
            try
            {
                for (var k = 0; k < 2048; k++) Marshal.WriteByte(buf, k, 0);
                var mii = new MENUITEMINFOW
                {
                    cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(),
                    fMask = 0x2 | 0x4 | 0x40 | 0x100,
                    dwTypeData = buf,
                    cch = 1024,
                };
                if (GetMenuItemInfoW(hMenu, i, true, ref mii) == 0) { log($"{new string(' ', depth * 2)}[{i}] <fail>"); return; }
                var text = Marshal.PtrToStringUni(buf) ?? "";
                var pad = new string(' ', depth * 2);
                log($"{pad}[{i}] id={mii.wID} fType=0x{mii.fType:X} submenu={(mii.hSubMenu != IntPtr.Zero)} text=\"{text}\"");
                if (mii.hSubMenu != IntPtr.Zero && depth < 3) Dump(mii.hSubMenu, log, depth + 1);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
