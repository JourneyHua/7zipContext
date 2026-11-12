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
///   2. 预览有时间与数量预算；阻塞读取不放在菜单线程，超限降级成灰字。
///   3. 定位 exe 用 Assembly.Location 起算，不用 AppContext.BaseDirectory。
/// </summary>
[ComVisible(true)]
[Guid(Clsid)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class PreviewMenu : IShellExtInit, IContextMenu
{
    public const string Clsid = "B9A10020-8C3F-4A2E-9E6D-5F1A2B3C4D20";

    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const string PreviewTitle = "压缩文件预览";

    private readonly List<string> _paths = new();
    private readonly List<string?> _commands = new(); // ID 偏移 → CLI 动词；null 是不可执行的预览行
    private readonly Func<IntPtr, uint, uint, UIntPtr, string?, bool> _insert = NativeMethods.InsertMenuW;
    private readonly List<string> _extractTargets = new(); // 实际解压目标：分卷只保留 .001
    private string? _archive;

    public PreviewMenu() { }

    // 允许测试在真实、未显示的 HMENU 上模拟部分插入失败，不修改全局 Win32 状态。
    internal PreviewMenu(Func<IntPtr, uint, uint, UIntPtr, string?, bool> insert) => _insert = insert;

    // ---------------- IShellExtInit ----------------

    public int Initialize(IntPtr pidlFolder, IntPtr pDataObj, IntPtr hKeyProgID)
    {
        try
        {
            _archive = null;
            _paths.Clear();
            _commands.Clear();
            _extractTargets.Clear();

            foreach (var p in ShellData.SelectedPaths(pDataObj))
                if (File.Exists(p) || Directory.Exists(p)) _paths.Add(p);

            // 解压目标统一走 Core：分卷入口和普通压缩包共同处理，后续卷不重复解压。
            _extractTargets.AddRange(ZipContext.Core.ArchiveSelection.TargetsForExtraction(_paths));

            // 只有「单选且是压缩包」才做预览树；其余只给压缩动词
            if (_paths.Count == 1 && ArchiveFormats.IsArchive(_paths[0]))
                _archive = ZipContext.Core.ArchiveFormats.ExtractionEntry(_paths[0]);
        }
        catch (Exception ex) { Log("Initialize: " + ex); }
        return 0;
    }

    // ---------------- IContextMenu ----------------

    public int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags)
    {
        try
        {
            _commands.Clear();
            // 本 handler 不改变默认动作，也不支持字符串规范动词调用。
            if ((uFlags & (0x00000001u | 0x00000800u)) != 0) return 0;
            if (_paths.Count == 0) return 0;
            var first = unchecked((uint)idCmdFirst);
            var last = unchecked((uint)idCmdLast);
            if (last < first) return 0;
            var available = (ulong)last - first + 1;
            bool CanAdd() => (ulong)_commands.Count < available && _commands.Count < ushort.MaxValue;
            uint pos = indexMenu;
            bool AddEntry(string text, string? verb, uint flags, IntPtr bmp)
            {
                if (!CanAdd()) return false;
                var id = (uint)((ulong)first + (uint)_commands.Count);
                if (!AddItem(hMenu, ref pos, flags, text, bmp, (UIntPtr)id)) return false;
                _commands.Add(verb); // 只有 Win32 确认插入成功，才分配偏移。
                return true;
            }

            var icon = Icons.App;
            var baseName = ZipContext.Core.NamingHelper.TargetBaseName(_paths);
            if (baseName.Length > 32) baseName = baseName[..32] + "…";

            // 分卷多选（xxx.7z.001 + xxx.7z.002）也要显示解压组；
            // 普通多选压缩包保持经典菜单原有“单选才给解压”的行为，不悄悄扩大范围。
            var showExtract = _archive is not null ||
                (_paths.Count > 1 && _paths.Any(ZipContext.Core.ArchiveFormats.IsFirstSplitVolume));
            var extractBaseName = ZipContext.Core.NamingHelper.TargetBaseName(_extractTargets);
            if (extractBaseName.Length > 32) extractBaseName = extractBaseName[..32] + "…";

            if (!AddItem(hMenu, ref pos, NativeMethods.MF_SEPARATOR, null, IntPtr.Zero)) return 0;

            if (showExtract)
            {
                var extract = new (string Text, string Verb)[]
                {
                    ("智能解压到此处(S)", "--smart-extract"),
                    ("智能解压到此处并删除", "--smart-extract-delete"),
                    ($"解压到 {extractBaseName}\\(T)", "--extract-to-name"),
                    ("解压到其他文件夹(B)...", "--extract-to"),
                };
                foreach (var (text, verb) in extract)
                {
                    if (!AddEntry(text, verb, NativeMethods.MF_STRING, icon)) return _commands.Count;
                }
            }

            // 压缩动词：普通文件/文件夹也显示（这七条全由我们自己插，菜单才会连成一组）
            foreach (var verb in new[] { "--add-zip", "--add-7z" })
            {
                var ext = verb["--add-".Length..];
                if (!AddEntry($"添加到 {baseName}.{ext}", verb, NativeMethods.MF_STRING, icon)) return _commands.Count;
            }

            // 独立「用 7-Zip 打开」：所有文件/文件夹都可点，可在 ini 里关闭
            if (ZipContext.Core.ArchiveRules.Current.OpenWithSevenZip)
            {
                if (!AddEntry("用 7-Zip 打开(O)", "--open-with-7zip", NativeMethods.MF_STRING, icon)) return _commands.Count;
            }

            if (_archive is null || !CanAdd() || available - (ulong)_commands.Count < 2) return _commands.Count;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!AddItem(hMenu, ref pos, NativeMethods.MF_SEPARATOR, null, IntPtr.Zero) ||
                !AddEntry(PreviewTitle, null, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, icon))
                return _commands.Count;
            var (status, lines) = ArchiveTree.ReadWithBudget(_archive,
                Math.Min(PreviewBudget.ReadMilliseconds, PreviewBudget.TotalMilliseconds - (int)sw.ElapsedMilliseconds));
            var readElapsed = sw.ElapsedMilliseconds;
            var previewLog = $"preview {Path.GetFileName(_archive)} status={status} lines={lines.Count} elapsed={readElapsed}ms";
            ThreadPool.QueueUserWorkItem(_ => Log(previewLog));

            if (status != TreeStatus.Ok)
            {
                AddEntry(StatusText(status), null,
                    NativeMethods.MF_STRING | NativeMethods.MF_GRAYED,
                    IntPtr.Zero);
                return _commands.Count;
            }

            foreach (var line in lines)
            {
                if (!CanAdd()) break;
                if (sw.ElapsedMilliseconds >= PreviewBudget.TotalMilliseconds)
                {
                    AddEntry("（其余预览已省略：处理超时）", null,
                        NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, IntPtr.Zero);
                    break;
                }
                // 缩进已经做进位图（ForNode 按 depth 补透明），文字不再加全角空格，
                // 否则图标贴最左、文字被推远，看起来是散的。
                if (!AddEntry(line.Name, null,
                    NativeMethods.MF_STRING | NativeMethods.MF_GRAYED,
                    Icons.ForNode(line.IsDir, line.Depth))) break;
            }

            return _commands.Count;
        }
        catch (Exception ex)
        {
            Log("QueryContextMenu: " + ex);
            return _commands.Count; // 已插入的 ID 仍须报告，避免后续 handler 重用。
        }
    }

    public int InvokeCommand(ref CMINVOKECOMMANDINFO pici)
    {
        try
        {
            if (pici.cbSize < Marshal.SizeOf<CMINVOKECOMMANDINFO>()) return E_INVALIDARG;
            var verb = unchecked((ulong)pici.lpVerb.ToInt64());
            if (verb > ushort.MaxValue) return E_FAIL; // 字符串或高位指针不是整数偏移。

            var idx = (int)verb;
            if (idx >= _commands.Count || _commands[idx] is not { } command) return E_FAIL;

            // 解压动词只收首卷；压缩动词仍使用完整选择。
            var targets = IsExtractVerb(command) ? _extractTargets : _paths;
            if (targets.Count == 0) return E_FAIL;
            Launch(command, targets);
            return 0;
        }
        catch (Exception ex)
        {
            Log("InvokeCommand: " + ex);
            return E_FAIL;
        }
    }

    public int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax)
        => E_NOTIMPL;

    // ---------------- 内部 ----------------

    private bool AddItem(IntPtr hMenu, ref uint pos, uint flags, string? text,
        IntPtr bmp, UIntPtr id = default)
    {
        if (!_insert(hMenu, pos, flags | NativeMethods.MF_BYPOSITION, id, text))
        {
            Log($"InsertMenuW 失败 flags=0x{flags:X} text={text}");
            return false;
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
        return true;
    }

    private static string StatusText(TreeStatus s) => s switch
    {
        TreeStatus.Timeout => "（预览已省略：处理超时）",
        TreeStatus.Omitted => "（预览已省略：超出安全上限或已有读取任务）",
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

    private static bool IsExtractVerb(string verb) => verb is
        "--smart-extract" or "--smart-extract-delete" or
        "--extract-to-name" or "--extract-to";

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
            ZipContext.Core.BoundedLog.TryAppend(path, sb.ToString());
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
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(bool Dir, int Depth), Lazy<IntPtr>> Cache = new();
    private static IntPtr _app;
    private static byte[]? _dirPx;
    private static byte[]? _filePx;
    private static int _sz;
    private static int _loaded;
    private static int _loading;

    /// <summary>动词与标题统一用本程序图标。</summary>
    public static IntPtr App { get { RequestLoad(); return Volatile.Read(ref _loaded) == 1 ? _app : IntPtr.Zero; } }

    /// <summary>
    /// 树行图标：菜单位图永远画在条目最左侧，所以缩进必须做进位图里 ——
    /// 生成一张「左边补 depth*16 透明、图标靠右」的宽位图，图标才会跟着层级右移。
    /// </summary>
    public static IntPtr ForNode(bool isDir, int depth)
    {
        RequestLoad();
        if (Volatile.Read(ref _loaded) == 0) return IntPtr.Zero;
        var key = (isDir, Math.Clamp(depth, 0, 2));

        var src = isDir ? _dirPx : _filePx;
        if (src is null || _sz <= 0) return IntPtr.Zero;

        return Cache.GetOrAdd(key, k => new Lazy<IntPtr>(() => Compose(src, k.Item2))).Value;
    }

    // 首次 Shell 图标查询可能触发 IO/第三方 handler；本次菜单可先显示文字，下次取缓存。
    private static void RequestLoad()
    {
        if (Interlocked.CompareExchange(ref _loading, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ => Ensure());
    }

    private static void Ensure()
    {
        var comResult = -1;
        try
        {
            // Shell 图标查询要求当前线程先初始化 COM；线程池按 MTA 使用。
            comResult = CoInitializeEx(IntPtr.Zero, 0);
            if (comResult < 0) return;
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
        finally
        {
            if (comResult >= 0) CoUninitialize();
            Volatile.Write(ref _loaded, 1);
            PreviewMenu.Log($"icons: dir={(_dirPx is not null)} file={(_filePx is not null)} app=0x{_app:X} size={_sz}");
        }
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
