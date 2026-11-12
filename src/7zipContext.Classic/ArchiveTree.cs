using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ZipContext.Classic;

/// <summary>从 IDataObject 取选中文件（CF_HDROP）。</summary>
internal static class ShellData
{
    private const int CF_HDROP = 15;

    public static List<string> SelectedPaths(IntPtr pDataObj)
    {
        var result = new List<string>();
        if (pDataObj == IntPtr.Zero) return result;

        object? com = null;
        try
        {
            com = Marshal.GetObjectForIUnknown(pDataObj);
            if (com is not IDataObject dobj) return result;

            var fe = new FORMATETC
            {
                cfFormat = (short)CF_HDROP,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED.TYMED_HGLOBAL,
            };

            STGMEDIUM sm;
            try { dobj.GetData(ref fe, out sm); }
            catch (COMException) { return result; } // 没有 CF_HDROP（不是文件选择）
            try
            {
                uint n = DragQueryFileW(sm.unionmember, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < n; i++)
                {
                    var sb = new StringBuilder(1024);
                    if (DragQueryFileW(sm.unionmember, i, sb, (uint)sb.Capacity) > 0)
                        result.Add(sb.ToString());
                }
            }
            finally
            {
                ReleaseStgMedium(ref sm);
            }
        }
        finally
        {
            if (com is not null) Marshal.ReleaseComObject(com);
        }
        return result;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);
}

internal static class ArchiveFormats
{
    // 识别规则统一由 Core 提供：普通压缩包 + .001 分卷首卷 + .tar.gz 等复合扩展。
    public static bool IsArchive(string path) => ZipContext.Core.ArchiveFormats.IsArchive(path);
}

public enum TreeStatus { Ok, Empty, Timeout, Unsupported, Error, Omitted }

public readonly record struct TreeLine(int Depth, string Name, bool IsDir);

/// <summary>
/// 压缩包目录树提供者。
/// 读取、配置、建树和排序共用预算；菜单线程只进行有上限的等待。
/// 同时最多一个工作任务，阻塞 IO 即使无法及时取消，也不能堆积后台线程。
/// </summary>
internal static class ArchiveTree
{
    private static readonly SemaphoreSlim ReaderSlot = new(1, 1);

    public static (TreeStatus Status, List<TreeLine> Lines) Read(string path) =>
        ReadWithBudget(path, PreviewBudget.TotalMilliseconds);

    public static (TreeStatus Status, List<TreeLine> Lines) ReadWithBudget(string path, int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        if (milliseconds <= 0) return (TreeStatus.Timeout, new());
        if (!ReaderSlot.Wait(0)) return (TreeStatus.Omitted, new());
        var cancellation = new CancellationTokenSource();
        var budget = new PreviewBudget(Math.Min(milliseconds, PreviewBudget.TotalMilliseconds), cancellation.Token);
        Task<(TreeStatus Status, List<TreeLine> Lines)> task;
        try
        {
            task = Task.Run(() =>
            {
                try { return ReadCore(path, budget); }
                finally { cancellation.Dispose(); ReaderSlot.Release(); }
            });
        }
        catch { cancellation.Dispose(); ReaderSlot.Release(); return (TreeStatus.Error, new()); }
        try
        {
            var remaining = Math.Min(milliseconds, PreviewBudget.TotalMilliseconds) - (int)clock.ElapsedMilliseconds;
            if (remaining > 0 && task.Wait(remaining)) return task.GetAwaiter().GetResult();
            // 不在菜单线程同步执行取消回调（终止子进程也可能耗时）。
            try { _ = cancellation.CancelAsync(); } catch (ObjectDisposedException) { }
            return (TreeStatus.Timeout, new());
        }
        catch { return (TreeStatus.Error, new()); }
    }

    private static (TreeStatus Status, List<TreeLine> Lines) ReadCore(string path, PreviewBudget budget)
    {
        var lines = new List<TreeLine>();
        try
        {
            budget.Check();
            if (!File.Exists(path)) return (TreeStatus.Error, lines);
            if (new FileInfo(path).Length > PreviewBudget.MaxArchiveBytes) throw new PreviewLimitException();
            budget.Check();

            var isZip = Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(path).Equals(".zipx", StringComparison.OrdinalIgnoreCase);

            List<string> names;
            var status = TreeStatus.Ok;
            if (isZip && !ZipContext.Core.ArchiveFormats.IsTraditionalZipEntry(path)) names = ReadZipNames(path, budget);
            else names = Read7zNames(path, budget, out status);

            if (status != TreeStatus.Ok) return (status, lines);
            if (names.Count == 0) return (TreeStatus.Empty, lines);

            return (TreeStatus.Ok, Render(BuildTree(names, budget), budget));
        }
        catch (OperationCanceledException) { return (TreeStatus.Timeout, new()); }
        catch (InvalidOperationException ex) when (ex.InnerException is OperationCanceledException)
        { return (TreeStatus.Timeout, new()); }
        catch (PreviewLimitException) { return (TreeStatus.Omitted, new()); }
        catch (ZipContext.Core.SevenZipLimitException) { return (TreeStatus.Omitted, new()); }
        catch (InvalidDataException) { return (TreeStatus.Error, lines); }
        catch (Exception) { return (TreeStatus.Error, lines); }
    }

    // ---------------- 取条目名 ----------------

    private static List<string> ReadZipNames(string path, PreviewBudget budget)
    {
        var list = new List<string>();
        using var input = new PreviewReadStream(new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete), budget);
        CheckZipDirectory(input, budget);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        if (zip.Entries.Count > PreviewBudget.MaxEntries) throw new PreviewLimitException();
        foreach (var e in zip.Entries)
        {
            budget.Check();
            if (!string.IsNullOrEmpty(e.FullName))
                list.Add(e.FullName.Replace('\\', '/'));
        }
        return list;
    }

    // 在 ZipArchive 一次性载入中央目录前检查声明的条目数/大小；ZIP64 暂省略预览。
    private static void CheckZipDirectory(Stream input, PreviewBudget budget)
    {
        var tail = new byte[(int)Math.Min(input.Length, 65557)];
        input.Seek(-tail.Length, SeekOrigin.End);
        input.ReadExactly(tail);
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if ((i & 255) == 0) budget.Check();
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != 0x06054b50) continue;
            ushort U16(int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + offset));
            uint U32(int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + offset));
            if (i + 22 + U16(20) != tail.Length) continue;
            if (U16(4) != 0 || U16(6) != 0 || U16(8) != U16(10) ||
                U16(10) > PreviewBudget.MaxEntries || U32(12) > PreviewBudget.MaxDirectoryBytes ||
                U32(16) == uint.MaxValue) throw new PreviewLimitException();
            input.Position = 0;
            return;
        }
        throw new InvalidDataException();
    }

    private static List<string> Read7zNames(string path, PreviewBudget budget, out TreeStatus status)
    {
        budget.Check();
        var exe = SevenZipPath();
        if (exe is null) { status = TreeStatus.Unsupported; return new List<string>(); }

        // 共用 Core 的 UTF-8 列表解析、并发管道读取和取消清理。
        // Folder=+ 必须保留，否则空文件夹会被误画成文件。
        try
        {
            var entries = new ZipContext.Core.SevenZipService(exe)
                .ListAsync(path, ct: budget.Token, maxEntries: PreviewBudget.MaxEntries,
                    maxOutputChars: PreviewBudget.MaxOutputChars).GetAwaiter().GetResult();
            budget.Check();
            status = TreeStatus.Ok;
            return entries.Select(e => e.IsFolder ? e.Path.TrimEnd('/') + "/" : e.Path).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (ZipContext.Core.SevenZipLimitException) { throw; }
        catch
        {
            status = TreeStatus.Error;
            return new List<string>();
        }
    }

    /// <summary>
    /// 只走 EngineLocator（读本地指针/本机探测，微秒级、零网络）。
    /// 在受控预览工作线程读取；绝对不能联网或起下载。
    /// 找不到引擎时返回 null，由调用方降级成一行提示文字。
    /// </summary>
    private static string? SevenZipPath() => ZipContext.Core.EngineLocator.Resolve("7z.exe");

    // ---------------- 建树 + 渲染 ----------------

    private sealed class Node
    {
        public string Name = "";
        public bool IsDir;
        public List<Node> Children = new();
    }

    private static List<Node> BuildTree(List<string> names, PreviewBudget budget)
    {
        var roots = new List<Node>();
        var index = new Dictionary<string, Node>(StringComparer.Ordinal);

        var chars = 0;
        foreach (var full in names)
        {
            budget.Check();
            if (full.Length > PreviewBudget.MaxPathChars || (chars += full.Length) > PreviewBudget.MaxNameChars)
                throw new PreviewLimitException();
            var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > PreviewBudget.MaxPathDepth) throw new PreviewLimitException();
            if (parts.Length == 0) continue;

            var bucket = roots;
            var acc = "";
            for (int i = 0; i < parts.Length; i++)
            {
                budget.Check();
                acc = acc.Length == 0 ? parts[i] : acc + "/" + parts[i];
                var isDir = i < parts.Length - 1 || full.EndsWith('/');

                if (!index.TryGetValue(acc, out var node))
                {
                    if (index.Count >= PreviewBudget.MaxNodes) throw new PreviewLimitException();
                    node = new Node { Name = parts[i], IsDir = isDir };
                    index[acc] = node;
                    bucket.Add(node);
                }
                else if (isDir) node.IsDir = true;

                bucket = node.Children;
            }
        }
        return roots;
    }

    /// <summary>根目录最多显示多少条（防超大包把菜单撑到屏幕外）。</summary>
    private static List<TreeLine> Render(List<Node> roots, PreviewBudget budget)
    {
        // 默认规则（2026-11-10 实测定下来的，可被 7zipContext.ini 覆盖）：
        //   根目录 → 文件夹 + 文件全部显示，文件夹在前、同类按名称升序
        //   子层   → 只展开文件夹，每个文件夹只给前 3 个，不再往下递归
        var s = PreviewSettings.Current;
        budget.Check();
        var lines = new List<TreeLine>();
        void Add(TreeLine line)
        {
            budget.Check();
            if (lines.Count >= PreviewBudget.MaxLines) throw new PreviewLimitException();
            lines.Add(line);
        }

        void Walk(List<Node> nodes, int depth)
        {
            budget.Check();
            if (depth > s.MaxDepth) return;

            var limit = depth == 0
                ? (s.RootMax <= 0 ? int.MaxValue : s.RootMax)
                : s.ChildrenPerFolder;
            if (limit <= 0) return;

            var taken = 0;
            foreach (var n in Sorted(nodes, budget))
            {
                budget.Check();
                if (taken >= limit) break;
                if (depth >= 1 && !n.IsDir && !s.ShowFilesInSubfolders) continue;

                Add(new TreeLine(depth, n.Name, n.IsDir));
                taken++;

                // 子项必须紧跟在自己的父文件夹后面（保持层级直觉），
                // 堆到末尾会让人误以为是最后一个文件的子项。
                if (n.IsDir && n.Children.Count > 0) Walk(n.Children, depth + 1);
            }

            if (depth == 0 && taken < nodes.Count)
                Add(new TreeLine(0, $"…另有 {nodes.Count - taken} 项", false));
        }

        Walk(roots, 0);
        return lines;
    }

    private static IEnumerable<Node> Sorted(List<Node> nodes, PreviewBudget budget) =>
        nodes.OrderBy(n => n, Comparer<Node>.Create((a, b) =>
        {
            budget.Check();
            if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
            return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        }));
}
