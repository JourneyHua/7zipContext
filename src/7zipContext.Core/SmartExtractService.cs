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
        bool ArchiveDeleted,
        int ExitCode = 0);

    /// <summary>执行智能解压；deleteAfter 成功后把压缩包删进回收站。</summary>
    public async Task<SmartExtractResult> ExtractAsync(string archivePath, bool deleteAfter,
        string? password = null, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var rules = ArchiveRules.Current;
        // 路径非法等非 IO 异常也会从 PlanDeletion 抛出；删除计划失败只表示「不能删」，不能中断解压。
        SplitVolumeNames.DeletionPlan? deletion = null;
        if (deleteAfter)
        {
            try { deletion = SplitVolumeNames.PlanDeletion(archivePath, rules.SplitFirstSuffixes, rules.SplitLaterSuffixes); }
            catch (Exception ex)
            {
                // 兜底路径不能再用可能抛错的重载去拼目录名：这条路本身就是异常路径。
                string dir;
                try { dir = Path.GetDirectoryName(Path.GetFullPath(archivePath)) ?? "."; }
                catch { dir = "."; }
                return new SmartExtractResult(
                    new SmartExtractPlan(dir, null, false),
                    false, $"无法确定分卷删除范围，已取消本次操作：{ex.Message}", false, 2);
            }
        }
        SmartExtractPlan plan;
        try
        {
            var entries = await _sevenZip.ListAsync(archivePath, password, ct);
            plan = Plan(entries, archivePath);
        }
        catch (PasswordRequiredException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new SmartExtractResult(
                new SmartExtractPlan(Path.GetDirectoryName(archivePath)!, null, false),
                false, $"读取压缩包结构失败：{ex.Message}", false,
                ex is SevenZipException { ExitCode: int code } ? code : 2);
        }

        progress?.Report($"正在解压到 {plan.DestDir} ...");
        var run = await _sevenZip.ExtractAsync(archivePath, plan.DestDir, password,
            progress: progress, ct: ct);

        if (!run.Success)
        {
            // 失败绝不删包
            return new SmartExtractResult(plan, false, DescribeFailure(run), false, run.ExitCode);
        }

        bool deleted = false;
        if (deleteAfter)
        {
            if (deletion is not { CanDelete: true })
                return new SmartExtractResult(plan, true,
                    $"解压成功，但已保留全部源文件：{deletion?.Reason}", false);
            if (deletion.Files.Count > 1 || SplitVolumeNames.HasSuffix(archivePath, rules.SplitFirstSuffixes) ||
                System.Text.RegularExpressions.Regex.IsMatch(archivePath, @"\.(?:[0-9]+|part[0-9]+\.rar)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                int? usedVolumes;
                try { usedVolumes = await _sevenZip.ReadVolumeCountAsync(archivePath, password, ct); }
                catch (OperationCanceledException) { throw; }
                catch { usedVolumes = null; }
                if (usedVolumes != deletion.Files.Count)
                    return new SmartExtractResult(plan, true,
                        "解压成功，但引擎报告的分卷数量缺失或与同名文件不一致，已保留全部源文件。", false);
            }
            var current = SplitVolumeNames.PlanDeletion(archivePath,
                rules.SplitFirstSuffixes, rules.SplitLaterSuffixes);
            if (!current.CanDelete || !deletion.Files.SequenceEqual(current.Files, StringComparer.OrdinalIgnoreCase))
                return new SmartExtractResult(plan, true,
                    "解压成功，但分卷组在解压期间发生变化，已保留全部源文件。", false);
            ct.ThrowIfCancellationRequested();
            progress?.Report("解压成功，正在把分卷文件移入回收站...");
            // 分卷包要删整组：只删 .001 会留下 .002 等孤儿文件。
            var volumeFiles = deletion.Files;
            string? deleteError = null;
            var allDeleted = true;
            foreach (var file in volumeFiles)
            {
                if (RecycleBinService.SendToRecycleBin(file, out var oneError)) continue;
                allDeleted = false;
                deleteError ??= $"{Path.GetFileName(file)}：{oneError}";
            }

            if (!allDeleted)
                return new SmartExtractResult(plan, true, $"解压成功，但删除分卷文件失败：{deleteError}", false);

            deleted = true;
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
