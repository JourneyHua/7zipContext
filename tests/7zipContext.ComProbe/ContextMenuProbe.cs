using System.Runtime.InteropServices;

namespace ZipContext.ComProbe;

/// <summary>
/// 复刻 Explorer 的经典右键菜单构建（SHParseDisplayName → SHBindToParent → IShellFolder::GetUIObjectOf(IContextMenu)
/// → QueryContextMenu），然后把 HMENU 整棵导出来（含级联子菜单），用于定位「压缩文件预览」为何变成级联菜单。
/// </summary>
internal static class ContextMenuProbe
{
    private static readonly Guid IID_IShellFolder = new("000214e6-0000-0000-c000-000000000046");
    private static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuItemInfoW(IntPtr hMenu, uint uItem, bool fByPosition, ref MENUITEMINFOW lpmii);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [StructLayout(LayoutKind.Sequential)]
    private struct MENUITEMINFOW
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    private const uint MIIM_ID = 0x2;
    private const uint MIIM_SUBMENU = 0x4;
    private const uint MIIM_STRING = 0x40;
    private const uint MIIM_FTYPE = 0x100;

    [ComImport, Guid("000214e6-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl,
            ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct CMINVOKECOMMANDINFO
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static void ListZipContextWindows(Action<string> log)
    {
        EnumWindows((h, _) =>
        {
            var sb = new System.Text.StringBuilder(512);
            GetWindowTextW(h, sb, 512);
            var t = sb.ToString();
            if (t.Contains("7zipContext"))
            {
                GetWindowThreadProcessId(h, out var pid);
                log($"  window \"{t}\" pid={pid} visible={IsWindowVisible(h)}");
            }
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>按菜单序号取命令 id 并 InvokeCommand，用于确认某个菜单项究竟是谁。</summary>
    public static void InvokeByIndex(string zipPath, uint index, Action<string> log)
    {
        using var resources = new NativeResources();
        var hr = SHParseDisplayName(zipPath, IntPtr.Zero, out var pidl, 0, out _);
        resources.Pidl(pidl);
        if (hr != 0) { log($"SHParseDisplayName hr=0x{hr:X8}"); return; }
        var iidFolder = IID_IShellFolder;
        hr = SHBindToParent(pidl, ref iidFolder, out var psfPtr, out var pidlChild);
        resources.ComPointer(psfPtr);
        if (hr != 0) { log($"SHBindToParent hr=0x{hr:X8}"); return; }
        var folder = resources.Rcw<IShellFolder>(psfPtr);
        resources.Release(psfPtr);
        var apidl = resources.Allocate(IntPtr.Size);
        Marshal.WriteIntPtr(apidl, pidlChild);
        var iidCm = IID_IContextMenu;
        hr = folder.GetUIObjectOf(IntPtr.Zero, 1, apidl, ref iidCm, IntPtr.Zero, out var pcm);
        resources.ComPointer(pcm);
        resources.FreeHGlobal(apidl);
        if (hr != 0) { log($"GetUIObjectOf hr=0x{hr:X8}"); return; }
        var cm = resources.Rcw<IContextMenu>(pcm);
        resources.Release(pcm);

        var hMenu = resources.Menu(CreatePopupMenu());
        hr = cm.QueryContextMenu(hMenu, 0, 1, 0x7FFF, 0);
        log($"QueryContextMenu hr=0x{hr:X8}");

        var buf = resources.Allocate(1024);
        var mii = new MENUITEMINFOW
        {
            cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(),
            fMask = MIIM_ID | MIIM_SUBMENU | MIIM_STRING | MIIM_FTYPE,
            dwTypeData = buf,
            cch = 512,
        };
        if (GetMenuItemInfoW(hMenu, index, true, ref mii) == 0) { log("GetMenuItemInfo failed"); return; }
        log($"target index={index} id={mii.wID} fType=0x{mii.fType:X} submenu={(mii.hSubMenu != IntPtr.Zero)}");
        resources.FreeHGlobal(buf);

        var info = new CMINVOKECOMMANDINFO
        {
            cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
            lpVerb = (IntPtr)(mii.wID - 1), // idCmdFirst = 1
            nShow = 1,
        };
        var inv = resources.Allocate(Marshal.SizeOf<CMINVOKECOMMANDINFO>());
        try
        {
            Marshal.StructureToPtr(info, inv, false);
            hr = cm.InvokeCommand(inv);
            log($"InvokeCommand hr=0x{hr:X8}");
        }
        finally { resources.FreeHGlobal(inv); }

        resources.Destroy(hMenu);
        resources.ReleaseComObject(cm);
        resources.ReleaseComObject(folder);

        Thread.Sleep(4000);
        log("--- 7zipContext windows after invoke ---");
        ListZipContextWindows(log);
    }

    public static void Run(string zipPath, Action<string> log)
    {
        using var resources = new NativeResources();
        var hr = SHParseDisplayName(zipPath, IntPtr.Zero, out var pidl, 0, out _);
        resources.Pidl(pidl);
        log($"SHParseDisplayName hr=0x{hr:X8}");
        if (hr != 0) return;

        var iidFolder = IID_IShellFolder;
        hr = SHBindToParent(pidl, ref iidFolder, out var psfPtr, out var pidlChild);
        resources.ComPointer(psfPtr);
        log($"SHBindToParent hr=0x{hr:X8}");
        if (hr != 0) return;

        var folder = resources.Rcw<IShellFolder>(psfPtr);
        resources.Release(psfPtr);

        // apidl = 指向「一个 PIDL 指针」的数组
        var apidl = resources.Allocate(IntPtr.Size);
        Marshal.WriteIntPtr(apidl, pidlChild);
        var iidCm = IID_IContextMenu;
        hr = folder.GetUIObjectOf(IntPtr.Zero, 1, apidl, ref iidCm, IntPtr.Zero, out var pcm);
        resources.ComPointer(pcm);
        resources.FreeHGlobal(apidl);
        log($"GetUIObjectOf(IContextMenu) hr=0x{hr:X8}");
        if (hr != 0) return;

        var cm = resources.Rcw<IContextMenu>(pcm);
        resources.Release(pcm);

        var hMenu = resources.Menu(CreatePopupMenu());
        hr = cm.QueryContextMenu(hMenu, 0, 1, 0x7FFF, 0 /* CMF_NORMAL */);
        log($"QueryContextMenu hr=0x{hr:X8} count={GetMenuItemCount(hMenu)}");

        Dump(hMenu, log, depth: 0);
        resources.Destroy(hMenu);
        resources.ReleaseComObject(cm);
        resources.ReleaseComObject(folder);
        log("MENU DUMP DONE");
    }

    private static void Dump(IntPtr hMenu, Action<string> log, int depth)
    {
        var count = GetMenuItemCount(hMenu);
        for (uint i = 0; i < count; i++)
        {
            var buf = Marshal.AllocHGlobal(1024);
            try
            {
                for (var k = 0; k < 1024; k++) Marshal.WriteByte(buf, k, 0);
                var mii = new MENUITEMINFOW
                {
                    cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(),
                    fMask = MIIM_ID | MIIM_SUBMENU | MIIM_STRING | MIIM_FTYPE,
                    dwTypeData = buf,
                    cch = 512,
                };
                if (GetMenuItemInfoW(hMenu, i, true, ref mii) == 0)
                {
                    log($"{new string(' ', depth * 2)}[{i}] <GetMenuItemInfo failed>");
                    return;
                }
                var text = Marshal.PtrToStringUni(buf) ?? "";
                var pad = new string(' ', depth * 2);
                var flag = mii.hSubMenu != IntPtr.Zero ? " [POPUP]" : "";
                var sep = (mii.fType & 0x800) != 0 ? " <SEPARATOR>" : "";
                log($"{pad}[{i}] id={mii.wID} type=0x{mii.fType:X}{flag}{sep} text=\"{text}\"");
                if (mii.hSubMenu != IntPtr.Zero && depth < 3)
                    Dump(mii.hSubMenu, log, depth + 1);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
