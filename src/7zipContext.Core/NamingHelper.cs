namespace ZipContext.Core;

/// <summary>压缩目标命名规则（Q11）：单选 → 条目同名；多选 → 父文件夹名。Shell 与 App 共用，保证菜单文字与实际输出一致。</summary>
public static class NamingHelper
{
    public static string TargetBaseName(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return "archive";
        if (paths.Count == 1)
            return Sanitize(ArchiveFormats.StripExtension(Path.GetFileName(paths[0])));

        var parent = Path.GetDirectoryName(Path.GetFullPath(paths[0]));
        var name = parent is null ? "archive" : new DirectoryInfo(parent).Name;
        return Sanitize(name);
    }

    /// <summary>「解压到 xxx\」的 xxx：压缩包去扩展名。</summary>
    public static string ExtractFolderName(string archivePath)
        => Sanitize(ArchiveFormats.StripExtension(Path.GetFileName(archivePath)));

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "archive" : name;
    }
}
