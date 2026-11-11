namespace ZipContext.Core;

/// <summary>智能解压规则（Q5 拍板语义）：
/// 规则一：压缩包根层级只有一个文件夹条目 → 直接解出这个文件夹本身（不套压缩包同名目录）。
/// 规则二：根层级有多个条目（文件或文件夹）→ 创建以压缩包名命名的文件夹，全部装进去。
/// 「智能解压到此处」的「此处」= 压缩包所在目录。
/// 「并删除」= 仅当解压完全成功后，把压缩包删进回收站。
/// </summary>
public sealed class SmartExtractService
{
    private readonly SevenZipService _sevenZip;

    public SmartExtractService(SevenZipService sevenZip) => _sevenZip = sevenZip;

    public sealed record SmartExtractPlan(
        string DestDir,                 // 实际解压到的目录
        string? WrapperFolderName,      // 规则二时创建的文件夹名；规则一为 null
        bool UseSingleFolderRule);

    /// <summary>根据包内结构计算解压计划（不实际解压，可先给用户看）。</summary>
    public static SmartExtractPlan Plan(IReadOnlyList<ArchiveEntry> entries, string archivePath)
    {
        var archiveDir = Path.GetDirectoryName(Path.GetFullPath(archivePath)) ?? ".";
        var name = ArchiveFormats.StripExtension(Path.GetFileName(archivePath));

        // 根层级去重后的条目
        var roots = entries
            .Select(e => e.Path.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 显式文件夹条目集合（7z 对纯目录会输出 Folder=+ 的条目）
        var folderRoots = entries
            .Where(e => e.IsFolder)
            .Select(e => e.Path.Split('/')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool singleRoot = roots.Count == 1;
        bool singleRootIsFolder = singleRoot && (folderRoots.Contains(roots[0]) ||
            // 若无显式目录条目，但所有条目都以 "<root>/" 开头，则 root 也是文件夹
            entries.All(e => e.Path.Contains('/')));

        if (singleRoot && singleRootIsFolder)
        {
            // 规则一：解到「此处」，直接得到 ./<那个文件夹>/
            return new SmartExtractPlan(archiveDir, null, true);
        }

        // 规则二（含单个根文件、空包）：建同名文件夹
        return new SmartExtractPlan(Path.Combine(archiveDir, name), name, false);
    }

    public sealed record SmartExtractResult(
        SmartExtractPlan Plan,
        bool Success,
        string? Error,
        bool ArchiveDeleted);

    /// <summary>执行智能解压；deleteAfter 成功后把压缩包删进回收站。</summary>
    public async Task<SmartExtractResult> ExtractAsync(string archivePath, bool deleteAfter,
        string? password = null, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        SmartExtractPlan plan;
        try
        {
            var entries = await _sevenZip.ListAsync(archivePath, password, ct);
            plan = Plan(entries, archivePath);
        }
        catch (PasswordRequiredException) { throw; }
        catch (Exception ex)
        {
            return new SmartExtractResult(
                new SmartExtractPlan(Path.GetDirectoryName(archivePath)!, null, false),
                false, $"读取压缩包结构失败：{ex.Message}", false);
        }

        progress?.Report($"正在解压到 {plan.DestDir} ...");
        var run = await _sevenZip.ExtractAsync(archivePath, plan.DestDir, password,
            progress: progress, ct: ct);

        if (!run.Success)
        {
            // 失败绝不删包
            return new SmartExtractResult(plan, false, DescribeFailure(run), false);
        }

        bool deleted = false;
        if (deleteAfter)
        {
            progress?.Report("解压成功，正在把压缩包移入回收站...");
            deleted = RecycleBinService.SendToRecycleBin(archivePath, out var delErr);
            if (!deleted)
                return new SmartExtractResult(plan, true, $"解压成功，但删除压缩包失败：{delErr}", false);
        }
        return new SmartExtractResult(plan, true, null, deleted);
    }

    private static string DescribeFailure(SevenZipService.RunResult run)
    {
        var text = string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr;
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase)
                              || l.Contains("错误"))
            ?.Trim();
        return $"解压失败（退出码 {run.ExitCode}）：{line ?? "未知错误"}";
    }
}
