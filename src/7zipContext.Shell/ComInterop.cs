using System.Runtime.InteropServices;

namespace ZipContext.Shell;

/// <summary>Win11 右键菜单所需的 COM 互操作定义（IExplorerCommand 体系）。</summary>
public static class ComInterop
{
    // ---- 关键 IID ----
    public const string IID_IUnknown = "00000000-0000-0000-C000-000000000046";
    public const string IID_IClassFactory = "00000001-0000-0000-C000-000000000046";
    public const string IID_IExplorerCommand = "a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9";
    public const string IID_IShellItemArray = "b63ea76d-1f85-456f-a19c-48159efa858b";
    public const string IID_IShellItem = "43826d1e-e718-42ee-bc55-a1e261c37bfe";

    // ---- 常量 ----
    public const uint CLSCTX_LOCAL_SERVER = 0x4;
    public const uint REGCLS_MULTIPLEUSE = 0x4;
    public const uint COINIT_APARTMENTTHREADED = 0x2;

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);
    public const int S_OK = 0;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);

    // EXPCMDSTATE
    public const uint ECS_ENABLED = 0;
    public const uint ECS_DISABLED = 1;
    public const uint ECS_HIDDEN = 2;

    // EXPCMDFLAGS
    public const uint ECF_DEFAULT = 0x1;
    public const uint ECF_HASSUBCOMMANDS = 0x2;

    // SIGDN
    public const uint SIGDN_FILESYSPATH = 0x80058000;

    // ---- 接口 ----

    [ComImport]
    [Guid(IID_IClassFactory)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance([MarshalAs(UnmanagedType.IUnknown)] object? pUnkOuter,
            ref Guid riid, out IntPtr ppvObject);

        [PreserveSig]
        int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
    }

    /// <summary>IExplorerCommand（shobjidl_core.h）。</summary>
    [ComImport]
    [Guid(IID_IExplorerCommand)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IExplorerCommand
    {
        [PreserveSig]
        int GetTitle(IShellItemArray? psiItemArray, out IntPtr ppszName);

        [PreserveSig]
        int GetIcon(IShellItemArray? psiItemArray, out IntPtr ppszIcon);

        [PreserveSig]
        int GetToolTip(IShellItemArray? psiItemArray, out IntPtr ppszInfotip);

        [PreserveSig]
        int GetCanonicalName(out Guid pguidCommandName);

        [PreserveSig]
        int GetState(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.Bool)] bool fOkToBeSlow,
            out uint pCmdState);

        [PreserveSig]
        int Invoke(IShellItemArray? psiItemArray, IntPtr pbc);

        [PreserveSig]
        int GetFlags(out uint pFlags);

        [PreserveSig]
        int EnumSubCommands(out IntPtr ppEnum);
    }

    [ComImport]
    [Guid(IID_IShellItemArray)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemArray
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetPropertyDescriptionList(ref Guid keyType, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetAttributes(int dwAttribFlags, uint sfgaoMask, out uint psfgaoAttribs);

        [PreserveSig]
        int GetCount(out uint pdwNumItems);

        [PreserveSig]
        int GetItemAt(uint dwIndex, out IShellItem ppsi);

        [PreserveSig]
        int EnumItems(out IntPtr ppenumShellItems);
    }

    [ComImport]
    [Guid(IID_IShellItem)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetParent(out IntPtr ppsi);

        [PreserveSig]
        int GetDisplayName(uint sigdnName, out IntPtr ppszName);

        [PreserveSig]
        int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        [PreserveSig]
        int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    // ---- P/Invoke ----

    [DllImport("ole32.dll")]
    public static extern int CoRegisterClassObject(ref Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk, uint dwClsContext, uint flags,
        out uint lpdwRegister);

    [DllImport("ole32.dll")]
    public static extern int CoRevokeClassObject(uint dwRegister);

    [DllImport("ole32.dll")]
    public static extern void CoFreeUnusedLibraries();

    [DllImport("ole32.dll")]
    public static extern int CoTaskMemFree(IntPtr pv);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    /// <summary>从 IShellItemArray 提取全部文件系统路径。</summary>
    public static List<string> GetPaths(IShellItemArray? array)
    {
        var result = new List<string>();
        if (array is null) return result;
        try
        {
            array.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (array.GetItemAt(i, out var item) != S_OK || item is null) continue;
                if (item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr ppsz) == S_OK && ppsz != IntPtr.Zero)
                {
                    result.Add(Marshal.PtrToStringUni(ppsz) ?? "");
                    CoTaskMemFree(ppsz);
                }
                Marshal.ReleaseComObject(item);
            }
        }
        catch { /* shell 数组异常时返回已收集的部分 */ }
        return result;
    }
}
