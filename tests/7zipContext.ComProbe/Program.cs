using System.Runtime.InteropServices;

namespace ZipContext.ComProbe;

// 真实 COM 激活链路探针：CoCreateInstance → GetFlags / EnumSubCommands / GetState / GetTitle / GetIcon
// 每步先落盘再调用——即使挂死被杀，日志也能看到断点。
// 用法：7zipContext.ComProbe.exe <zipPath>
internal static class Program
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "7zipContext-comprobe.log");

    private static void L(string m)
    {
        File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + m + Environment.NewLine);
        Console.WriteLine(m);
    }

    // ---- 互操作（与 7zipContext.Shell 契约一致） ----
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern int CoTaskMemFree(IntPtr pv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromShellItem(IntPtr psi, ref Guid riid, out IntPtr ppv);

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr a, ref Guid b, ref Guid c, out IntPtr d);
        [PreserveSig] int GetPropertyStore(int a, ref Guid b, out IntPtr c);
        [PreserveSig] int GetPropertyDescriptionList(ref Guid a, ref Guid b, out IntPtr c);
        [PreserveSig] int GetAttributes(int a, uint b, out uint c);
        [PreserveSig] int GetCount(out uint n);
        [PreserveSig] int GetItemAt(uint i, out IShellItem s);
        [PreserveSig] int EnumItems(out IntPtr e);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr a, ref Guid b, ref Guid c, out IntPtr d);
        [PreserveSig] int GetParent(out IntPtr p);
        [PreserveSig] int GetDisplayName(uint f, out IntPtr n);
        [PreserveSig] int GetAttributes(uint m, out uint a);
        [PreserveSig] int Compare(IShellItem o, uint h, out int r);
    }

    [ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IExplorerCommand
    {
        [PreserveSig] int GetTitle(IShellItemArray a, out IntPtr t);
        [PreserveSig] int GetIcon(IShellItemArray a, out IntPtr t);
        [PreserveSig] int GetToolTip(IShellItemArray a, out IntPtr t);
        [PreserveSig] int GetCanonicalName(out Guid g);
        [PreserveSig] int GetState(IShellItemArray? a, [MarshalAs(UnmanagedType.Bool)] bool slow, out uint s);
        [PreserveSig] int Invoke(IShellItemArray a, IntPtr b);
        [PreserveSig] int GetFlags(out uint f);
        [PreserveSig] int EnumSubCommands(out IntPtr e);
    }

    private static readonly (string Name, string Clsid)[] Verbs =
    {
        ("SmartExtract",       "B9A10001-8C3F-4A2E-9E6D-5F1A2B3C4D01"),
        ("SmartExtractDelete", "B9A10002-8C3F-4A2E-9E6D-5F1A2B3C4D02"),
        ("ExtractToName",      "B9A10003-8C3F-4A2E-9E6D-5F1A2B3C4D03"),
        ("ExtractToFolder",    "B9A10004-8C3F-4A2E-9E6D-5F1A2B3C4D04"),
        ("AddZip",             "B9A10006-8C3F-4A2E-9E6D-5F1A2B3C4D06"),
        ("Add7z",              "B9A10007-8C3F-4A2E-9E6D-5F1A2B3C4D07"),
        ("QuickZip",           "B9A10008-8C3F-4A2E-9E6D-5F1A2B3C4D08"),
    };

    private static void InvokeVerb(string zipPath, string clsidStr)
    {
        var iid = new Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9");
        var clsid = new Guid(clsidStr);
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, 0x4, ref iid, out var ppv);
        L($"create {clsidStr} hr=0x{hr:X8}");
        if (hr != 0) return;
        var cmd = (IExplorerCommand)Marshal.GetObjectForIUnknown(ppv);
        Marshal.Release(ppv);

        var gi = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        hr = SHCreateItemFromParsingName(zipPath, IntPtr.Zero, ref gi, out var psi);
        L($"SHCreateItem hr=0x{hr:X8}");
        if (hr != 0) return;
        var ga = new Guid("b63ea76d-1f85-456f-a19c-48159efa858b");
        hr = SHCreateShellItemArrayFromShellItem(psi, ref ga, out var ppsa);
        Marshal.Release(psi);
        L($"SHCreateShellItemArray hr=0x{hr:X8}");
        if (hr != 0) return;
        var arr = (IShellItemArray)Marshal.GetObjectForIUnknown(ppsa);
        Marshal.Release(ppsa);

        L("Invoke start");
        hr = cmd.Invoke(arr, IntPtr.Zero);
        L($"Invoke hr=0x{hr:X8}");

        Thread.Sleep(4000);
        var procs = System.Diagnostics.Process.GetProcessesByName("7zipContext");
        L($"7zipContext processes: {procs.Length}");
        foreach (var p in procs)
            L($"  pid={p.Id} title=[{p.MainWindowTitle}]");
    }

    private static void Main(string[] args)
    {
        try { File.Delete(LogPath); } catch { }

        // 模式二：导出真实经典右键菜单树（定位级联子菜单来源）
        if (args.Length >= 2 && args[0] == "menu")
        {
            ContextMenuProbe.Run(args[1], L);
            return;
        }

        // 模式三：按菜单序号触发命令（确认某个菜单项真实身份）
        if (args.Length >= 3 && args[0] == "invoke")
        {
            ContextMenuProbe.InvokeByIndex(args[1], uint.Parse(args[2]), L);
            return;
        }

        // 模式四：直接对某个动词 CLSID 调 Invoke（Explorer 点击时的真实路径）
        if (args.Length >= 3 && args[0] == "invoke-verb")
        {
            InvokeVerb(args[1], args[2]);
            return;
        }

        var zip = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "probe", "probe.zip");
        var iid = new Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9");

        // 构造选中项数组（压缩包）
        IShellItemArray? arr = null;
        var gi = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        var h = SHCreateItemFromParsingName(zip, IntPtr.Zero, ref gi, out var psi);
        if (h == 0)
        {
            var ga = new Guid("b63ea76d-1f85-456f-a19c-48159efa858b");
            h = SHCreateShellItemArrayFromShellItem(psi, ref ga, out var ppsa);
            Marshal.Release(psi);
            if (h == 0)
            {
                arr = (IShellItemArray)Marshal.GetObjectForIUnknown(ppsa);
                Marshal.Release(ppsa);
            }
        }
        L($"shell item array created: {(arr is null ? "NO" : "YES")}");

        foreach (var (name, clsidStr) in Verbs)
        {
            var clsid = new Guid(clsidStr);
            L($"--- {name} ---");
            var hr = CoCreateInstance(ref clsid, IntPtr.Zero, 0x4, ref iid, out var ppv);
            if (hr != 0) { L($"  create hr=0x{hr:X8}"); continue; }

            var cmd = (IExplorerCommand)Marshal.GetObjectForIUnknown(ppv);
            Marshal.Release(ppv);

            hr = cmd.GetFlags(out var flags);
            L($"  GetFlags hr=0x{hr:X8} flags=0x{flags:X}");

            hr = cmd.EnumSubCommands(out var penum);
            L($"  EnumSubCommands hr=0x{hr:X8} ppEnum={(penum == IntPtr.Zero ? "null" : "NONNULL")}");
            if (penum != IntPtr.Zero) Marshal.Release(penum);

            if (arr is not null)
            {
                hr = cmd.GetState(arr, true, out var st);
                L($"  GetState(arr) hr=0x{hr:X8} state={st}");
                hr = cmd.GetTitle(arr, out var pt);
                var title = Marshal.PtrToStringUni(pt);
                if (pt != IntPtr.Zero) CoTaskMemFree(pt);
                L($"  GetTitle hr=0x{hr:X8} title={title}");
                hr = cmd.GetIcon(arr, out var pi);
                var icon = Marshal.PtrToStringUni(pi);
                if (pi != IntPtr.Zero) CoTaskMemFree(pi);
                L($"  GetIcon hr=0x{hr:X8} icon={icon}");
            }
            Marshal.ReleaseComObject(cmd);
        }

        L("ALL DONE");
    }
}
