using ZipContext.Core;

// 7zipContext 冒烟测试：智能解压两条规则 + 「完全成功才删包」红线 + 格式/密码覆盖。
// 用法：dotnet run --project tests/7zipContext.SmokeTest

int passed = 0, failed = 0;
void Check(string name, bool ok, string? detail = null)
{
    if (ok) { passed++; Console.WriteLine($"  PASS  {name}"); }
    else { failed++; Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : " — " + detail)}"); }
}

var work = Path.Combine(Path.GetTempPath(), "7zipContext-smoke-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
Console.WriteLine($"工作目录: {work}");

var svc = new SevenZipService();
// 冒烟测试必须走控制台通道：GUI 通道在遇到加密包时会弹 7zG 的密码框，测试会挂死等输入
svc.UseGui = false;
var smart = new SmartExtractService(svc);

// ---------- 制作测试包 ----------
async Task MakeZip(string zipName, params string[] entries)
{
    // entries 形如 "docs/a.txt" —— 用临时目录组装后压缩
    var stage = Path.Combine(work, "stage-" + Guid.NewGuid().ToString("N")[..6]);
    foreach (var e in entries)
    {
        var full = Path.Combine(stage, e.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, $"content of {e}");
    }
    await svc.CompressAsync("zip", Path.Combine(work, zipName),
        Directory.GetDirectories(stage).Concat(Directory.GetFiles(stage)).Select(Path.GetFileName)!,
        workingDirectory: stage);
    Directory.Delete(stage, true);
}

await MakeZip("single-folder.zip", "docs/a.txt", "docs/sub/b.txt");   // 规则一
await MakeZip("multi-root.zip", "a.txt", "docs/b.txt");                // 规则二
await MakeZip("single-file.zip", "readme.txt");                        // 规则二（单文件）
await MakeZip("中文名 包.zip", "文件夹/文件.txt", "说明.txt");          // 规则二 + 中文/空格
await MakeZip("nested.zip", "outer/inner/deep/c.txt");                 // 深层路径

Console.WriteLine("\n[1] 智能解压计划（规则判定）");
{
    var e1 = await svc.ListAsync(Path.Combine(work, "single-folder.zip"));
    var p1 = SmartExtractService.Plan(e1, Path.Combine(work, "single-folder.zip"));
    Check("规则一：单根文件夹 → 解到「此处」", p1.UseSingleFolderRule && p1.WrapperFolderName is null);

    var e2 = await svc.ListAsync(Path.Combine(work, "multi-root.zip"));
    var p2 = SmartExtractService.Plan(e2, Path.Combine(work, "multi-root.zip"));
    Check("规则二：多条目 → 建同名文件夹", !p2.UseSingleFolderRule && p2.WrapperFolderName == "multi-root");

    var e3 = await svc.ListAsync(Path.Combine(work, "single-file.zip"));
    var p3 = SmartExtractService.Plan(e3, Path.Combine(work, "single-file.zip"));
    Check("规则二：单个根文件 → 也建同名文件夹", !p3.UseSingleFolderRule && p3.WrapperFolderName == "single-file");

    var e5 = await svc.ListAsync(Path.Combine(work, "nested.zip"));
    var p5 = SmartExtractService.Plan(e5, Path.Combine(work, "nested.zip"));
    Check("深层路径正确归入规则一", p5.UseSingleFolderRule);
}

Console.WriteLine("\n[2] 智能解压执行 + 文件落点");
{
    var r1 = await smart.ExtractAsync(Path.Combine(work, "single-folder.zip"), deleteAfter: false);
    Check("规则一解出文件夹本身", r1.Success &&
        Directory.Exists(Path.Combine(work, "docs")) &&
        File.Exists(Path.Combine(work, "docs", "a.txt")) &&
        File.Exists(Path.Combine(work, "docs", "sub", "b.txt")) &&
        !Directory.Exists(Path.Combine(work, "single-folder")));

    var r2 = await smart.ExtractAsync(Path.Combine(work, "multi-root.zip"), deleteAfter: false);
    Check("规则二建同名文件夹", r2.Success &&
        File.Exists(Path.Combine(work, "multi-root", "a.txt")) &&
        File.Exists(Path.Combine(work, "multi-root", "docs", "b.txt")));

    var r3 = await smart.ExtractAsync(Path.Combine(work, "single-file.zip"), deleteAfter: false);
    Check("单文件落同名文件夹", r3.Success &&
        File.Exists(Path.Combine(work, "single-file", "readme.txt")));

    var r4 = await smart.ExtractAsync(Path.Combine(work, "中文名 包.zip"), deleteAfter: false);
    Check("中文名/空格压缩包（规则二：建同名文件夹）", r4.Success &&
        File.Exists(Path.Combine(work, "中文名 包", "文件夹", "文件.txt")) &&
        File.Exists(Path.Combine(work, "中文名 包", "说明.txt")));
}

Console.WriteLine("\n[3] 「并删除」红线：完全成功才删、失败绝不删");
{
    // 成功场景 → 包进回收站（文件消失）
    var before = File.Exists(Path.Combine(work, "single-folder.zip"));
    var rOk = await smart.ExtractAsync(Path.Combine(work, "single-folder.zip"), deleteAfter: true);
    Check("成功后删除压缩包", before && rOk.Success && rOk.ArchiveDeleted &&
        !File.Exists(Path.Combine(work, "single-folder.zip")));

    // 失败场景：坏包 → 不许删
    var bad = Path.Combine(work, "bad.zip");
    await File.WriteAllTextAsync(bad, "this is not a zip");
    var rBad = await smart.ExtractAsync(bad, deleteAfter: true);
    Check("坏包解压失败", !rBad.Success);
    Check("失败绝不删包（红线）", File.Exists(bad));

    // 坏包但写进一半的情况：目标目录里已有的好内容不许破坏
    var damaged = Path.Combine(work, "damaged.zip");
    var goodBytes = await File.ReadAllBytesAsync(Path.Combine(work, "multi-root.zip"));
    var truncated = goodBytes[..(goodBytes.Length / 2)];
    await File.WriteAllBytesAsync(damaged, truncated);
    var rDmg = await smart.ExtractAsync(damaged, deleteAfter: true);
    Check("截断包失败不删", !rDmg.Success && File.Exists(damaged));
}

Console.WriteLine("\n[4] 加密包与密码");
{
    var enc = Path.Combine(work, "secret.7z");
    var stage = Path.Combine(work, "enc-stage");
    Directory.CreateDirectory(stage);
    await File.WriteAllTextAsync(Path.Combine(stage, "secret.txt"), "top secret");
    await svc.CompressAsync("7z", enc, new[] { stage }, password: "123456");
    Directory.Delete(stage, true);

    var needPwd = false;
    try { await svc.ListAsync(enc, password: null); }
    catch (PasswordRequiredException) { needPwd = true; }
    Check("无密码读取加密包抛 PasswordRequired", needPwd);

    var entries = await svc.ListAsync(enc, password: "123456");
    Check("正确密码可读", entries.Any(e => e.Path.Contains("secret.txt")));

    var wrongPwd = false;
    try { await svc.ListAsync(enc, password: "wrong"); }
    catch (PasswordRequiredException) { wrongPwd = true; }
    catch (SevenZipException) { wrongPwd = true; } // 7z 也可能以普通失败退出
    Check("错误密码被拒", wrongPwd);
}

Console.WriteLine("\n[5] 列表解析与树构造");
{
    var entries = await svc.ListAsync(Path.Combine(work, "nested.zip"));
    var root = new TreeNode { Name = "(root)", IsFolder = true };
    foreach (var e in entries) root.Insert(e);
    root.SortRecursive();
    Check("树：outer/inner/deep/c.txt 三层结构", root.Children.Count == 1 &&
        root.Children[0].Name == "outer" &&
        root.Children[0].Children[0].Name == "inner" &&
        root.Children[0].Children[0].Children[0].Name == "deep" &&
        root.Children[0].Children[0].Children[0].Children[0].Name == "c.txt");
}

Console.WriteLine("\n[6] 压缩输出命名规则（菜单文字与输出一致）");
{
    var one = NamingHelper.TargetBaseName(new[] { @"C:\x\report.docx" });
    var many = NamingHelper.TargetBaseName(new[] { @"C:\x\folder\a.txt", @"C:\x\folder\b.txt" });
    var z = NamingHelper.ExtractFolderName(@"C:\x\backup.tar.gz");
    Check("单选 → 同名", one == "report");
    Check("多选 → 父文件夹名", many == "folder");
    Check("tar.gz 复合扩展", z == "backup");
}

Console.WriteLine("\n[7] 分卷文件识别与菜单去重");
{
    var first = Path.Combine(work, "split.7z.001");
    var later = Path.Combine(work, "split.7z.002");
    var normal = Path.Combine(work, "notes.txt");
    await File.WriteAllTextAsync(first, "first");
    await File.WriteAllTextAsync(later, "later");
    await File.WriteAllTextAsync(normal, "normal");

    Check(".001 识别为可分卷压缩包", ArchiveFormats.IsArchive(first) && ArchiveFormats.IsFirstSplitVolume(first));
    Check(".002 不当独立压缩包", !ArchiveFormats.IsArchive(later) && !ArchiveFormats.IsFirstSplitVolume(later));
    Check("多选 .001 + .002 显示解压", ArchiveSelection.CanExtract(new[] { first, later }));
    Check("多选 .001 + 普通文件也显示解压", ArchiveSelection.CanExtract(new[] { first, normal }));
    Check("单选 .002 不显示解压", !ArchiveSelection.CanExtract(new[] { later }));

    var targets = ArchiveSelection.TargetsForExtraction(new[] { first, later, normal });
    Check("该选择仅有首卷可执行", targets.Count == 1 &&
        string.Equals(targets[0], first, StringComparison.OrdinalIgnoreCase));

    Check("分卷菜单命名去掉 .001 和 .7z",
        ArchiveFormats.StripExtension("split.7z.001") == "split" &&
        NamingHelper.ExtractFolderName(first) == "split");
    Check("分卷组删除范围包含后续卷", ArchiveFormats.SplitVolumeFiles(first).Count == 2 &&
        ArchiveFormats.SplitVolumeFiles(first).Any(p =>
            string.Equals(p, later, StringComparison.OrdinalIgnoreCase)));
}

Console.WriteLine("\n[8] 分卷规则可配置");
{
    Check("默认首卷后缀含 .001/.z01",
        ArchiveRules.Current.SplitFirstSuffixes.Contains(".001") &&
        ArchiveRules.Current.SplitFirstSuffixes.Contains(".z01"));
    Check("默认开启「用 7-Zip 打开」", ArchiveRules.Current.OpenWithSevenZip);
    Check("缺少末尾 ZIP 的 .z01 不作为解压入口", !ArchiveFormats.IsFirstSplitVolume("weird.z01"));

    var first = Path.Combine(work, "weird.part1.rar");
    var later = Path.Combine(work, "weird.part2.rar");
    await File.WriteAllTextAsync(first, "first");
    await File.WriteAllTextAsync(later, "later");

    var firstSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".part1.rar" };
    var laterSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".part2.rar" };
    var group = SplitVolumeNames.GroupFiles(first, firstSet, laterSet);
    Check("自定义分卷后缀可找同组", group.Count == 2 &&
        group.Any(p => string.Equals(p, later, StringComparison.OrdinalIgnoreCase)));
    Check("自定义分卷命名去掉分卷后缀",
        SplitVolumeNames.StripSplitSuffix(first, firstSet).EndsWith("weird", StringComparison.OrdinalIgnoreCase));
}

Console.WriteLine("\n[9] 分卷删除范围与保留源文件");
{
    var firstSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".001", ".z01", ".part1.rar", ".custom" };
    var laterSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".002", ".z02", ".part2.rar", ".later" };
    string Fixture(string dir, string name)
    {
        var path = Path.Combine(work, dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, name);
        return path;
    }
    SplitVolumeNames.DeletionPlan Deletion(string path) => SplitVolumeNames.PlanDeletion(path, firstSet, laterSet);

    var numeric = Fixture("families", "照片.001");
    Fixture("families", "照片.002");
    var unrelatedZip = Fixture("families", "照片.z02");
    var unrelatedRar = Fixture("families", "照片.part2.rar");
    var numbered = Deletion(numeric);
    Check("数字卷不会混入同名 ZIP/RAR 家族", numbered.CanDelete && numbered.Files.Count == 2 &&
        !numbered.Files.Contains(unrelatedZip) && !numbered.Files.Contains(unrelatedRar));
    var rar = Fixture("families", "照片.part1.rar");
    var rarPlan = Deletion(rar);
    Check("RAR 家族不会混入同名数字卷", rarPlan.CanDelete && rarPlan.Files.Count == 2 && !rarPlan.Files.Contains(numeric));

    var large = Fixture("large-group", "many.001");
    for (var i = 2; i <= 1002; i++) Fixture("large-group", $"many.{i:000}");
    var largePlan = Deletion(large);
    Check("超过 999 卷不会截断", largePlan.CanDelete && largePlan.Files.Count == 1002);
    File.Delete(Path.Combine(Path.GetDirectoryName(large)!, "many.500"));
    var gap = Deletion(large);
    Check("编号空缺拒绝删除且不漏掉后段", !gap.CanDelete && gap.Files.Count == 1001 &&
        gap.Files.Any(p => p.EndsWith(".1002")));

    var ambiguous = Fixture("padding", "same.001");
    Fixture("padding", "same.002");
    Fixture("padding", "same.02");
    Check("同编号不同补零方式保留", !Deletion(ambiguous).CanDelete);
    var zipFirst = Fixture("traditional-zip", "bundle.z01");
    Fixture("traditional-zip", "bundle.z02");
    var zipLast = Fixture("traditional-zip", "bundle.zip");
    Check("传统分卷 ZIP 首卷保留", !Deletion(zipFirst).CanDelete);
    Check("传统分卷 ZIP 末尾包也保留", !Deletion(zipLast).CanDelete);
    var custom = Fixture("custom-group", "bundle.custom");
    Fixture("custom-group", "bundle.later");
    Check("未知非数字分卷保留且不猜测配对", !Deletion(custom).CanDelete && Deletion(custom).Files.Count == 1);
    var customNumeric = Fixture("custom-numbered", "bundle.chunk1.bin");
    Fixture("custom-numbered", "bundle.chunk2.bin");
    Check("未知数字家族可枚举但不删除", !SplitVolumeNames.PlanDeletion(customNumeric,
        new(StringComparer.OrdinalIgnoreCase) { ".chunk1.bin" }, laterSet).CanDelete);
    var laterRar = Fixture("unconfigured", "bundle.part2.rar");
    Check("未配置的常见后续 RAR 卷也拒绝删除", !Deletion(laterRar).CanDelete);
    var paddedRar = Fixture("padded-rar", "bundle.part0001.rar");
    Fixture("padded-rar", "bundle.part0002.rar");
    Check("常见 RAR 补零家族保护不依赖配置", Deletion(paddedRar).CanDelete && Deletion(paddedRar).Files.Count == 2);

    // 独立 ZIP 旁有同名 .z01：引擎可解压，但命名无法确认是否为同一组，必须保留。
    var disguised = Path.Combine(work, "preserved.zip");
    File.Copy(Path.Combine(work, "single-file.zip"), disguised);
    var uncertainSibling = Fixture(".", "preserved.z01");
    var preserved = await smart.ExtractAsync(disguised, deleteAfter: true);
    Check("成功解压但不可靠分卷保留并告警", preserved.Success && !preserved.ArchiveDeleted &&
        preserved.Error?.Contains("保留") == true && File.Exists(disguised) && File.Exists(uncertainSibling));

    // 用真实引擎生成数字分卷，验证成功才删整组且同名其他格式仍在。
    var realDir = Path.Combine(work, "real-volumes");
    Directory.CreateDirectory(realDir);
    await File.WriteAllBytesAsync(Path.Combine(realDir, "payload.bin"),
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(8192));
    var split = Path.Combine(realDir, "real.7z");
    var psi = new System.Diagnostics.ProcessStartInfo(EngineLocator.Require("7z.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { "a", "-t7z", "-mx=0", "-v1k", split, Path.Combine(realDir, "payload.bin") })
        psi.ArgumentList.Add(arg);
    using var proc = System.Diagnostics.Process.Start(psi)!;
    await Task.WhenAll(proc.StandardOutput.ReadToEndAsync(), proc.StandardError.ReadToEndAsync(), proc.WaitForExitAsync());
    Check("真实数字分卷夹具创建成功", proc.ExitCode == 0 && File.Exists(split + ".002"));
    Check("普通 ZIP 不伪造分卷数量", await svc.ReadVolumeCountAsync(Path.Combine(work, "single-file.zip")) is null);
    Check("引擎报告实际数字分卷数量", await svc.ReadVolumeCountAsync(split + ".001") ==
        Deletion(split + ".001").Files.Count);
    var foreign = Fixture("real-volumes", "real.7z.z02");
    var realResult = await smart.ExtractAsync(split + ".001", deleteAfter: true);
    Check("真实分卷成功后删除完整数字组", realResult.Success && realResult.ArchiveDeleted &&
        !Directory.EnumerateFiles(realDir).Any(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"\.7z\.[0-9]+$")));
    Check("真实解压删除不影响其他家族", File.Exists(foreign));
}

Console.WriteLine("\n[10] 真实传统分卷 ZIP 入口");
{
    var split = TraditionalZipFixture.Create(Path.Combine(work, "traditional-real"), "照片.v1");
    var entries = await svc.ListAsync(split.Last);
    Check("传统 ZIP 末尾入口列出两卷内容", entries.Count == 2 &&
        TraditionalZipFixture.Contents.Keys.All(name => entries.Any(e => e.Path == name)));
    var dest = Path.Combine(work, "traditional-extracted");
    var extracted = await svc.ExtractAsync(split.Last, dest);
    Check("传统 ZIP 从末尾入口真实解压且内容一致", extracted.Success &&
        TraditionalZipFixture.Contents.All(p => File.Exists(Path.Combine(dest, p.Key)) &&
            File.ReadAllText(Path.Combine(dest, p.Key)) == p.Value));
    var firstReadable = false;
    try { var firstEntries = await svc.ListAsync(split.First); firstReadable = firstEntries.Count == 2; }
    catch (SevenZipException) { }
    Console.WriteLine($"  INFO  engine can list complete contents via .z01: {firstReadable}");
    Check("完整 .z01 被规范为末尾 ZIP", ArchiveFormats.ExtractionEntry(split.First) == split.Last);
    Check("单选末尾 ZIP 可显示解压", ArchiveSelection.CanExtract(new[] { split.Last }));
    Check("单选完整 .z01 可显示解压", ArchiveSelection.CanExtract(new[] { split.First }));
    Check("后续 .z02 不单独显示解压", !ArchiveFormats.IsArchive(split.Middle) &&
        !ArchiveSelection.CanExtract(new[] { split.Middle }));
    var targets = ArchiveSelection.TargetsForExtraction(new[] { split.First, split.Middle, split.Last, split.First });
    Check("整组多选只执行末尾 ZIP 一次", targets.Count == 1 && targets[0] == split.Last &&
        ArchiveSelection.CanExtract(new[] { split.First, split.Middle, split.Last }));
    Check("末尾 ZIP 与后续卷混选也只执行一次", ArchiveSelection.TargetsForExtraction(new[] { split.Last, split.Middle })
        .SequenceEqual(new[] { split.Last }) && ArchiveSelection.CanExtract(new[] { split.Last, split.Middle }));
    Check("传统 ZIP 名称保留中间点号", NamingHelper.TargetBaseName(targets) == "照片.v1");
    var preserved = await smart.ExtractAsync(targets[0], deleteAfter: true);
    Check("真实传统 ZIP 解压成功但保留全部卷", preserved.Success && !preserved.ArchiveDeleted &&
        preserved.Error?.Contains("保留") == true && File.Exists(split.First) && File.Exists(split.Middle) && File.Exists(split.Last));

    var lastBytes = File.ReadAllBytes(split.Last);
    File.Delete(split.Last);
    Check("缺少末尾 ZIP 隐藏解压入口", !ArchiveFormats.IsArchive(split.First) &&
        !ArchiveSelection.CanExtract(new[] { split.First, split.Middle }) &&
        ArchiveSelection.TargetsForExtraction(new[] { split.First, split.Middle }).Count == 0);
    File.WriteAllBytes(split.Last, lastBytes);
    var middleBytes = File.ReadAllBytes(split.Middle);
    File.Delete(split.Middle);
    var incomplete = await svc.TestAsync(split.Last);
    Check("真实 ZIP 缺少中间卷完整性检查失败", !incomplete.Success && File.Exists(split.First) && File.Exists(split.Last));
    File.WriteAllBytes(split.Middle, middleBytes);

    var ini = ArchiveRules.IniPath!;
    var original = File.Exists(ini) ? File.ReadAllBytes(ini) : null;
    try
    {
        File.WriteAllText(ini, "[archive]\nsplitFirstSuffixes=.001,.z01,.part1.rar,.chunk1.bin\nsplitLaterSuffixes=.002,.z02,.part2.rar,.chunk2.bin\n");
        File.SetLastWriteTimeUtc(ini, DateTime.UtcNow.AddSeconds(1));
        Check("未知配置分卷不猜测解压入口", !ArchiveFormats.IsArchive("unknown.chunk1.bin") &&
            !ArchiveSelection.CanExtract(new[] { "unknown.chunk1.bin" }) &&
            ArchiveSelection.TargetsForExtraction(new[] { "unknown.chunk1.bin" }).Count == 0);
        Check("已配置 RAR 首卷仍支持", ArchiveFormats.IsArchive("known.part1.rar"));
        Check("RAR 后续卷按家族排除", !ArchiveFormats.IsArchive("known.part3.rar") &&
            !ArchiveFormats.IsArchive("known.part999.rar"));
        Check("传统卷识别忽略大小写", ArchiveFormats.IsTraditionalZipSegment("demo.Z02") &&
            !ArchiveFormats.IsArchive("demo.Z02"));
    }
    finally
    {
        if (original is null) File.Delete(ini);
        else { File.WriteAllBytes(ini, original); File.SetLastWriteTimeUtc(ini, DateTime.UtcNow.AddSeconds(2)); }
    }
}

Console.WriteLine($"\n========== 结果: {passed} 通过 / {failed} 失败 ==========");
Directory.Delete(work, true);
return failed == 0 ? 0 : 1;
