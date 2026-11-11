using System.IO;
using System.Windows;
using ZipContext.Core;

namespace ZipContext.App;

/// <summary>
/// 分发器：右键菜单（经典 + Win11 COM 服务器）通过命令行调用本程序；
/// 无参启动（开始菜单图标 / 双击 exe）与 --settings 打开设置面板。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 开始菜单图标来自 AppX 的 Application 节点，激活时不带参数 → 打开设置窗口。
        if (args.Length == 0) return OpenSettings();

        if (args[0].Equals("--settings", StringComparison.OrdinalIgnoreCase)) return OpenSettings();

        var verb = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        // 引擎命令由 install.ps1 以隐藏窗口调用，绝不能弹 MessageBox（会挂住安装脚本）。
        // 统一用「状态文件 + 退出码」回传；手动执行时加 --show 才弹窗。
        if (verb is "--ensure-engine" or "--check-engine-update" or "--update-engine" or "--engine-info")
            return RunEngineCommand(verb, rest);

        try
        {
            switch (verb)
            {
                case "--smart-extract":
                    RunSmartExtract(rest, deleteAfter: false);
                    break;
                case "--smart-extract-delete":
                    RunSmartExtract(rest, deleteAfter: true);
                    break;
                case "--extract-to-name":
                    RunExtractToName(rest);
                    break;
                case "--extract-to":
                    RunExtractToOther(rest);
                    break;
                case "--add-zip":
                    RunCompress("zip", rest);
                    break;
                case "--add-7z":
                    RunCompress("7z", rest);
                    break;
                case "--quick-zip":
                    RunCompress("zip", rest, openFolderAfter: true);
                    break;
                case "--test":
                    if (rest.Length > 0) RunTest(rest[0]);
                    break;
                case "--open":
                    // v1 旧快捷方式兼容：打开设置面板，而不是弹一句「没有主窗口」
                    return OpenSettings();
                case "--install":
                    ShellIntegration.ShellIntegration.Install();
                    break;
                case "--defaults":
                    // Windows 不允许静默设默认程序，只能把用户送到「默认应用」页
                    ShellIntegration.ShellIntegration.OpenDefaultAppsSettings();
                    break;
                case "--uninstall":
                    ShellIntegration.ShellIntegration.Uninstall();
                    break;
                default:
                    // 裸路径（旧快捷方式 / 拖文件到 exe）→ 打开设置面板
                    if (File.Exists(args[0])) return OpenSettings();
                    ShowError($"未知命令：{verb}"); return 2;
            }
            return 0;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return 1;
        }
    }

    private static void ShowError(string message) =>
        MessageBox.Show(message, "7zipContext", MessageBoxButton.OK, MessageBoxImage.Error);

    private static void ShowInfo(string message) =>
        MessageBox.Show(message, "7zipContext", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>
    /// 打开设置面板。WinExe 没有 Application 实例，必须显式建一个，
    /// 否则 Window.ShowDialog 无法运行 Dispatcher 消息循环（窗口一闪而过或直接抛异常）。
    /// 进程里只会有一个 Application；引擎命令不走这里，所以不会冲突。
    /// </summary>
    private static int OpenSettings()
    {
        try
        {
            var app = Application.Current ?? new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            var window = new SettingsWindow();
            app.MainWindow = window;
            window.ShowDialog();
            app.Shutdown();
            return 0;
        }
        catch (Exception ex)
        {
            ShowError($"无法打开设置窗口：{ex.Message}");
            return 1;
        }
    }

    // ---------- 7-Zip 引擎：首次安装 / 更新检查 / 升级 ----------

    private static int RunEngineCommand(string verb, string[] rest)
    {
        string? Value(string name)
        {
            for (var i = 0; i < rest.Length - 1; i++)
                if (rest[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return rest[i + 1];
            return null;
        }
        var force = rest.Any(a => a is "--force" or "-Force");
        var show = rest.Any(a => a is "--show" or "-Show");
        var enginePath = Value("--engine-path") ?? Value("-EnginePath");
        var engineSource = Value("--engine-source") ?? Value("-EngineSource");

        EngineProvisioner.EnsureResult result;
        try
        {
            result = verb switch
            {
                "--ensure-engine" => EngineProvisioner.Ensure(enginePath, engineSource, force),
                "--check-engine-update" => EngineProvisioner.CheckUpdate(force),
                "--update-engine" => EngineProvisioner.Update(engineSource),
                _ => new EngineProvisioner.EnsureResult(true, EngineProvisioner.Describe(), null, "info"),
            };
        }
        catch (Exception ex)
        {
            result = new EngineProvisioner.EnsureResult(false, ex.Message, null, "failed");
        }

        // 只有「准备 / 升级」写 last-ensure.json（install.ps1 读它判断引擎是否就绪）。
        // 检查 / 查询命令写到独立文件，避免把安装状态覆盖成一条更新提示。
        if (verb is "--ensure-engine" or "--update-engine")
            EngineProvisioner.WriteStatus(result, EngineProvisioner.StatusPath);
        else
            EngineProvisioner.WriteStatus(result,
                Path.Combine(ZipContext.Core.EngineLocator.ConfigRoot, "last-query.json"));

        if (show)
        {
            Action<string> notify = result.Ok ? ShowInfo : ShowError;
            notify(result.Message);
        }
        else Console.WriteLine(result.Message);   // 直接命令行运行时也能看到
        return result.Ok ? 0 : 1;
    }

    // ---------- 动词实现 ----------

    private static void RunSmartExtract(string[] archives, bool deleteAfter)
    {
        var sevenZip = new SevenZipService();
        var smart = new SmartExtractService(sevenZip);
        int ok = 0, fail = 0;

        foreach (var archive in archives.Where(File.Exists))
        {
            string? password = null;
            while (true)
            {
                try
                {
                    var result = smart.ExtractAsync(archive, deleteAfter, password).GetAwaiter().GetResult();
                    if (result.Success) { ok++; break; }
                    fail++;
                    ShowError($"{Path.GetFileName(archive)}：{result.Error}");
                    break;
                }
                catch (PasswordRequiredException)
                {
                    password = PasswordDialog.Prompt(Path.GetFileName(archive));
                    if (password is null) { fail++; break; } // 用户取消
                }
            }
        }

        if (ok + fail > 1)
            ShowInfo($"完成：成功 {ok} 个，失败 {fail} 个。");
    }

    private static void RunExtractToName(string[] archives)
    {
        var sevenZip = new SevenZipService();
        foreach (var archive in archives.Where(File.Exists))
        {
            var dest = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(archive))!,
                ArchiveFormats.StripExtension(Path.GetFileName(archive)));
            ExtractCore(sevenZip, archive, dest);
        }
    }

    private static void RunExtractToOther(string[] args)
    {
        if (args.Length == 0) return;
        var archive = args[0];
        if (!File.Exists(archive)) return;

        string dest;
        if (args.Length > 1)
        {
            dest = args[1];
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择解压目标文件夹",
                InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(archive)),
            };
            if (dlg.ShowDialog() != true) return;
            dest = dlg.FolderName;
        }
        ExtractCore(new SevenZipService(), archive, dest);
    }

    private static void ExtractCore(SevenZipService svc, string archive, string dest)
    {
        string? password = null;
        while (true)
        {
            try
            {
                var res = svc.ExtractAsync(archive, dest, password).GetAwaiter().GetResult();
                if (!res.Success)
                    ShowError($"{Path.GetFileName(archive)} 解压失败：{res.StdErr}");
                return;
            }
            catch (PasswordRequiredException)
            {
                password = PasswordDialog.Prompt(Path.GetFileName(archive));
                if (password is null) return;
            }
        }
    }

    private static void RunCompress(string format, string[] inputs, bool openFolderAfter = false)
    {
        if (inputs.Length == 0) return;
        inputs = inputs.Where(File.Exists).Concat(inputs.Where(Directory.Exists)).Distinct().ToArray();
        if (inputs.Length == 0) return;

        // 输出名规则（Q11）：单选 → 同名；多选 → 父文件夹名（与菜单文字共用 NamingHelper）
        var baseName = NamingHelper.TargetBaseName(inputs);
        var parentDir = Path.GetDirectoryName(Path.GetFullPath(inputs[0]))!;
        var outPath = Path.Combine(parentDir, $"{baseName}.{format}");
        // 重名时加 (1)(2)
        outPath = MakeUnique(outPath);

        var svc = new SevenZipService();
        var res = svc.CompressAsync(format, outPath, inputs).GetAwaiter().GetResult();
        if (!res.Success)
        {
            ShowError($"压缩失败：{res.StdErr}");
            return;
        }
        if (openFolderAfter)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { "/select,", outPath },
                UseShellExecute = true,
            });
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

    private static void RunTest(string archive)
    {
        string? password = null;
        while (true)
        {
            try
            {
                var res = new SevenZipService().TestAsync(archive, password).GetAwaiter().GetResult();
                if (res.Success) ShowInfo("完整性测试通过 ✓");
                else ShowError($"完整性测试失败：{res.StdErr}");
                return;
            }
            catch (PasswordRequiredException)
            {
                password = PasswordDialog.Prompt(Path.GetFileName(archive));
                if (password is null) return;
            }
        }
    }
}
