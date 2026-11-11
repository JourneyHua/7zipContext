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

public enum TreeStatus { Ok, Empty, Timeout, Unsupported, Error }

public readonly record struct TreeLine(int Depth, string Name, bool IsDir);

/// <summary>
/// 压缩包目录树提供者。
/// 硬约束（实测）：7z.exe 启动就要 120ms，条目按 8µs 累加，所以必须有硬超时。
/// .zip 走 ZipArchive 读中央目录，不起进程，毫秒级。
/// </summary>
internal static class ArchiveTree
{
    private const int TimeoutMs = 250;

    public static (TreeStatus Status, List<TreeLine> Lines) Read(string path)
    {
        var lines = new List<TreeLine>();
        try
        {
            if (!File.Exists(path)) return (TreeStatus.Error, lines);

            var isZip = Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(path).Equals(".zipx", StringComparison.OrdinalIgnoreCase);

            List<string> names;
            var status = TreeStatus.Ok;
            if (isZip) names = ReadZipNames(path);
            else names = Read7zNames(path, out status, out lines);

            if (status != TreeStatus.Ok) return (status, lines);
            if (names.Count == 0) return (TreeStatus.Empty, lines);

            return (TreeStatus.Ok, Render(BuildTree(names)));
        }
        catch (InvalidDataException) { return (TreeStatus.Error, lines); }
        catch (Exception) { return (TreeStatus.Error, lines); }
    }

    // ---------------- 取条目名 ----------------

    private static List<string> ReadZipNames(string path)
    {
        var list = new List<string>();
        using var zip = ZipFile.OpenRead(path);
        foreach (var e in zip.Entries)
            if (!string.IsNullOrEmpty(e.FullName))
                list.Add(e.FullName.Replace('\\', '/'));
        return list;
    }

    private static List<string> Read7zNames(string path, out TreeStatus status, out List<TreeLine> lines)
    {
        lines = new List<TreeLine>();
        var exe = SevenZipPath();
        if (exe is null) { status = TreeStatus.Unsupported; return new List<string>(); }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("l");
        psi.ArgumentList.Add("-slt");
        psi.ArgumentList.Add("-bd");
        psi.ArgumentList.Add(path);

        using var p = Process.Start(psi);
        if (p is null) { status = TreeStatus.Error; return new List<string>(); }

        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();

        if (!p.WaitForExit(TimeoutMs))
        {
            try { p.Kill(true); } catch { /* 杀不掉也没别的办法 */ }
            status = TreeStatus.Timeout;
            return new List<string>();
        }
        p.WaitForExit();

        var names = new List<string>();
        foreach (var raw in sb.ToString().Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("Path = ", StringComparison.Ordinal)) continue;
            var v = line[7..];
            // 跳过 7z 的头部信息块（Type/Physical Size/Headers Size... 之后的存档路径行）
            if (v.Length == 0 || v.Trim() == path) continue;
            if (v.StartsWith("--", StringComparison.Ordinal)) continue;
            names.Add(v.Replace('\\', '/'));
        }

        status = TreeStatus.Ok;
        return names;
    }

    /// <summary>
    /// 只走 EngineLocator（读本地指针/本机探测，微秒级、零网络）。
    /// 这里跑在 explorer.exe 进程内且有 250ms 硬超时，绝对不能联网或起下载。
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

    private static List<Node> BuildTree(List<string> names)
    {
        var roots = new List<Node>();
        var index = new Dictionary<string, Node>(StringComparer.Ordinal);

        foreach (var full in names)
        {
            var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            var bucket = roots;
            var acc = "";
            for (int i = 0; i < parts.Length; i++)
            {
                acc = acc.Length == 0 ? parts[i] : acc + "/" + parts[i];
                var isDir = i < parts.Length - 1 || full.EndsWith('/');

                if (!index.TryGetValue(acc, out var node))
                {
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
    private static List<TreeLine> Render(List<Node> roots)
    {
        // 默认规则（2026-11-10 实测定下来的，可被 7zipContext.ini 覆盖）：
        //   根目录 → 文件夹 + 文件全部显示，文件夹在前、同类按名称升序
        //   子层   → 只展开文件夹，每个文件夹只给前 3 个，不再往下递归
        var s = PreviewSettings.Current;
        var lines = new List<TreeLine>();

        void Walk(List<Node> nodes, int depth)
        {
            if (depth > s.MaxDepth) return;

            var limit = depth == 0
                ? (s.RootMax <= 0 ? int.MaxValue : s.RootMax)
                : s.ChildrenPerFolder;
            if (limit <= 0) return;

            var taken = 0;
            foreach (var n in Sorted(nodes))
            {
                if (taken >= limit) break;
                if (depth >= 1 && !n.IsDir && !s.ShowFilesInSubfolders) continue;

                lines.Add(new TreeLine(depth, n.Name, n.IsDir));
                taken++;

                // 子项必须紧跟在自己的父文件夹后面（保持层级直觉），
                // 堆到末尾会让人误以为是最后一个文件的子项。
                if (n.IsDir && n.Children.Count > 0) Walk(n.Children, depth + 1);
            }

            if (depth == 0 && taken < nodes.Count)
                lines.Add(new TreeLine(0, $"…另有 {nodes.Count - taken} 项", false));
        }

        Walk(roots, 0);
        return lines;
    }

    private static IEnumerable<Node> Sorted(List<Node> nodes) =>
        nodes.OrderByDescending(n => n.IsDir)
             .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
}
