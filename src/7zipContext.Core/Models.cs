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
    public bool IsFolder { get; init; }
    public long Size { get; set; }
    public long PackedSize { get; set; }
    public bool Encrypted { get; set; }
    public List<TreeNode> Children { get; } = new();

    /// <summary>插入一条以 '/' 分隔的路径，自动建中间目录节点。</summary>
    public void Insert(ArchiveEntry entry)
    {
        var parts = entry.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cur = this;
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            var child = cur.Children.FirstOrDefault(c => c.Name == parts[i] && c.IsFolder == (!last || entry.IsFolder));
            if (child is null)
            {
                child = new TreeNode
                {
                    Name = parts[i],
                    FullPath = string.Join('/', parts.Take(i + 1)),
                    IsFolder = !last || entry.IsFolder,
                };
                cur.Children.Add(child);
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

    /// <summary>按「文件夹在前、名称排序」整理整棵树。</summary>
    public void SortRecursive()
    {
        Children.Sort((a, b) =>
        {
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        foreach (var c in Children) c.SortRecursive();
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
        ".001", // 7-Zip 分卷首卷：xxx.7z.001 / xxx.zip.001
    };

    /// <summary>复合扩展名（.tar.gz 等）。返回去掉压缩后缀的基础名。</summary>
    public static string StripExtension(string fileName)
    {
        // 分卷首卷要先去掉 .001，再走普通/复合扩展名规则：
        // xxx.7z.001 → xxx；xxx.tar.gz.001 → xxx。
        if (IsFirstSplitVolume(fileName))
            fileName = fileName[..^".001".Length];

        var lower = fileName.ToLowerInvariant();
        foreach (var compound in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst" })
            if (lower.EndsWith(compound))
                return fileName[..^compound.Length];
        return Path.GetFileNameWithoutExtension(fileName);
    }

    public static bool IsArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Extractable.Contains(Path.GetExtension(path))) return true;

        var lower = path.ToLowerInvariant();
        return lower.EndsWith(".tar.gz") || lower.EndsWith(".tar.bz2") ||
               lower.EndsWith(".tar.xz") || lower.EndsWith(".tar.zst");
    }

    /// <summary>是否是分卷首卷（.001）。只有首卷能交给 7-Zip 识别整个分卷组。</summary>
    public static bool IsFirstSplitVolume(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.GetExtension(path).Equals(".001", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否是分卷后续卷（.002、.003…）。后续卷不能单独作为压缩包打开。</summary>
    public static bool IsLaterSplitVolume(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return ext.Length == 4 && ext[0] == '.' &&
               ext[1] is >= '0' and <= '9' &&
               ext[2] is >= '0' and <= '9' &&
               ext[3] is >= '0' and <= '9' &&
               !IsFirstSplitVolume(path);
    }

    /// <summary>分卷组的基础名（去掉 .001/.002 等数字后缀），用于找同组文件。</summary>
    public static string SplitVolumeBasePath(string path)
    {
        var full = Path.GetFullPath(path);
        var ext = Path.GetExtension(full);
        return ext.Length == 4 && ext[0] == '.' && char.IsDigit(ext[1]) &&
               char.IsDigit(ext[2]) && char.IsDigit(ext[3])
            ? full[..^ext.Length]
            : full;
    }

    /// <summary>
    /// 取同一分卷组的全部已存在文件（从 .001 开始连续查找）。
    /// 传入非首卷时只返回该文件自身，避免把 .002 当成整组。
    /// </summary>
    public static IReadOnlyList<string> SplitVolumeFiles(string path)
    {
        var first = Path.GetFullPath(path);
        if (!IsFirstSplitVolume(first)) return new[] { first };

        var dir = Path.GetDirectoryName(first) ?? "";
        var prefix = Path.GetFileName(SplitVolumeBasePath(first)) + ".";
        var files = new List<string> { first };
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{prefix}{i:000}");
            if (!File.Exists(candidate)) break;
            files.Add(candidate);
        }
        return files;
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
