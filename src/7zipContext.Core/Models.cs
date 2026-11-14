using System.Text.RegularExpressions;

namespace ZipContext.Core;

/// <summary>压缩包内单个条目（文件或文件夹）。</summary>
public sealed record ArchiveEntry(
    string Path,          // 包内相对路径，使用 '/' 分隔
    bool IsFolder,
    long Size,
    long PackedSize,
    DateTime? Modified,
    bool Encrypted);

/// <summary>包内目录树节点（预览窗 / 主窗口共用）。</summary>
public sealed class TreeNode
{
    public required string Name { get; init; }
    public string FullPath { get; init; } = "";
    // 同一路径既是文件又被用作目录（包内有 a 和 a/b）时需要把节点升级为目录。
    public bool IsFolder { get; set; }
    public long Size { get; set; }
    public long PackedSize { get; set; }
    public bool Encrypted { get; set; }
    private readonly List<TreeNode> _children = new();
    private readonly Dictionary<(string Name, bool Folder), TreeNode> _index = new();
    private bool _exposed;
    // Preserve the mutable List API. Once exposed, rebuild before insertion because
    // a retained caller reference can change even without another getter call.
    public List<TreeNode> Children { get { _exposed = true; return _children; } }

    private TreeNode? Find(string name, bool folder)
    {
        if (_exposed)
        {
            _index.Clear();
            foreach (var child in _children) _index.TryAdd((child.Name, child.IsFolder), child);
        }
        return _index.GetValueOrDefault((name, folder));
    }

    /// <summary>插入一条以 '/' 分隔的路径，自动建中间目录节点。</summary>
    public void Insert(ArchiveEntry entry)
    {
        var parts = entry.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cur = this;
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            var wantedFolder = !last || entry.IsFolder;
            var child = cur.Find(parts[i], wantedFolder);
            if (child is null && !last)
            {
                // 包内同时有文件 a 和 a/b 时，中间层不能另建同名节点，否则预览出现两行 "a"。
                child = cur.PromoteToFolder(cur.Find(parts[i], folder: false));
            }
            if (child is null)
            {
                child = new TreeNode
                {
                    Name = parts[i],
                    FullPath = string.Join('/', parts.Take(i + 1)),
                    IsFolder = wantedFolder,
                };
                cur._children.Add(child);
                cur._index.Add((child.Name, child.IsFolder), child);
            }
            if (last)
            {
                child.Size = entry.Size;
                child.PackedSize = entry.PackedSize;
                child.Encrypted = entry.Encrypted;
            }
            cur = child;
        }
    }

    /// <summary>把同名文件节点升级为目录，并同步索引键。</summary>
    private TreeNode? PromoteToFolder(TreeNode? existing)
    {
        if (existing is null) return null;
        if (!existing.IsFolder)
        {
            _index.Remove((existing.Name, false));
            existing.IsFolder = true;
            _index[(existing.Name, true)] = existing;
        }
        return existing;
    }

    /// <summary>按「文件夹在前、名称排序」整理整棵树。</summary>
    public void SortRecursive()
    {
        _children.Sort((a, b) =>
        {
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        foreach (var c in _children) c.SortRecursive();
    }

    /// <summary>根层级条目（用于智能解压判定）。</summary>
    public IReadOnlyList<TreeNode> RootEntries => Children;
}

public static class ArchiveFormats
{
    public static readonly HashSet<string> Extractable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz",
        ".zst", ".cab", ".iso", ".wim", ".arj", ".lzh", ".lha", ".z", ".taz", ".tpz",
        ".zipx", ".xpi", ".jar", ".apk", ".ar", ".cpio", ".rpm", ".deb", ".msi",
        // 分卷首卷不写死：由 [archive] splitFirstSuffixes 配置，默认 .001/.z01
    };

    /// <summary>复合扩展名（.tar.gz 等）。返回去掉压缩后缀的基础名。</summary>
    public static string StripExtension(string fileName)
    {
        // 分卷首卷要先去掉配置里的分卷后缀，再走普通/复合扩展名规则：
        // xxx.7z.001 → xxx；xxx.part1.rar → xxx；xxx.tar.gz.001 → xxx。
        var rules = ArchiveRules.Current;
        fileName = SplitVolumeNames.StripSplitSuffix(fileName, rules.SplitFirstSuffixes);

        var lower = fileName.ToLowerInvariant();
        foreach (var compound in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst" })
            if (lower.EndsWith(compound))
                return fileName[..^compound.Length];
        return Path.GetFileNameWithoutExtension(fileName);
    }

    public static bool IsArchive(string path)
        => ExtractionEntry(path) is not null;

    /// <summary>解析实际入口：传统 ZIP 从末尾 .zip 进入，未知分卷格式不猜测。</summary>
    public static string? ExtractionEntry(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (IsTraditionalZipSegment(path))
            return IsFirstSplitVolume(path) ? Path.ChangeExtension(path, ".zip") : null;
        if (IsLaterSplitVolume(path)) return null;
        var configuredFirst = SplitVolumeNames.MatchSuffix(path, ArchiveRules.Current.SplitFirstSuffixes);
        if (configuredFirst is not null)
            return IsFirstSplitVolume(path) ? path : null;
        if (Extractable.Contains(Path.GetExtension(path))) return path;

        var lower = path.ToLowerInvariant();
        return lower.EndsWith(".tar.gz") || lower.EndsWith(".tar.bz2") ||
               lower.EndsWith(".tar.xz") || lower.EndsWith(".tar.zst") ? path : null;
    }

    public static bool IsTraditionalZipSegment(string path) =>
        !string.IsNullOrWhiteSpace(path) && Regex.IsMatch(
            Path.GetExtension(path), @"^\.z[0-9]{2,}$", RegexOptions.IgnoreCase);

    /// <summary>本地配套文件探测，无目录扫描、网络或子进程。</summary>
    public static bool IsTraditionalZipEntry(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(Path.ChangeExtension(path, ".z01"));

    /// <summary>已支持的分卷入口候选；.z01 需要配套 .zip，实际执行时规范到后者。</summary>
    public static bool IsFirstSplitVolume(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (IsTraditionalZipEntry(path)) return true;
        var suffix = SplitVolumeNames.MatchSuffix(path, ArchiveRules.Current.SplitFirstSuffixes);
        if (suffix is null) return false;
        if (suffix.Equals(".z01", StringComparison.OrdinalIgnoreCase))
            return File.Exists(Path.ChangeExtension(path, ".zip"));
        return suffix.Equals(".001", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(suffix, @"^\.part0*1\.rar$", RegexOptions.IgnoreCase);
    }

    /// <summary>是否是分卷后续卷（.002/.z02 等）。后续卷不能单独作为压缩包打开。</summary>
    public static bool IsLaterSplitVolume(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !IsFirstSplitVolume(path) &&
        (IsTraditionalZipSegment(path) ||
         SplitVolumeNames.HasSuffix(path, ArchiveRules.Current.SplitLaterSuffixes) ||
         Regex.IsMatch(path, @"\.part[0-9]+\.rar$", RegexOptions.IgnoreCase));

    /// <summary>分卷组的基础名（去掉配置的分卷后缀），用于找同组文件。</summary>
    public static string SplitVolumeBasePath(string path)
    {
        var rules = ArchiveRules.Current;
        var full = Path.GetFullPath(path);
        var stripped = SplitVolumeNames.StripSplitSuffix(full, rules.SplitFirstSuffixes);
        return SplitVolumeNames.StripSplitSuffix(stripped, rules.SplitLaterSuffixes);
    }

    /// <summary>
    /// 取同一分卷组的全部已存在文件。传入非首卷时只返回该文件自身，避免把后续卷当成整组。
    /// 只枚举同一编号家族，不跨格式合并；自动删除还须检查 PlanDeletion.CanDelete。
    /// </summary>
    public static IReadOnlyList<string> SplitVolumeFiles(string path)
    {
        var rules = ArchiveRules.Current;
        return SplitVolumeNames.GroupFiles(path, rules.SplitFirstSuffixes, rules.SplitLaterSuffixes);
    }

    /// <summary>格式化文件大小（1.2 GB 形式）。</summary>
    public static string HumanSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}
