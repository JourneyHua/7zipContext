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
    Check("执行只保留首卷", targets.Count == 1 &&
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
    Check(".z01 识别为首卷", ArchiveFormats.IsFirstSplitVolume("weird.z01"));

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

Console.WriteLine($"\n========== 结果: {passed} 通过 / {failed} 失败 ==========");
Directory.Delete(work, true);
return failed == 0 ? 0 : 1;
