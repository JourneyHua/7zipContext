using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ZipContext.Classic;

/// <summary>
/// 经典菜单 handler：压缩包上补 4 条解压动词 + 内联目录树预览。
/// 由 explorer.exe 进程内加载（coreclr 在 explorer 里跑）。
///
/// 为什么这 4 条要自己插：AppX 的动词在经典菜单里只会漏出「添加到 …」两条，
/// 解压那四条不会出现（紧凑菜单里它们在「7zipContext &gt;」浮层中）。
///
/// 安全三条：
///   1. 异常绝不抛回 explorer，全部吞掉并记日志。
///   2. 列目录有 250ms 硬超时；超时降级成一行灰字，绝不阻塞菜单。
///   3. 定位 exe 用 Assembly.Location 起算，不用 AppContext.BaseDirectory。
/// </summary>
[ComVisible(true)]
[Guid(Clsid)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class PreviewMenu : IShellExtInit, IContextMenu
{
    public const string Clsid = "B9A10020-8C3F-4A2E-9E6D-5F1A2B3C4D20";

    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const string PreviewTitle = "压缩文件预览";

    private readonly List<string> _paths = new();
    private readonly List<string> _commands = new(); // 菜单 id 偏移 → CLI 动词
    private string? _archive;

    // ---------------- IShellExtInit ----------------

    public int Initialize(IntPtr pidlFolder, IntPtr pDataObj, IntPtr hKeyProgID)
    {
        try
        {
            _archive = null;
            _paths.Clear();
            _commands.Clear();

            foreach (var p in ShellData.SelectedPaths(pDataObj))
                if (File.Exists(p) || Directory.Exists(p)) _paths.Add(p);

            // 只有「单选且是压缩包」才做预览树；其余只给压缩动词
            if (_paths.Count == 1 && ArchiveFormats.IsArchive(_paths[0]))
                _archive = _paths[0];
        }
        catch (Exception ex) { Log("Initialize: " + ex); }
        return 0;
    }

    // ---------------- IContextMenu ----------------

    public int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags)
    {
        try
        {
            if (_paths.Count == 0) return 0;

            var icon = Icons.App;
            var baseName = ZipContext.Core.NamingHelper.TargetBaseName(_paths);
            if (baseName.Length > 32) baseName = baseName[..32] + "…";

            _commands.Clear();
            uint pos = indexMenu;
            uint id = (uint)idCmdFirst;

            AddItem(hMenu, ref pos, NativeMethods.MF_SEPARATOR, null, IntPtr.Zero);

            if (_archive is not null)
            {
                var extract = new (string Text, string Verb)[]
                {
                    ("智能解压到此处(S)", "--smart-extract"),
                    ("智能解压到此处并删除", "--smart-extract-delete"),
                    ($"解压到 {baseName}\\(T)", "--extract-to-name"),
                    ("解压到其他文件夹(B)...", "--extract-to"),
                };
                foreach (var (text, verb) in extract)
                {
                    AddItem(hMenu, ref pos, NativeMethods.MF_STRING, text, icon, (UIntPtr)id++);
                    _commands.Add(verb);
                }
            }

            // 压缩动词：普通文件/文件夹也显示（这七条全由我们自己插，菜单才会连成一组）
            foreach (var verb in new[] { "--add-zip", "--add-7z" })
            {
                var ext = verb["--add-".Length..];
                AddItem(hMenu, ref pos, NativeMethods.MF_STRING, $"添加到 {baseName}.{ext}", icon, (UIntPtr)id++);
                _commands.Add(verb);
            }

            if (_archive is null) return _commands.Count;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (status, lines) = ArchiveTree.Read(_archive);
            sw.Stop();
            PreviewMenu.Log($"preview {Path.GetFileName(_archive)} status={status} lines={lines.Count} elapsed={sw.ElapsedMilliseconds}ms");

            AddItem(hMenu, ref pos, NativeMethods.MF_SEPARATOR, null, IntPtr.Zero);
            AddItem(hMenu, ref pos, NativeMethods.MF_STRING, PreviewTitle, icon);

            if (status != TreeStatus.Ok)
            {
                AddItem(hMenu, ref pos,
                    NativeMethods.MF_STRING | NativeMethods.MF_GRAYED,
                    StatusText(status), IntPtr.Zero);
                return _commands.Count;
            }

            foreach (var line in lines)
            {
                // 缩进已经做进位图（ForNode 按 depth 补透明），文字不再加全角空格，
                // 否则图标贴最左、文字被推远，看起来是散的。
                AddItem(hMenu, ref pos,
                    NativeMethods.MF_STRING | NativeMethods.MF_GRAYED,
                    line.Name, Icons.ForNode(line.IsDir, line.Depth));
            }

            return _commands.Count;
        }
        catch (Exception ex)
        {
            Log("QueryContextMenu: " + ex);
            return 0;
        }
    }

    public int InvokeCommand(ref CMINVOKECOMMANDINFO pici)
    {
        try
        {
            long verb = (long)pici.lpVerb;
            if ((verb & 0xFFFF0000L) != 0) return 0; // 字符串动词不是我们的

            var idx = (int)verb;
            if (idx < 0 || idx >= _commands.Count) return 0;

            Launch(_commands[idx], _paths);
            return 0;
        }
        catch (Exception ex)
        {
            Log("InvokeCommand: " + ex);
            return 0;
        }
    }

    public int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax)
        => E_NOTIMPL;

    // ---------------- 内部 ----------------

    private static void AddItem(IntPtr hMenu, ref uint pos, uint flags, string? text,
        IntPtr bmp, UIntPtr id = default)
    {
        if (!NativeMethods.InsertMenuW(hMenu, pos, flags | NativeMethods.MF_BYPOSITION, id, text))
        {
            Log($"InsertMenuW 失败 flags=0x{flags:X} text={text}");
            return;
        }

        if (bmp != IntPtr.Zero)
        {
            var mii = new MENUITEMINFO
            {
                cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
                fMask = NativeMethods.MIIM_BITMAP,
                hbmpItem = bmp,
            };
            if (!NativeMethods.SetMenuItemInfoW(hMenu, pos, true, ref mii))
                Log($"SetMenuItemInfoW 失败 err={Marshal.GetLastWin32Error()}");
        }
        pos++;
    }

    private static string StatusText(TreeStatus s) => s switch
    {
        TreeStatus.Timeout => "（读取超时，包太大）",
        TreeStatus.Empty => "（空压缩包）",
        // 非 .zip 且引擎缺失时走这里（.zip 走 ZipArchive，不依赖引擎，天然可用）
        TreeStatus.Unsupported => "（未找到 7-Zip 引擎，请运行 install.ps1）",
        _ => "（无法读取，可能已加密或损坏）",
    };

    /// <summary>按红线 2：Assembly.Location 起算，绝不用 AppContext.BaseDirectory。</summary>
    private static string ResolveAppExe()
    {
        var asm = Assembly.GetExecutingAssembly().Location;
        var dir = Path.GetDirectoryName(asm);
        if (!string.IsNullOrEmpty(dir))
        {
            var up = Directory.GetParent(dir)?.FullName;
            foreach (var candidate in new[] { dir, up })
            {
                if (candidate is null) continue;
                var p = Path.Combine(candidate, "7zipContext.exe");
                if (File.Exists(p)) return p;
            }
        }
        return "7zipContext.exe";
    }

    private static void Launch(string verb, IReadOnlyList<string> paths)
    {
        var exe = ResolveAppExe();
        Log($"拉起: {exe} {verb} ({paths.Count} 项)");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(verb);
        foreach (var p in paths) psi.ArgumentList.Add(p);
        Process.Start(psi);
    }

    internal static void Log(string msg)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "7zipContext-classic.log");
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"))
              .Append(" [pid=").Append(Environment.ProcessId).Append(' ')
              .Append(Path.GetFileNameWithoutExtension(Environment.ProcessPath)).Append("] ")
              .AppendLine(msg);
            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch { /* 日志失败不影响菜单 */ }
    }
}

/// <summary>
/// 菜单位图缓存：文件夹 / 文件 / 本程序图标，各取一张，进程生命周期内不释放。
/// 菜单不拥有位图，必须由我们保活；三张 16x16 的泄漏是刻意接受的代价。
/// </summary>
internal static class Icons
{
    private static readonly Dictionary<(bool Dir, int Depth), IntPtr> Cache = new();
    private static IntPtr _app;
    private static byte[]? _dirPx;
    private static byte[]? _filePx;
    private static int _sz;
    private static bool _loaded;

    /// <summary>动词与标题统一用本程序图标。</summary>
    public static IntPtr App { get { Ensure(); return _app; } }

    /// <summary>
    /// 树行图标：菜单位图永远画在条目最左侧，所以缩进必须做进位图里 ——
    /// 生成一张「左边补 depth*16 透明、图标靠右」的宽位图，图标才会跟着层级右移。
    /// </summary>
    public static IntPtr ForNode(bool isDir, int depth)
    {
        Ensure();
        var key = (isDir, depth);
        if (Cache.TryGetValue(key, out var bmp)) return bmp;

        var src = isDir ? _dirPx : _filePx;
        if (src is null || _sz <= 0) return IntPtr.Zero;

        bmp = Compose(src, depth);
        Cache[key] = bmp;
        return bmp;
    }

    private static void Ensure()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            // SHGFI_USEFILEATTRIBUTES + 假路径在本机实测返回 0（拿不到图标），
            // 所以优先用系统里的真实路径取图标；失败再退回假路径。
            _dirPx = Pixels(Environment.SystemDirectory, NativeMethods.FILE_ATTRIBUTE_DIRECTORY, false)
                  ?? Pixels("C:////__7zipContext_probe__", NativeMethods.FILE_ATTRIBUTE_DIRECTORY, true);

            var sampleFile = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
            _filePx = Pixels(sampleFile, NativeMethods.FILE_ATTRIBUTE_NORMAL, false)
                   ?? Pixels("C:////__7zipContext_probe__.zzz", NativeMethods.FILE_ATTRIBUTE_NORMAL, true);

            var asm = Assembly.GetExecutingAssembly().Location;
            var dir = Path.GetDirectoryName(asm);
            if (dir is not null)
            {
                foreach (var candidate in new[] { dir, Directory.GetParent(dir)?.FullName })
                {
                    if (candidate is null) continue;
                    var appExe = Path.Combine(candidate, "7zipContext.exe");
                    if (System.IO.File.Exists(appExe))
                    {
                        var px = Pixels(appExe, 0, false);
                        if (px is not null) _app = Compose(px, 0);
                        break;
                    }
                }
            }
        }
        catch { /* 拿不到图标就退回纯文字，不影响可用性 */ }
        finally { PreviewMenu.Log($"icons: dir={(_dirPx is not null)} file={(_filePx is not null)} app=0x{_app:X} size={_sz}"); }
    }

    /// <summary>取图标的 32bpp 像素（BGRA）。失败返回 null。</summary>
    private static byte[]? Pixels(string? path, uint attributes, bool useAttributes)
    {
        var sfi = new SHFILEINFO();
        var cb = (uint)Marshal.SizeOf<SHFILEINFO>();
        var flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON;
        if (useAttributes) flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;

        var hr = NativeMethods.SHGetFileInfoW(path, attributes, ref sfi, cb, flags);
        if (hr == IntPtr.Zero || sfi.hIcon == IntPtr.Zero)
        {
            PreviewMenu.Log($"SHGetFileInfo 失败 path={path ?? "(null)"} useAttr={useAttributes}");
            return null;
        }

        try { return ToPixels(sfi.hIcon, out _sz); }
        finally { NativeMethods.DestroyIcon(sfi.hIcon); }
    }

    /// <summary>HICON → 32bpp BGRA 像素。DDB 与丢 alpha 的坑都在这里绕开。</summary>
    private static byte[]? ToPixels(IntPtr hIcon, out int size)
    {
        size = 0;
        if (!NativeMethods.GetIconInfo(hIcon, out var ii)) return null;

        IntPtr hdc = IntPtr.Zero;
        try
        {
            if (ii.hbmColor == IntPtr.Zero) return null;

            var bm = new NativeMethods.BITMAP();
            if (NativeMethods.GetObjectW(ii.hbmColor, Marshal.SizeOf<NativeMethods.BITMAP>(), ref bm) == 0) return null;
            int cx = bm.bmWidth, cy = bm.bmHeight;
            if (cx <= 0 || cy <= 0) return null;

            hdc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return null;

            var bi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = cx,
                    biHeight = -cy,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                },
            };

            var px = new byte[cx * cy * 4];
            if (NativeMethods.GetDIBits(hdc, ii.hbmColor, 0, (uint)cy, px, ref bi, 0) == 0) return null;

            size = cx;
            return px;
        }
        catch { return null; }
        finally
        {
            if (hdc != IntPtr.Zero) NativeMethods.DeleteDC(hdc);
            if (ii.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmColor);
        }
    }

    /// <summary>把 16x16 图标画到一张 (depth+1)x16 的透明 DIB 上，形成层级缩进。</summary>
    private static IntPtr Compose(byte[] src, int depth)
    {
        int sz = _sz > 0 ? _sz : 16;
        int w = sz * (depth + 1), h = sz;

        IntPtr hdc = IntPtr.Zero;
        try
        {
            hdc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return IntPtr.Zero;

            var bi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                },
            };

            var bmp = NativeMethods.CreateDIBSection(hdc, ref bi, 0, out var bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;

            // 先清成全透明
            var canvas = new byte[w * h * 4];
            int xoff = depth * sz;
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(src, y * sz * 4, canvas, (y * w + xoff) * 4, sz * 4);
            }
            Marshal.Copy(canvas, 0, bits, canvas.Length);
            return bmp; // 故意不释放：菜单不拥有它
        }
        catch { return IntPtr.Zero; }
        finally { if (hdc != IntPtr.Zero) NativeMethods.DeleteDC(hdc); }
    }
}
