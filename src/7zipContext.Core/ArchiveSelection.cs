namespace ZipContext.Core;

/// <summary>
/// 右键菜单选中项 → 解压目标 的统一规则。
/// 已支持的分卷入口统一规范化；传统 ZIP 的 .z01 与末尾 .zip 合并为一次执行。
/// 分卷首卷与普通压缩包共同执行；后续卷不作为独立目标。
/// </summary>
public static class ArchiveSelection
{
    /// <summary>当前选择是否应该显示解压动词。</summary>
    public static bool CanExtract(IReadOnlyList<string> paths)
    {
        return TargetsForExtraction(paths).Count != 0;
    }

    /// <summary>把选择折算成真正交给 CLI/7-Zip 的压缩包列表。</summary>
    public static IReadOnlyList<string> TargetsForExtraction(IReadOnlyList<string> paths)
    {
        return paths
            .Select(ArchiveFormats.ExtractionEntry)
            .OfType<string>()
            .DistinctBy(path => Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
