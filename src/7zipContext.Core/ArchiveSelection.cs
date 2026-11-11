namespace ZipContext.Core;

/// <summary>
/// 右键菜单选中项 → 解压目标 的统一规则。
/// 分卷文件：只要选中任一 .001 首卷，就显示解压；执行时只保留首卷，
/// .002 等后续卷和同选的普通文件都忽略，避免同一组分卷被重复解压。
/// </summary>
public static class ArchiveSelection
{
    /// <summary>当前选择是否应该显示解压动词。</summary>
    public static bool CanExtract(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return false;
        return paths.Any(ArchiveFormats.IsFirstSplitVolume) ||
               paths.All(ArchiveFormats.IsArchive);
    }

    /// <summary>把选择折算成真正交给 CLI/7-Zip 的压缩包列表。</summary>
    public static IReadOnlyList<string> TargetsForExtraction(IReadOnlyList<string> paths)
    {
        var firstVolumes = paths
            .Where(ArchiveFormats.IsFirstSplitVolume)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (firstVolumes.Count > 0) return firstVolumes;

        return paths
            .Where(ArchiveFormats.IsArchive)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
