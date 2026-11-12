using System.Diagnostics;
using System.IO;
using System.Windows;
using ZipContext.Core;

namespace ZipContext.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args) => new CommandRunner().Run(args);
}

/// <summary>退出码是调用协议；提示信息不能代替失败返回值。</summary>
internal static class CommandExit
{
    internal const int Success = 0, Failed = 1, InvalidArguments = 2, Cancelled = 3, Partial = 4;

    internal static int FromEngine(int code) => code switch
    {
        0 => Success, 1 => Partial, 7 => InvalidArguments, 255 => Cancelled, _ => Failed,
    };

    // Ok 表示引擎仍可用；离线检查未完成不能冒充本次查询成功。
    internal static int FromProvisioning(EngineProvisioner.EnsureResult result) =>
        !result.Ok || result.Origin == "offline" ? Failed : result.HasWarnings ? Partial : Success;

    internal static int Aggregate(IEnumerable<int> results)
    {
        var codes = results.ToArray();
        if (codes.Length == 0) return InvalidArguments;
        if (codes.Contains(Partial) || (codes.Contains(Success) && codes.Any(c => c != Success))) return Partial;
        if (codes.Contains(Failed)) return Failed;
        if (codes.Contains(InvalidArguments)) return InvalidArguments;
        if (codes.Contains(Cancelled)) return Cancelled;
        return Success;
    }
}

internal sealed class CommandRunner
{
    private readonly Func<SevenZipService> _serviceFactory;
    private readonly Action<string, bool>? _notify;
    private readonly Func<string, string?> _password;
    private readonly Func<string, string?> _folder;
    private readonly Action<ProcessStartInfo> _launch;
    private bool _nonInteractive;

    // 可替换边界仅用于隔离测试，不修改本机引擎指针或触发桌面操作。
    internal CommandRunner(Func<SevenZipService>? serviceFactory = null,
        Action<string, bool>? notify = null, Func<string, string?>? password = null,
        Func<string, string?>? folder = null, Action<ProcessStartInfo>? launch = null)
    {
        _serviceFactory = serviceFactory ?? (() => new SevenZipService());
        _notify = notify;
        _password = password ?? PasswordDialog.Prompt;
        _folder = folder ?? (initial =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择解压目标文件夹", InitialDirectory = initial };
            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        });
        _launch = launch ?? (start =>
        {
            using var process = Process.Start(start) ?? throw new IOException("未能启动目标程序。");
        });
    }

    internal int Run(string[] args)
    {
        _nonInteractive = args.Any(a => a.Equals("--non-interactive", StringComparison.OrdinalIgnoreCase));
        args = args.Where(a => !a.Equals("--non-interactive", StringComparison.OrdinalIgnoreCase)).ToArray();
        var verb = args.FirstOrDefault()?.ToLowerInvariant() ?? "--settings";
        var rest = args.Skip(1).ToArray();
        var registrationAttempted = false;
        try
        {
            if (verb is "--ensure-engine" or "--check-engine-update" or "--update-engine" or "--engine-info")
                return RunEngineCommand(verb, rest);
            switch (verb)
            {
                case "--settings": case "--open":
                    NoArguments(rest);
                    RequireInteractive();
                    return OpenSettings();
                case "--smart-extract": case "--smart-extract-delete":
                    PathsOnly(rest);
                    return RunSmartExtract(rest, verb == "--smart-extract-delete");
                case "--extract-to-name":
                    PathsOnly(rest);
                    return RunExtractToName(rest);
                case "--extract-to": return RunExtractToOther(rest);
                case "--add-zip": case "--quick-zip": case "--add-7z":
                    PathsOnly(rest);
                    return RunCompress(verb == "--add-7z" ? "7z" : "zip", rest, verb == "--quick-zip");
                case "--open-with-7zip":
                    PathsOnly(rest);
                    RequireInteractive();
                    return RunOpenWithSevenZip(rest);
                case "--test":
                    PathsOnly(rest);
                    if (rest.Length != 1) throw new ArgumentException("--test 必须且只能指定一个压缩包。");
                    if (!File.Exists(rest[0])) throw new ArgumentException($"文件不存在：{rest[0]}");
                    return WithPassword(rest[0], password =>
                    {
                        var result = Service().TestAsync(rest[0], password).GetAwaiter().GetResult();
                        if (result.Success) ShowInfo("完整性测试通过 ✓");
                        ReportRun(result, "完整性测试");
                        return CommandExit.FromEngine(result.ExitCode);
                    });
                case "--install":
                    NoArguments(rest);
                    registrationAttempted = true;
                    ShellIntegration.ShellIntegration.Install();
                    return CommandExit.Success;
                case "--uninstall":
                    NoArguments(rest);
                    registrationAttempted = true;
                    ShellIntegration.ShellIntegration.Uninstall();
                    return CommandExit.Success;
                case "--defaults":
                    NoArguments(rest);
                    RequireInteractive();
                    ShellIntegration.ShellIntegration.OpenDefaultAppsSettings();
                    return CommandExit.Success;
                default:
                    if (args.Length == 1 && File.Exists(args[0]))
                    {
                        RequireInteractive();
                        return OpenSettings();
                    }
                    throw new ArgumentException($"未知命令或无效路径：{verb}");
            }
        }
        catch (Exception ex)
        {
            // 隐藏注册命令始终不弹窗；错误状态文件失败也不能逃逸 Main。
            if (verb is "--install" or "--uninstall")
            {
                if (registrationAttempted) try
                {
                    EngineProvisioner.WriteStatus(new EngineProvisioner.EnsureResult(false, ex.Message, null,
                        ex is EngineBusyException or ZipContext.Deployment.DeploymentBusyException ? "busy" : "failed"),
                        Path.Combine(EngineLocator.ConfigRoot, "last-registration.json"));
                }
                catch { }
                Console.Error.WriteLine(ex.Message);
            }
            else if (verb is "--ensure-engine" or "--check-engine-update" or "--update-engine" or "--engine-info")
                Console.Error.WriteLine(ex.Message);
            else ShowError(ex.Message);
            return ExceptionExit(ex);
        }
    }

    private static int ExceptionExit(Exception ex) => ex switch
    {
        OperationCanceledException => CommandExit.Cancelled,
        ArgumentException => CommandExit.InvalidArguments,
        _ => CommandExit.Failed,
    };

    private void Notify(string message, bool error)
    {
        if (_notify is not null) _notify(message, error);
        else if (_nonInteractive)
        {
            if (error) Console.Error.WriteLine(message); else Console.WriteLine(message);
        }
        else MessageBox.Show(message, "7zipContext", MessageBoxButton.OK,
            error ? MessageBoxImage.Error : MessageBoxImage.Information);
    }
    private void ShowError(string message) => Notify(message, true);
    private void ShowInfo(string message) => Notify(message, false);
    private void RequireInteractive()
    {
        if (_nonInteractive) throw new ArgumentException("此操作需要图形界面，不能使用 --non-interactive。");
    }
    private static void NoArguments(string[] args)
    {
        if (args.Length != 0) throw new ArgumentException("此命令不接受附加参数。");
    }
    private static void PathsOnly(string[] args)
    {
        if (args.Length == 0 || args.Any(a => string.IsNullOrWhiteSpace(a) || a.StartsWith("--", StringComparison.Ordinal)))
            throw new ArgumentException("请提供有效输入路径；不支持的选项不能作为输入。以 -- 开头的文件名请使用完整路径。");
    }
    private SevenZipService Service()
    {
        var service = _serviceFactory();
        if (_nonInteractive) service.UseGui = false;
        return service;
    }
    private int OpenSettings()
    {
        Environment.SetEnvironmentVariable(ZipContext.Deployment.DeploymentLease.EnvironmentName, null);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new SettingsWindow();
        app.MainWindow = window;
        window.ShowDialog();
        app.Shutdown();
        return CommandExit.Success;
    }

    private int RunEngineCommand(string verb, string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var key = args[i].ToLowerInvariant() switch
            {
                "-force" => "--force", "-show" => "--show",
                "-enginepath" => "--engine-path", "-enginesource" => "--engine-source",
                _ => args[i].ToLowerInvariant(),
            };
            if (key == "--engine-source") throw new ArgumentException("自定义下载源已停用；请使用官方源或 --engine-path 本机离线引擎。");
            if (key is "--force" or "--show")
            {
                if (!flags.Add(key)) throw new ArgumentException($"重复选项：{key}");
                continue;
            }
            if (key is not ("--engine-path" or "--engine-source" or "--status-path"))
                throw new ArgumentException($"未知引擎选项：{args[i]}");
            if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith('-'))
                throw new ArgumentException($"选项缺少值：{key}");
            if (!values.TryAdd(key, args[i])) throw new ArgumentException($"重复选项：{key}");
        }
        if (_nonInteractive && flags.Contains("--show")) throw new ArgumentException("--show 与 --non-interactive 不能同时使用。");
        if (values.ContainsKey("--engine-path") && verb != "--ensure-engine")
            throw new ArgumentException("--engine-path 仅适用于 --ensure-engine。");
        if (values.ContainsKey("--engine-source") && verb is not ("--ensure-engine" or "--update-engine"))
            throw new ArgumentException("此命令不接受 --engine-source。");
        if (flags.Contains("--force") && verb is not ("--ensure-engine" or "--check-engine-update"))
            throw new ArgumentException("此命令不接受 --force。");
        string? Value(string key) => values.GetValueOrDefault(key);
        EngineProvisioner.EnsureResult result;
        int code;
        try
        {
            result = verb switch
            {
                "--ensure-engine" => EngineProvisioner.Ensure(Value("--engine-path"), Value("--engine-source"), flags.Contains("--force")),
                "--check-engine-update" => EngineProvisioner.CheckUpdate(flags.Contains("--force")),
                "--update-engine" => EngineProvisioner.Update(Value("--engine-source")),
                _ => new EngineProvisioner.EnsureResult(true, EngineProvisioner.Describe(), null, "info"),
            };
            code = CommandExit.FromProvisioning(result);
        }
        catch (Exception ex)
        {
            result = new EngineProvisioner.EnsureResult(false, ex.Message, null, "failed");
            code = ExceptionExit(ex);
        }
        var statusPath = Value("--status-path") ?? (verb is "--ensure-engine" or "--update-engine"
            ? EngineProvisioner.StatusPath : Path.Combine(EngineLocator.ConfigRoot, "last-query.json"));
        if (!EngineProvisioner.WriteStatus(result, statusPath)) code = CommandExit.Failed;
        if (flags.Contains("--show")) Notify(result.Message, !result.Ok);
        else Console.WriteLine(result.Message);
        return code;
    }

    private int WithPassword(string archive, Func<string?, int> operation)
    {
        string? password = null;
        while (true)
        {
            try { return operation(password); }
            catch (PasswordRequiredException ex)
            {
                if (_nonInteractive) { ShowError(ex.Message); return CommandExit.Failed; }
                password = _password(Path.GetFileName(archive));
                if (password is null) return CommandExit.Cancelled;
            }
            catch (Exception ex) { ShowError($"{Path.GetFileName(archive)}：{ex.Message}"); return ExceptionExit(ex); }
        }
    }

    private int ExtractBatch(string[] sources, Func<string, int> operation)
    {
        var results = new List<int>();
        var existing = new List<string>();
        foreach (var source in sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(source))
            {
                ShowError($"文件不存在：{source}");
                results.Add(CommandExit.InvalidArguments);
            }
            else if (ArchiveFormats.ExtractionEntry(source) is null && !ArchiveFormats.IsLaterSplitVolume(source))
            {
                ShowError($"不支持的压缩包或缺少分卷入口：{source}");
                results.Add(CommandExit.InvalidArguments);
            }
            else existing.Add(source);
        }
        var targets = ArchiveSelection.TargetsForExtraction(existing);
        foreach (var archive in targets)
        {
            if (!File.Exists(archive))
            {
                ShowError($"压缩包入口不存在：{archive}");
                results.Add(CommandExit.InvalidArguments);
            }
            else results.Add(operation(archive));
        }
        if (targets.Count == 0 && results.Count == 0)
        {
            ShowError("没有可执行的压缩包入口，请选择首卷及所需配套文件。");
            return CommandExit.InvalidArguments;
        }
        if (results.Count > 1)
            ShowInfo($"完成：成功 {results.Count(c => c == CommandExit.Success)} 个，其余 {results.Count(c => c != CommandExit.Success)} 个。");
        return CommandExit.Aggregate(results);
    }

    private int RunSmartExtract(string[] archives, bool deleteAfter) => ExtractBatch(archives, archive =>
        WithPassword(archive, password =>
        {
            var result = new SmartExtractService(Service()).ExtractAsync(archive, deleteAfter, password).GetAwaiter().GetResult();
            if (result.Error is not null) ShowError($"{Path.GetFileName(archive)}：{result.Error}");
            return result.Success
                ? (result.Error is null ? CommandExit.Success : CommandExit.Partial)
                : CommandExit.FromEngine(result.ExitCode == 0 ? 2 : result.ExitCode);
        }));

    private int RunExtractToName(string[] archives) => ExtractBatch(archives, archive =>
        ExtractCore(archive, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(archive))!,
            ArchiveFormats.StripExtension(Path.GetFileName(archive)))));

    private int RunExtractToOther(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--destination", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && (index != args.Length - 2 || string.IsNullOrWhiteSpace(args[^1]) || args[^1].StartsWith("--")))
            throw new ArgumentException("--destination 后必须提供目标目录，并放在参数末尾。");
        var sources = index < 0 ? args : args[..index];
        PathsOnly(sources);
        if (index < 0 && _nonInteractive) throw new ArgumentException("非交互解压必须指定 --destination <目录>。");
        string? dest = index >= 0 ? args[^1] : null;
        var prompted = false;
        return ExtractBatch(sources, archive =>
        {
            if (index < 0 && !prompted)
            {
                dest = _folder(Path.GetDirectoryName(Path.GetFullPath(archive))!);
                prompted = true;
            }
            return dest is null ? CommandExit.Cancelled : ExtractCore(archive, dest);
        });
    }

    private int ExtractCore(string archive, string dest) => WithPassword(archive, password =>
    {
        var result = Service().ExtractAsync(archive, dest, password).GetAwaiter().GetResult();
        ReportRun(result, $"{Path.GetFileName(archive)} 解压");
        return CommandExit.FromEngine(result.ExitCode);
    });

    private void ReportRun(SevenZipService.RunResult result, string operation)
    {
        if (result.Success) return;
        var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        ShowError($"{operation}未全部完成（引擎退出码 {result.ExitCode}）：{detail}");
    }

    private int RunCompress(string format, string[] inputs, bool openFolderAfter)
    {
        if (inputs.Any(p => !File.Exists(p) && !Directory.Exists(p)))
            throw new ArgumentException("压缩输入包含不存在的文件或目录，未开始压缩。");
        inputs = inputs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var parent = Path.GetDirectoryName(Path.GetFullPath(inputs[0])) ?? throw new ArgumentException("不能直接压缩驱动器根目录。");
        var output = MakeUnique(Path.Combine(parent, $"{NamingHelper.TargetBaseName(inputs)}.{format}"));
        var result = Service().CompressAsync(format, output, inputs).GetAwaiter().GetResult();
        ReportRun(result, "压缩");
        var code = CommandExit.FromEngine(result.ExitCode);
        if (code == CommandExit.Success && openFolderAfter && !_nonInteractive)
        {
            try { _launch(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", output }, UseShellExecute = true }); }
            catch (Exception ex) { ShowError($"压缩成功，但打开结果目录失败：{ex.Message}"); return CommandExit.Partial; }
        }
        return code;
    }

    private static string MakeUnique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private int RunOpenWithSevenZip(string[] paths)
    {
        if (paths.Any(p => !File.Exists(p) && !Directory.Exists(p))) throw new ArgumentException("打开路径不存在。");
        var fm = EngineLocator.Require("7zFM.exe");
        var start = new ProcessStartInfo(fm) { UseShellExecute = true };
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase)) start.ArgumentList.Add(path);
        _launch(start);
        return CommandExit.Success; // GUI 打开命令只确认启动，不代表用户之后的操作完成。
    }
}
