using System.Diagnostics;
using System.Reflection;
using System.Text;
using ZipContext.App;
using ZipContext.Core;

if (args.FirstOrDefault() == "--engine-lock-fixture") return await EngineStateChecks.Child(args);
if (args.FirstOrDefault() == "--deployment-fixture") return await DeploymentChecks.Child(args);
if (CliChecks.Child(args) is int cliFixtureCode) return cliFixtureCode;

// 子进程夹具：用自身模拟会写满 stderr 或长时间运行的 7z。
if (args.FirstOrDefault() == "l")
{
    var source = args.First(a => a.EndsWith(".fixture", StringComparison.Ordinal));
    if (Path.GetFileName(source) == "flood.fixture")
    {
        File.WriteAllText(source, Environment.ProcessId.ToString());
        Console.Write(new string('X', 8 * 1024 * 1024));
        Console.Out.Flush();
        await Task.Delay(30_000);
    }
    else if (Path.GetFileName(source) == "wait.fixture")
    {
        File.WriteAllText(source, Environment.ProcessId.ToString());
        await Task.Delay(30_000);
    }
    else
    {
        Console.Error.Write(new string('E', 256 * 1024));
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("----------\nPath = empty\nFolder = +\n\nPath = 文件.txt\nSize = 1\n");
    }
    return 0;
}

int passed = 0, failed = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
    if (ok) passed++; else failed++;
}

var work = Path.Combine(Path.GetTempPath(), "7zipContext-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
RuntimeUpgradeChecks.Run(work, Check);
var ini = AppSettings.IniPath; // 仅操作本测试输出目录里的配置。
var original = File.Exists(ini) ? File.ReadAllBytes(ini) : null;
var previewType = typeof(ZipContext.Classic.PreviewMenu).Assembly.GetType("ZipContext.Classic.PreviewSettings")!;
object Preview() => previewType.GetProperty("Current")!.GetValue(null)!;
int RootMax() => (int)previewType.GetProperty("RootMax")!.GetValue(Preview())!;
void WriteIni(string text)
{
    File.WriteAllText(ini, text);
    // 文件系统时间精度不应影响缓存测试。
    File.SetLastWriteTimeUtc(ini, DateTime.UtcNow.AddSeconds(passed + failed + 1));
}

try
{
    Check("inline comments", IniValue.Read("20 ; comment") == "20" && IniValue.Read("TRUE # comment") == "TRUE");
    Check("suffix semicolon separator", IniValue.Read(".001;.z01") == ".001;.z01");
    WriteIni("[preview]\nrootMax=7 ; count\nchildrenPerFolder=2 # count\nmaxDepth=2\n[archive]\nsplitFirstSuffixes=.part1.rar ; first\nsplitLaterSuffixes=.part2.rar ; later\nopenWithSevenZip=TRUE ; enabled\n");
    var settings = AppSettings.Load();
    Check("settings numeric comments", settings.RootMax == 7 && settings.ChildrenPerFolder == 2);
    Check("classic numeric comments", RootMax() == 7);
    Check("archive boolean casing", ArchiveRules.Current.OpenWithSevenZip && settings.OpenWithSevenZip);
    Check("archive suffix comments", ArchiveRules.Current.SplitFirstSuffixes.SetEquals(new[] { ".part1.rar" }));
    Check("later rar never extractable", !ArchiveFormats.IsArchive("example.part2.rar"));
    Check("first rar extractable", ArchiveFormats.IsArchive("example.part1.rar"));
    Check("longest suffix wins", SplitVolumeNames.StripSplitSuffix("example.part1.rar",
        new HashSet<string> { ".rar", ".part1.rar" }) == "example");
    settings.Save();
    Check("settings round trip", AppSettings.Load().RootMax == 7 && AppSettings.Load().SplitFirstSuffixes == ".part1.rar");
    Check("atomic save leaves no temp", Directory.GetFiles(Path.GetDirectoryName(ini)!, "7zipContext.ini.*.tmp").Length == 0);
    File.Delete(ini);
    Check("deleted archive config resets cache", ArchiveRules.Current.SplitFirstSuffixes.Contains(".001"));
    Check("deleted preview config resets cache", RootMax() == 20);

    var payload = Path.Combine(work, "payload");
    var installed = Path.Combine(work, "installed");
    Directory.CreateDirectory(payload);
    Directory.CreateDirectory(installed);
    File.WriteAllText(Path.Combine(payload, "7zipContext.ini"), "default");
    File.WriteAllText(Path.Combine(installed, "7zipContext.ini"), "custom");
    File.WriteAllText(Path.Combine(payload, "program.dll"), "new");
    typeof(ZipContext.Setup.SetupWindow).GetMethod("CopyDirectory", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, new object[] { payload, installed });
    Check("repair preserves installed configuration", File.ReadAllText(Path.Combine(installed, "7zipContext.ini")) == "custom");
    Check("repair updates other payload files", File.ReadAllText(Path.Combine(installed, "program.dll")) == "new");

    var service = new SevenZipService(Environment.ProcessPath) { UseGui = false };
    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    {
        var entries = await service.ListAsync(Path.Combine(work, "pipes.fixture"), ct: timeout.Token);
        Check("large stderr does not deadlock", entries.Count == 2);
        Check("UTF-8 names survive pipe", entries.Any(e => e.Path == "文件.txt"));
        Check("empty directory flag", entries.Any(e => e.Path == "empty" && e.IsFolder));
    }
    var waiting = Path.Combine(work, "wait.fixture");
    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    {
        var limited = false;
        try { await service.ListAsync(Path.Combine(work, "pipes.fixture"), ct: timeout.Token, maxEntries: 1); }
        catch (SevenZipLimitException) { limited = true; }
        Check("engine listing entry cap enforced", limited);
        var flood = Path.Combine(work, "flood.fixture");
        limited = false;
        try { await service.ListAsync(flood, ct: timeout.Token, maxOutputChars: 4096); }
        catch (SevenZipLimitException) { limited = true; }
        Check("engine output cap fails without pipe deadlock", limited);
        var exited = false;
        if (File.Exists(flood))
        {
            try { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(flood))); exited = child.WaitForExit(5000); }
            catch (ArgumentException) { exited = true; }
        }
        Check("engine output cap terminates producer", exited);
    }
    using (var cancellation = new CancellationTokenSource())
    {
        var task = new SmartExtractService(service).ExtractAsync(waiting, deleteAfter: true, ct: cancellation.Token);
        var sw = Stopwatch.StartNew();
        while (!File.Exists(waiting) && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        Check("cancel fixture started", File.Exists(waiting));
        cancellation.Cancel();
        var cancelled = false;
        try { await task; }
        catch (OperationCanceledException) { cancelled = true; }
        Check("smart extraction propagates cancellation", cancelled);
        if (File.Exists(waiting))
        {
            var pid = int.Parse(File.ReadAllText(waiting));
            var exited = false;
            try { using var child = Process.GetProcessById(pid); exited = child.WaitForExit(5000); }
            catch (ArgumentException) { exited = true; }
            Check("cancel kills child process", exited);
            Check("cancel never deletes source", File.Exists(waiting));
        }
    }

    var engine = EngineLocator.Resolve("7z.exe");
    if (engine is not null)
    {
        var real = new SevenZipService(engine) { UseGui = false };
        var empty = Path.Combine(work, "empty-folder");
        Directory.CreateDirectory(empty);
        var zip = Path.Combine(work, "empty-folder.zip");
        await real.CompressAsync("zip", zip, new[] { "empty-folder" }, workingDirectory: work);
        var entries = await real.ListAsync(zip);
        Check("real ZIP empty directory recognized", entries.Count == 1 && entries[0].IsFolder);
        var treeType = previewType.Assembly.GetType("ZipContext.Classic.ArchiveTree")!;
        (ZipContext.Classic.TreeStatus Status, List<ZipContext.Classic.TreeLine> Lines) ReadTree(string path) =>
            ((ZipContext.Classic.TreeStatus, List<ZipContext.Classic.TreeLine>))treeType.GetMethod("Read")!
                .Invoke(null, new object[] { path })!;
        var archive = Path.Combine(work, "empty-folder.7z");
        await real.CompressAsync("7z", archive, new[] { "empty-folder" }, workingDirectory: work);
        var result = ((ZipContext.Classic.TreeStatus Status, List<ZipContext.Classic.TreeLine> Lines))
            treeType.GetMethod("Read")!.Invoke(null, new object[] { archive })!;
        Check("real 7z preview empty directory", result.Status == ZipContext.Classic.TreeStatus.Ok &&
            result.Lines.Any(l => l.Name == "empty-folder" && l.IsDir));
        var bad = Path.Combine(work, "broken.7z");
        File.WriteAllText(bad, "broken");
        result = ((ZipContext.Classic.TreeStatus, List<ZipContext.Classic.TreeLine>))
            treeType.GetMethod("Read")!.Invoke(null, new object[] { bad })!;
        Check("broken preview not reported empty/ok", result.Status is ZipContext.Classic.TreeStatus.Error or ZipContext.Classic.TreeStatus.Timeout);

        var traditional = TraditionalZipFixture.Create(Path.Combine(work, "traditional-preview"), "preview");
        result = ((ZipContext.Classic.TreeStatus, List<ZipContext.Classic.TreeLine>))
            treeType.GetMethod("Read")!.Invoke(null, new object[] { traditional.Last })!;
        Check("traditional split ZIP preview uses engine", result.Status == ZipContext.Classic.TreeStatus.Ok &&
            result.Lines.Any(l => l.Name == "first.txt") && result.Lines.Any(l => l.Name == "目录" && l.IsDir));

        var zeroBudget = ((ZipContext.Classic.TreeStatus Status, List<ZipContext.Classic.TreeLine> Lines))
            treeType.GetMethod("ReadWithBudget")!.Invoke(null, new object[] { traditional.Last, 0 })!;
        Check("exhausted preview budget returns no lines", zeroBudget.Status == ZipContext.Classic.TreeStatus.Timeout && zeroBudget.Lines.Count == 0);
        var slot = (SemaphoreSlim)treeType.GetField("ReaderSlot", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var shortClock = Stopwatch.StartNew();
        var shortBudget = ((ZipContext.Classic.TreeStatus Status, List<ZipContext.Classic.TreeLine> Lines))
            treeType.GetMethod("ReadWithBudget")!.Invoke(null, new object[] { traditional.Last, 1 })!;
        Check("caller stops waiting when live preview budget expires", shortBudget.Status == ZipContext.Classic.TreeStatus.Timeout &&
            shortClock.ElapsedMilliseconds < 500);
        var ownsSlot = slot.Wait(1000);
        Check("preview worker slot released after timeout cancellation", ownsSlot);
        if (ownsSlot)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var busy = ReadTree(traditional.Last);
                Check("busy preview omits immediately without extra worker", busy.Status == ZipContext.Classic.TreeStatus.Omitted &&
                    busy.Lines.Count == 0 && sw.ElapsedMilliseconds < 100);
            }
            finally { slot.Release(); }
        }

        string MakePreviewZip(string name, int count, Func<int, string> entryName)
        {
            var path = Path.Combine(work, name + ".zip");
            using var file = File.Create(path);
            using var output = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create);
            for (var i = 0; i < count; i++) output.CreateEntry(entryName(i));
            return path;
        }
        var manyEntries = MakePreviewZip("many-entries", 10001, i => $"{i}.txt");
        var omitted = ReadTree(manyEntries);
        Check("ZIP entry count rejected before full tree", omitted.Status == ZipContext.Classic.TreeStatus.Omitted && omitted.Lines.Count == 0);

        WriteIni("[preview]\nrootMax=0\nchildrenPerFolder=3\nmaxDepth=1\n");
        var manyLines = MakePreviewZip("many-lines", 201, i => $"{i}.txt");
        omitted = ReadTree(manyLines);
        Check("rootMax zero cannot disable menu line cap", omitted.Status == ZipContext.Classic.TreeStatus.Omitted && omitted.Lines.Count == 0);
        WriteIni("[preview]\nrootMax=20\nchildrenPerFolder=3\nmaxDepth=1\n");

        var deep = MakePreviewZip("deep-path", 1, _ => string.Join('/', Enumerable.Range(0, 33).Select(i => "d" + i)) + "/file.txt");
        omitted = ReadTree(deep);
        Check("deep paths omitted before excessive node construction", omitted.Status == ZipContext.Classic.TreeStatus.Omitted);
        var longPath = MakePreviewZip("long-path", 1, _ => new string('a', 1025) + ".txt");
        omitted = ReadTree(longPath);
        Check("oversized names omitted", omitted.Status == ZipContext.Classic.TreeStatus.Omitted);
        var manyNodes = MakePreviewZip("many-nodes", 7000, i => $"d{i}/s{i}/file.txt");
        omitted = ReadTree(manyNodes);
        Check("node count is bounded independently of entry count", omitted.Status is ZipContext.Classic.TreeStatus.Omitted or ZipContext.Classic.TreeStatus.Timeout);

        var forged = Path.Combine(work, "forged-directory.zip");
        var footer = new byte[22];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(footer, 0x06054b50);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(12), 9 * 1024 * 1024);
        File.WriteAllBytes(forged, footer);
        omitted = ReadTree(forged);
        Check("ZIP directory size cap enforced before allocation", omitted.Status == ZipContext.Classic.TreeStatus.Omitted);
        var normalAgain = ReadTree(zip);
        Check("normal preview remains usable after limit cases", normalAgain.Status == ZipContext.Classic.TreeStatus.Ok &&
            normalAgain.Lines.Any(l => l.Name == "empty-folder"));

        ClassicMenuChecks.Run(Check, Path.Combine(work, "undisplayed-menus"));
        await EngineStateChecks.Run(Check, Path.Combine(work, "engine-state"));
        await DeploymentChecks.Run(Check, Path.Combine(work, "deployment-files"));
        await CliChecks.Run(Check, Path.Combine(work, "cli-exit-codes"));
        UpdateCheckChecks.Run(Check, Path.Combine(work, "update-checks"));
        SettingsChecks.Run(Check, Path.Combine(work, "shared-settings"));
        DiagnosticsChecks.Run(Check, Path.Combine(work, "bounded-diagnostics"));

        // 非交互模式强制控制台引擎，真实多包 CLI 检查也可无界面运行。
        {
            File.WriteAllText(Path.Combine(work, "one.txt"), "one");
            File.WriteAllText(Path.Combine(work, "two.txt"), "two");
            var first = Path.Combine(work, "one.zip");
            var second = Path.Combine(work, "two.zip");
            await real.CompressAsync("zip", first, new[] { "one.txt" }, workingDirectory: work);
            await real.CompressAsync("zip", second, new[] { "two.txt" }, workingDirectory: work);
            var destination = Path.Combine(work, "destination with spaces");
            var app = Path.Combine(AppContext.BaseDirectory, "7zipContext.exe");
            var start = new ProcessStartInfo(app) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--extract-to", first, second, "--destination", destination, "--non-interactive" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(15_000)) { process.Kill(entireProcessTree: true); Check("multi-archive CLI finishes", false); }
            else Check("multi-archive CLI finishes", process.ExitCode == 0);
            Check("multi-archive destination contains both files", File.Exists(Path.Combine(destination, "one.txt")) &&
                File.Exists(Path.Combine(destination, "two.txt")));
        }
    }
    else { failed++; Console.WriteLine("FAIL real engine unavailable"); }
}
finally
{
    if (original is null) File.Delete(ini); else File.WriteAllBytes(ini, original);
    Directory.Delete(work, recursive: true);
}
Console.WriteLine($"Result: {passed} passed / {failed} failed");
return failed == 0 ? 0 : 1;
