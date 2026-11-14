using System.Diagnostics;
using ZipContext.App;
using ZipContext.Core;

internal static class CliChecks
{
    // 自身模拟引擎，只处理专属临时文件；不写用户配置、不启动 GUI 引擎。
    internal static int? Child(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("a" or "x" or "t" or "l")) return null;
        var inputs = args.Skip(1).Where(a => !a.StartsWith('-')).ToArray();
        var marker = inputs.LastOrDefault(a => Path.GetFileName(a).StartsWith("cli-", StringComparison.Ordinal));
        if (marker is null) return null;
        var name = Path.GetFileName(marker);
        if (name.Contains("password") && !args.Contains("-psecret"))
        {
            Console.Error.WriteLine("password required");
            return 2;
        }
        // 输出里出现 "password" 但其实是别的错误：不能据此索要密码。
        if (name.Contains("wordlist"))
        {
            Console.WriteLine("----------\nPath = password.txt\nSize = 1\n");
            Console.Error.WriteLine("fixture outcome 2");
            return 2;
        }
        var code = name.Contains("fail") ? 2 : name.Contains("warning") ? 1 :
            name.Contains("cancel") ? 255 : name.Contains("invalid") ? 7 : 0;
        if (args[0] == "l" && name.Contains("write")) code = 0;
        if (code != 0) { Console.Error.WriteLine(code == 255 ? "password prompt cancelled" : "fixture outcome " + code); return code; }
        if (args[0] == "l") Console.WriteLine("----------\nPath = item.txt\nSize = 1\n");
        return 0;
    }

    internal static async Task Run(Action<string, bool> check, string root)
    {
        Directory.CreateDirectory(root);
        string Input(string name)
        {
            var path = Path.Combine(root, "cli-" + name);
            File.WriteAllText(path, "fixture");
            return path;
        }
        var good = Input("good.zip");
        var bad = Input("fail.zip");
        var warning = Input("warning.zip");
        var cancel = Input("cancel.zip");
        var invalid = Input("invalid.zip");
        var encrypted = Input("password.zip");
        var missing = Path.Combine(root, "missing.zip");
        var unsupported = Input("unsupported.txt");
        var later = Input("group.002");
        int launches = 0, prompts = 0, created = 0;
        var messages = new List<string>();
        CommandRunner Runner(Func<string, string?>? password = null, Func<string, string?>? folder = null) => new(
            serviceFactory: () => { created++; return new SevenZipService(Environment.ProcessPath) { UseGui = false }; },
            notify: (message, _) => messages.Add(message),
            password: password ?? (_ => { prompts++; return null; }),
            folder: folder ?? (_ => { prompts++; return null; }),
            launch: _ => { launches++; throw new IOException("fixture launch denied"); });
        int Run(params string[] command) => Runner().Run(command.Append("--non-interactive").ToArray());

        check("CLI unknown command is invalid", Run("--unknown") == 2);
        check("CLI missing archive is invalid", Run("--test", missing) == 2);
        check("CLI missing test argument is invalid", Run("--test") == 2);
        check("CLI extra test argument is invalid", Run("--test", good, bad) == 2);
        check("CLI missing compress argument is invalid", Run("--add-zip") == 2);
        check("CLI missing extract argument is invalid", Run("--smart-extract") == 2);
        check("CLI missing named-extract argument is invalid", Run("--extract-to-name") == 2);
        check("CLI invalid paths do not prepare engine", created == 0);
        check("CLI missing compression input rejects whole operation", Run("--add-zip", good, missing) == 2 && created == 0);
        check("CLI destination value required", Run("--extract-to", good, "--destination") == 2);
        check("CLI duplicate destination rejected", Run("--extract-to", good, "--destination", root, "--destination", root) == 2);
        check("CLI noninteractive destination required", Run("--extract-to", good) == 2);
        check("CLI unsupported option rejected", Run("--add-zip", "--bogus", good) == 2);
        check("CLI unsupported archive rejected", Run("--smart-extract", unsupported) == 2);
        check("CLI later-volume-only selection rejected", Run("--smart-extract", later) == 2);
        check("CLI interactive-only commands rejected without GUI", Run("--settings") == 2 && Run("--defaults") == 2 && Run("--open-with-7zip", good) == 2);
        var registrationStatus = Path.Combine(EngineLocator.ConfigRoot, "last-registration.json");
        var originalStatus = File.Exists(registrationStatus) ? File.ReadAllText(registrationStatus) : null;
        check("CLI extra registration argument rejected", Run("--install", "--bogus") == 2 && Run("--uninstall", "--bogus") == 2);
        check("CLI invalid registration leaves status untouched", originalStatus == (File.Exists(registrationStatus) ? File.ReadAllText(registrationStatus) : null));
        check("CLI unknown engine option rejected", Run("--ensure-engine", "--bogus") == 2);
        check("CLI custom ensure source rejected", Run("--ensure-engine", "--engine-source", "https://example.invalid/engine.exe") == 2);
        check("CLI legacy custom source rejected", Run("--ensure-engine", "-EngineSource", "https://example.invalid/engine.exe") == 2);
        check("CLI custom update source rejected", Run("--update-engine", "--engine-source", "https://example.invalid/engine.exe") == 2);
        var rejected = false;
        try { EngineProvisioner.Ensure(null, "https://example.invalid/engine.exe"); } catch (ArgumentException) { rejected = true; }
        check("engine API custom ensure rejected before provisioning", rejected);
        rejected = false;
        try { EngineProvisioner.Update(""); } catch (ArgumentException) { rejected = true; }
        check("engine API even empty custom source rejected", rejected);
        check("CLI missing engine option value rejected", Run("--ensure-engine", "--engine-path") == 2);
        check("CLI duplicate engine option rejected", Run("--ensure-engine", "--engine-path", root, "-EnginePath", root) == 2);
        check("CLI incompatible engine options rejected", Run("--engine-info", "--force") == 2 && Run("--check-engine-update", "--engine-source", "https://example.invalid") == 2);
        check("CLI contradictory interactive option rejected", Run("--engine-info", "--show") == 2);
        check("CLI provisioning readiness success is success", CommandExit.FromProvisioning(new(true, "ready", "1", "local")) == 0);
        check("CLI provisioning warning is partial", CommandExit.FromProvisioning(new(true, "warning", "1", "local", true)) == 4);
        check("CLI offline update query is failure despite usable engine", CommandExit.FromProvisioning(new(true, "offline", "1", "offline")) == 1);
        check("CLI failed provisioning is failure", CommandExit.FromProvisioning(new(false, "failed", null, "busy")) == 1);
        using (var status = System.Text.Json.JsonDocument.Parse(new EngineProvisioner.EnsureResult(true, "warning", "1", "local", true).ToJson()))
            check("CLI provisioning status carries structured warning", status.RootElement.GetProperty("ok").GetBoolean() && status.RootElement.GetProperty("warnings").GetBoolean());
        check("CLI invalid commands cause no dialogs or launches", prompts == 0 && launches == 0);

        check("CLI integrity failure returns failure", Run("--test", bad) == 1);
        check("CLI integrity success returns success", Run("--test", good) == 0);
        check("CLI engine warning returns partial", Run("--test", warning) == 4);
        check("CLI engine cancellation returns cancelled", Run("--test", cancel) == 3);
        check("CLI engine syntax error returns invalid", Run("--test", invalid) == 2);
        check("CLI password required without interaction fails promptly", Run("--test", encrypted) == 1 && prompts == 0);
        check("CLI cancelled password prompt returns cancelled", Runner().Run(new[] { "--test", encrypted }) == 3 && prompts == 1);
        // 输出里出现 "password" 字样但不是密码问题（例如包内条目名含该词）：不得弹密码框。
        var wordlist = Input("wordlist.zip");
        var promptsBefore = prompts;
        check("CLI incidental password word does not prompt", Runner().Run(new[] { "--test", wordlist }) == 1 && prompts == promptsBefore);
        check("CLI password retry can succeed", Runner(_ => "secret").Run(new[] { "--test", encrypted }) == 0);
        check("CLI cancelled folder prompt returns cancelled", Runner().Run(new[] { "--extract-to", good }) == 3);
        check("CLI named extraction failure propagates", Run("--extract-to-name", bad) == 1);
        check("CLI smart listing cancellation propagates", Run("--smart-extract", cancel) == 3);
        check("CLI smart listing failure propagates", Run("--smart-extract", bad) == 1);
        var writeCancel = Input("write-cancel.zip");
        check("CLI smart write cancellation propagates and retains source", Run("--smart-extract-delete", writeCancel) == 3 && File.Exists(writeCancel));
        check("CLI smart write warning is partial", Run("--smart-extract", Input("write-warning.zip")) == 4);
        check("CLI smart extraction success returns success", Run("--smart-extract", good) == 0);
        var splitZip = Input("good-split.zip");
        var splitFirst = Path.ChangeExtension(splitZip, ".z01");
        File.WriteAllText(splitFirst, "fixture segment");
        check("CLI retained split sources return partial without recycling", Run("--smart-extract-delete", splitZip) == 4 &&
            File.Exists(splitZip) && File.Exists(splitFirst));
        check("CLI extraction mixed success and failure is partial", Run("--extract-to", good, bad, "--destination", root) == 4);
        check("CLI extraction mixed success and invalid path is partial", Run("--extract-to-name", good, missing) == 4);
        check("CLI all extraction failures return failure", Run("--extract-to-name", bad, Input("fail-two.zip")) == 1);
        check("CLI compression failure propagates", Run("--add-zip", Input("fail.txt")) == 1);
        check("CLI compression warning propagates", Run("--add-7z", Input("warning.txt")) == 4);
        check("CLI compression cancellation propagates", Run("--add-zip", Input("cancel.txt")) == 3);
        check("CLI quick compression noninteractive never opens Explorer", Run("--quick-zip", Input("good.txt")) == 0 && launches == 0);
        check("CLI result-directory failure after compression is partial", Runner().Run(new[] { "--quick-zip", Input("good-two.txt") }) == 4 && launches == 1);
        check("CLI file-manager launch failure returns failure", Runner().Run(new[] { "--open-with-7zip", good }) == 1 && launches == 2);
        var throwing = new CommandRunner(() => throw new OperationCanceledException(), (message, _) => messages.Add(message));
        check("CLI cancellation exception is cancelled", throwing.Run(new[] { "--test", good, "--non-interactive" }) == 3);
        throwing = new CommandRunner(() => throw new IOException("unavailable engine"), (message, _) => messages.Add(message));
        check("CLI engine startup exception is failure", throwing.Run(new[] { "--add-zip", good, "--non-interactive" }) == 1);
        check("CLI all-cancelled batch is cancelled", CommandExit.Aggregate(new[] { 3, 3 }) == 3);
        check("CLI failure takes precedence without completed work", CommandExit.Aggregate(new[] { 1, 2, 3 }) == 1);

        // 实际 WinExe 边界：验证 OS 可见退出码，不只是测试内部返回值。
        var app = Path.Combine(AppContext.BaseDirectory, "7zipContext.exe");
        async Task<int> Exit(params string[] command)
        {
            var start = new ProcessStartInfo(app) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in command.Append("--non-interactive")) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            return process.ExitCode;
        }
        check("CLI executable exposes invalid-argument exit code", await Exit("--test", missing) == 2);
        var corrupt = Input("corrupt-real.zip");
        check("CLI executable reports damaged archive failure", await Exit("--test", corrupt) == 1);
        check("CLI executable reports damaged extraction failure", await Exit("--extract-to-name", corrupt) == 1);
        var text = Input("real-source.txt");
        var archive = Path.ChangeExtension(text, ".zip");
        check("CLI executable compresses with real console engine", await Exit("--add-zip", text) == 0 && File.Exists(archive));
        check("CLI executable tests real archive successfully", await Exit("--test", archive) == 0);
        check("CLI executable exposes mixed-batch partial code", await Exit("--extract-to-name", archive, corrupt) == 4);
        var firstVolume = Input("good.7z.001");
        var laterVolume = Input("good.7z.002");
        check("mixed split and normal archive both selected", ArchiveSelection.TargetsForExtraction(new[] { firstVolume, good, laterVolume }).SequenceEqual(new[] { firstVolume, good }));
        check("mixed split and normal selection order retained", ArchiveSelection.TargetsForExtraction(new[] { good, firstVolume }).SequenceEqual(new[] { good, firstVolume }));
        check("archive path aliases deduplicated", ArchiveSelection.TargetsForExtraction(new[] { good, Path.Combine(root, ".", Path.GetFileName(good)), good.ToUpperInvariant() }).Count == 1);
        check("two split groups retain normal archive", ArchiveSelection.TargetsForExtraction(new[] { firstVolume, Input("good-two.7z.001"), good }).Count == 3);
        check("empty selection hides extraction", !ArchiveSelection.CanExtract(Array.Empty<string>()));
        check("unsupported selection hides extraction", !ArchiveSelection.CanExtract(new[] { unsupported, laterVolume }));
        check("menu visibility matches supported mixed targets", ArchiveSelection.CanExtract(new[] { good, unsupported, laterVolume }) && ArchiveSelection.TargetsForExtraction(new[] { good, unsupported, laterVolume }).SequenceEqual(new[] { good }));
        check("CLI mixed split success and ordinary failure returns partial", Run("--extract-to-name", firstVolume, bad, laterVolume) == 4);
        var split = TraditionalZipFixture.Create(Path.Combine(root, "real-mixed"), "split");
        var selection = new[] { split.First, split.Middle, archive, split.Last, split.First };
        check("traditional split dedup retains ordinary archive", ArchiveSelection.TargetsForExtraction(selection).SequenceEqual(new[] { split.Last, archive }) && ArchiveSelection.CanExtract(selection));
        var destination = Path.Combine(root, "real mixed output");
        check("CLI actual split and ordinary batch succeeds", await Exit("--extract-to", split.First, split.Middle, archive, split.Last, "--destination", destination) == 0);
        check("CLI actual mixed batch extracts all expected content", File.ReadAllText(Path.Combine(destination, Path.GetFileName(text))) == "fixture" &&
            TraditionalZipFixture.Contents.All(entry => File.ReadAllText(Path.Combine(destination, entry.Key)) == entry.Value));
    }
}
