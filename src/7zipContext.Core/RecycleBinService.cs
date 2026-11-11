using System.Runtime.InteropServices;

namespace ZipContext.Core;

/// <summary>把文件/文件夹删进回收站（Q5：可反悔）。</summary>
public static class RecycleBinService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>删除到回收站。成功返回 true。</summary>
    public static bool SendToRecycleBin(string path, out string? error)
    {
        error = null;
        try
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                error = "文件不存在。";
                return false;
            }

            var op = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                // pFrom 必须以双 \0 结尾
                pFrom = path + "\0\0",
                pTo = null,
                fFlags = FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI,
            };
            int result = SHFileOperation(ref op);
            if (result != 0)
            {
                error = $"SHFileOperation 返回 {result}";
                return false;
            }
            if (op.fAnyOperationsAborted)
            {
                error = "操作被中止。";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
