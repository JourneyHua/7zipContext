using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZipContext.Deployment;

namespace ZipContext.Setup;

/// <summary>
/// 面向普通用户的图形化安装/卸载入口。
/// 安装包只携带本项目文件；7-Zip 本体仍由现有 install.ps1 在本机探测或联网获取。
/// </summary>
public sealed class SetupWindow : Window
{
    private static readonly string BundleRoot = AppContext.BaseDirectory;
    // 外部安装包使用 payload\；已安装目录里保留一份卸载工具，此时根目录本身就是 payload。
    private static readonly string PayloadRoot = Directory.Exists(Path.Combine(BundleRoot, "payload"))
        ? Path.Combine(BundleRoot, "payload")
        : BundleRoot;
    private static readonly string InstallRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "7zipContext", "app");

    private readonly Button _install = new() { Content = "安装 / 修复", Width = 112, Height = 32, IsDefault = true };
    private readonly Button _uninstall = new() { Content = "卸载", Width = 88, Height = 32 };
    private readonly Button _openSettings = new() { Content = "打开设置", Width = 96, Height = 32 };
    private readonly CheckBox _removeFiles = new() { Content = "卸载时删除程序文件", IsChecked = true };
    private readonly CheckBox _setAsDefault = new()
    {
        Content = "设为默认打开方式（安装后打开系统设置，由你选择 .zip / .7z / .rar）",
        IsChecked = false,
        Margin = new Thickness(0, 0, 0, 8),
    };
    private readonly CheckBox _removeEngine = new() { Content = "同时删除本工具下载的 7-Zip 引擎（本机已有的不会删除）" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
        FontSize = 12,
        Background = Brushes.WhiteSmoke,
        Padding = new Thickness(8),
    };
    private readonly ProgressBar _progress = new() { Height = 4, IsIndeterminate = false };

    private bool _busy;
    private bool _explorerStopped;
    private string? _deploymentToken;
    private static readonly string DeploymentRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "7zipContext", "deployments");

    public SetupWindow()
    {
        Title = "7zipContext 安装与卸载";
        Width = 650;
        Height = 580;
        MinWidth = 560;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new DockPanel { Margin = new Thickness(20) };
        root.Children.Add(Header());
        root.Children.Add(Footer());
        root.Children.Add(Options());
        root.Children.Add(_log);
        Content = root;

        _install.Click += async (_, _) => await RunOperation(install: true);
        _uninstall.Click += async (_, _) => await RunOperation(install: false);
        _openSettings.Click += (_, _) => OpenSettings();
        _status.Text = "点击「安装 / 修复」开始。无需输入命令，也不需要管理员权限。";
        RefreshSettingsButton();
    }

    private UIElement Header()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(new TextBlock
        {
            Text = "7zipContext",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "7-Zip 右键菜单增强层 · 图形化安装与卸载",
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 3, 0, 10),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "程序会安装到当前用户目录。安装或卸载期间资源管理器会短暂重启；" +
                   "若本机没有 7-Zip，将从官方发布页下载并校验。",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });
        DockPanel.SetDock(panel, Dock.Top);
        return panel;
    }

    private UIElement Options()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 10),
        };
        buttons.Children.Add(_install);
        buttons.Children.Add(Space());
        buttons.Children.Add(_uninstall);
        buttons.Children.Add(Space());
        buttons.Children.Add(_openSettings);
        panel.Children.Add(buttons);
        panel.Children.Add(_setAsDefault);

        var options = new WrapPanel();
        options.Children.Add(_removeFiles);
        options.Children.Add(_removeEngine);
        panel.Children.Add(options);
        DockPanel.SetDock(panel, Dock.Top);
        return panel;
    }

    private UIElement Footer()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(_progress);
        panel.Children.Add(_status);
        _status.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(panel, Dock.Bottom);
        return panel;
    }

    private static FrameworkElement Space() => new Border { Width = 10 };

    private async Task RunOperation(bool install)
    {
        if (_busy) return;
        if (!install && MessageBox.Show(
                "确定卸载 7zipContext 的两套右键菜单和文件关联吗？",
                "确认卸载", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        _log.Clear();
        SetStatus(install ? "正在安装，请稍候……" : "正在卸载，请稍候……");
        var completed = false;
        DeploymentLease? lease = null;
        try
        {
            lease = await Task.Run(() => DeploymentLease.Acquire(Path.GetDirectoryName(InstallRoot)!,
                null));
            _deploymentToken = lease.Token;
            if (install) await Install();
            else await Uninstall();
            completed = true;
        }
        catch (Exception ex)
        {
            await Task.Run(RestartExplorerIfNeeded);
            AppendLog("[失败] " + ex.Message);
            SetStatus(ex.Message, error: true);
            MessageBox.Show(ex.Message, install ? "安装未完成" : "卸载未完成",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            await Task.Run(RestartExplorerIfNeeded);
            _deploymentToken = null;
            lease?.Dispose();
            RefreshSettingsButton();
            SetBusy(false);
        }
        if (completed && install && _setAsDefault.IsChecked == true)
        {
            AppendLog("请在系统「默认应用」中搜索 .zip、.7z、.rar，逐项选择 7-Zip 文件管理器。未选择的类型保留现有默认程序。");
            try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
            catch (Exception ex)
            {
                AppendLog("无法打开系统设置：" + ex.Message);
                SetStatus("安装已完成。请手动打开 Windows 设置 → 应用 → 默认应用，选择 7-Zip。");
            }
        }
    }

    private async Task Install()
    {
        await RecoverInterruptedInstallations();
        var stage = Path.Combine(DeploymentRoot, "install-" + Guid.NewGuid().ToString("N"));
        var candidate = Path.Combine(stage, "candidate");
        FileDeployment? transaction = null;
        var registrationAttempted = false;
        try
        {
            AppendLog("后台准备程序文件与备份……");
            transaction = await Task.Run(() =>
            {
                ValidatePayload();
                CopyPayload(candidate);
                return FileDeployment.Prepare(candidate, InstallRoot, Path.Combine(stage, "transaction"),
                    new[] { Path.Combine("dist", "7zipContext.ini") }, false,
                    ZipContext.Core.LegacyRuntimeFiles.Find(InstallRoot, candidate));
            });
            // 使用候选程序先准备引擎，网络等待不占用资源管理器停机时间。
            var ready = Path.Combine(stage, "engine-result.json");
            var engineExit = await RunExecutable(Path.Combine(candidate, "dist", "7zipContext.exe"),
                "--ensure-engine", "--status-path", ready);
            var engineResult = await Task.Run(() =>
            {
                if (!File.Exists(ready)) return (Ok: false, Message: $"引擎退出码 {engineExit}，未生成请求结果文件。");
                using var result = JsonDocument.Parse(File.ReadAllText(ready));
                // 引擎已就绪但关联刷新有警告时，继续后续正式注册和自检。
                var ok = engineExit is 0 or 4 && result.RootElement.TryGetProperty("ok", out var success) && success.GetBoolean();
                var message = result.RootElement.TryGetProperty("message", out var text) ? text.GetString() ?? "无详细结果" : "无详细结果";
                return (Ok: ok, Message: message);
            });
            AppendLog(engineResult.Message);
            if (!engineResult.Ok) throw new IOException("引擎准备未完成，程序文件尚未切换：" + engineResult.Message);
            await Task.Run(StopShellProcesses);
            await Task.Run(transaction.Apply);
            var script = Path.Combine(InstallRoot, "scripts", "install.ps1");
            registrationAttempted = true;
            var exitCode = await RunScript(script, "-SkipPublish", "-SkipEngineCheck",
                "-PreparedEngineStatus", "'" + ready.Replace("'", "''") + "'");
            if (exitCode != 0) throw new IOException($"安装自检未通过（退出码 {exitCode}）。");
            var warning = await Task.Run(transaction.Commit);
            if (warning is not null) AppendLog(warning);
            else await TryCleanupStage(stage);
        }
        catch (Exception ex)
        {
            if (transaction is null) { await TryCleanupStage(stage); throw; }
            if (transaction.Journal.State == "committed") throw;
            try
            {
                await Task.Run(transaction.Rollback);
                AppendLog("文件已恢复到安装前状态。");
                if (registrationAttempted)
                {
                    // 旧版 --install 可能清除 UserChoice；不能为恢复注册重放该行为。
                    AppendLog("系统注册可能存在部分变化，尚未验证恢复；请用新版安装工具修复。备份保留在：" + stage);
                }
                else
                {
                    AppendLog("系统注册尚未执行，无需恢复注册。");
                    await Task.Run(transaction.Cleanup);
                    await TryCleanupStage(stage);
                }
            }
            catch (Exception recovery)
            {
                throw new IOException($"{ex.Message}\n恢复未全部完成：{recovery.Message}\n备份保留在：{stage}",
                    new AggregateException(ex, recovery));
            }
            throw new IOException(ex.Message + "\n文件已恢复；安装未完成。" +
                (registrationAttempted ? "\n系统注册尚需验证；备份保留在：" + stage : ""), ex);
        }

        await Task.Run(RestartExplorerIfNeeded);
        SetStatus("安装完成。右键文件或文件夹即可使用 7zipContext。", success: true);
        AppendLog("安装完成。可以删除下载的 ZIP 和安装工具，不影响已安装程序。");
        MessageBox.Show("安装完成！\n\n右键文件或文件夹即可使用 7zipContext。\n开始菜单中的 7zipContext 可打开设置面板。",
            "安装成功", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task Uninstall()
    {
        AppendLog("清理菜单注册与文件关联……");

        var installedScript = Path.Combine(InstallRoot, "scripts", "uninstall.ps1");
        var fallbackScript = Path.Combine(PayloadRoot, "scripts", "uninstall.ps1");
        var script = File.Exists(installedScript) ? installedScript : fallbackScript;
        if (!File.Exists(script)) throw new FileNotFoundException("找不到卸载脚本，安装包可能不完整。", script);
        await Task.Run(StopShellProcesses);

        var args = new List<string>();
        // 备用脚本位于安装包内，绝不能随 -Purge 删除安装包自身。
        if (_removeFiles.IsChecked == true && script == installedScript) args.Add("-Purge");
        if (_removeEngine.IsChecked == true) args.Add("-PurgeEngine");

        var exitCode = await RunScript(script, args.ToArray());
        if (exitCode != 0)
            throw new InvalidOperationException($"卸载未完成（退出码 {exitCode}）。请查看日志中的注册残留或文件清理错误，处理后重试。");

        var removeFiles = _removeFiles.IsChecked == true;
        await Task.Run(() =>
        {
            if (removeFiles && Directory.Exists(InstallRoot))
            {
                if (SamePath(BundleRoot, InstallRoot))
                {
                    // Windows 不能删除正在运行的卸载工具自身；其余目录可以全部清掉。
                    foreach (var dir in new[] { "dist", "scripts", "package", "tests" })
                    {
                        var path = Path.Combine(InstallRoot, dir);
                        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    }
                    AppendLog("程序文件已删除；关闭窗口后可手动删除本卸载工具。 ");
                }
                else
                {
                    Directory.Delete(InstallRoot, recursive: true);
                    AppendLog("程序文件已删除。");
                }
            }
        });
        await Task.Run(RestartExplorerIfNeeded);
        SetStatus("卸载完成，菜单与文件关联已清理。", success: true);
        AppendLog("卸载完成。");
        MessageBox.Show("卸载完成。", "7zipContext", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void ValidatePayload()
    {
        var required = new[]
        {
            Path.Combine("dist", "7zipContext.exe"),
            Path.Combine("dist", "7zipContext.Shell.comhost.dll"),
            Path.Combine("dist", "7zipContext.Classic.comhost.dll"),
            Path.Combine("scripts", "install.ps1"),
            Path.Combine("scripts", "uninstall.ps1"),
            Path.Combine("scripts", "7zipContext-menu.ps1"),
            Path.Combine("scripts", "deployment.ps1"),
            Path.Combine("scripts", "DeploymentFiles.cs"),
            Path.Combine("package", "AppxManifest.xml"),
            Path.Combine("tests", "7zipContext.ComProbe", "bin", "Release", "net8.0-windows", "7zipContext.ComProbe.exe"),
        };
        var missing = required.Where(p => !File.Exists(Path.Combine(PayloadRoot, p))).ToArray();
        if (missing.Length > 0)
            throw new FileNotFoundException("安装包不完整，请重新下载并完整解压后再运行。缺少：" + string.Join("、", missing));
    }

    private static void CopyPayload(string targetRoot)
    {
        Directory.CreateDirectory(targetRoot);
        foreach (var dir in new[] { "dist", "scripts", "package", "tests" })
            CopyDirectory(Path.Combine(PayloadRoot, dir), Path.Combine(targetRoot, dir));

        // 已安装目录也保留一份图形化工具，设置面板的「卸载」按钮可随时打开它。
        foreach (var name in new[]
                 {
                     "7zipContext.Setup.exe", "7zipContext.Setup.dll",
                     "7zipContext.Setup.deps.json", "7zipContext.Setup.runtimeconfig.json",
                     "ZipContext.Core.dll",
                 })
        {
            var source = Path.Combine(BundleRoot, name);
            var destination = Path.Combine(targetRoot, name);
            // 由已安装工具自己发起修复时，不替换它正在加载的自身程序集。
            if (File.Exists(source) && !SamePath(BundleRoot, InstallRoot)) File.Copy(source, destination, overwrite: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        FileDeployment.CopyTree(source, destination, true);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private async Task<int> RunScript(string script, params string[] switches)
    {
        var powershell = Path.Combine(Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var quotedScript = "'" + script.Replace("'", "''") + "'";
        var command = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
                      "$ErrorActionPreference = 'Stop'; try { " +
                      $"& {quotedScript} {string.Join(' ', switches)}; exit $LASTEXITCODE " +
                      "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";

        var psi = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (_deploymentToken is not null) psi.Environment[DeploymentLease.EnvironmentName] = _deploymentToken;
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppendLog(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppendLog("[错误] " + e.Data); };

        return await Task.Run(() =>
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动系统安装组件。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(180000))
            {
                try { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } catch { }
                throw new TimeoutException("系统安装步骤超过三分钟未完成，已停止并尝试恢复文件。请保留安装日志与备份。");
            }
            process.WaitForExit(); // Flush completed output callbacks.
            return process.ExitCode;
        });
    }

    private async Task<int> RunExecutable(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (_deploymentToken is not null) start.Environment[DeploymentLease.EnvironmentName] = _deploymentToken;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = await Task.Run(() => Process.Start(start) ?? throw new IOException("无法启动候选程序准备引擎。"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } catch { }
            throw new TimeoutException("准备引擎超过五分钟未完成，程序文件尚未切换，请稍后重试。");
        }
        return process.ExitCode;
    }

    private async Task RecoverInterruptedInstallations()
    {
        var stages = await Task.Run(() => Directory.Exists(DeploymentRoot)
            ? Directory.GetDirectories(DeploymentRoot, "install-*") : Array.Empty<string>());
        foreach (var stage in stages)
        {
            try
            {
                var work = Path.Combine(stage, "transaction");
                if (!File.Exists(Path.Combine(work, "transaction.xml"))) { await TryCleanupStage(stage); continue; }
                var previous = await Task.Run(() => FileDeployment.Load(work, InstallRoot));
                if (previous.Journal.State == "committed") { await TryCleanupStage(stage); continue; }
                if (previous.Journal.State != "prepared" && previous.Journal.State != "rolledBack") await Task.Run(StopShellProcesses);
                await Task.Run(previous.Rollback);
                AppendLog("已恢复上次未完成操作的文件：" + stage);
                await Task.Run(previous.Cleanup);
                await TryCleanupStage(stage);
            }
            catch (Exception ex) { throw new IOException("上次部署恢复未完成，备份保留在：" + stage + "\n" + ex.Message, ex); }
        }
    }

    private async Task TryCleanupStage(string stage)
    {
        try
        {
            var prefix = Path.GetFullPath(DeploymentRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(stage).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("准备目录越出预期范围。");
            await Task.Run(() => FileDeployment.DeleteOwnedTree(stage));
        }
        catch (Exception ex) { AppendLog("准备目录保留在 " + stage + "：" + ex.Message); }
    }

    private void StopShellProcesses()
    {
        foreach (var name in new[] { "7zipContext", "7zipContext.Shell" })
            foreach (var process in Process.GetProcessesByName(name))
                try { process.Kill(); process.WaitForExit(5000); } catch { }
                finally { process.Dispose(); }

        foreach (var process in Process.GetProcessesByName("dllhost"))
        {
            try
            {
                if (process.Modules.Cast<ProcessModule>().Any(m =>
                        m.FileName.Contains("7zipContext", StringComparison.OrdinalIgnoreCase)))
                { process.Kill(); process.WaitForExit(5000); }
            }
            catch { }
            finally { process.Dispose(); }
        }

        var explorers = Process.GetProcessesByName("explorer");
        if (explorers.Length == 0) return;
        _explorerStopped = true;
        foreach (var process in explorers)
            try { process.Kill(); process.WaitForExit(5000); } catch { }
            finally { process.Dispose(); }
    }

    private void RestartExplorerIfNeeded()
    {
        if (!_explorerStopped) return;
        _explorerStopped = false;
        try
        {
            if (Process.GetProcessesByName("explorer").Length == 0)
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch (Exception ex) { AppendLog("资源管理器未自动恢复，请手动运行 explorer.exe：" + ex.Message); }
    }

    private void OpenSettings()
    {
        var exe = Path.Combine(InstallRoot, "dist", "7zipContext.exe");
        if (!File.Exists(exe)) return;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("无法打开设置：" + ex.Message, error: true); }
    }

    private void RefreshSettingsButton() =>
        _openSettings.IsEnabled = File.Exists(Path.Combine(InstallRoot, "dist", "7zipContext.exe"));

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _install.IsEnabled = !busy;
        _uninstall.IsEnabled = !busy;
        _openSettings.IsEnabled = !busy && File.Exists(Path.Combine(InstallRoot, "dist", "7zipContext.exe"));
        _removeFiles.IsEnabled = !busy;
        _removeEngine.IsEnabled = !busy;
        _setAsDefault.IsEnabled = !busy;
        _progress.IsIndeterminate = busy;
    }

    private void SetStatus(string text, bool success = false, bool error = false)
    {
        _status.Text = text;
        _status.Foreground = error ? Brushes.Firebrick : success ? Brushes.ForestGreen : Brushes.DimGray;
    }

    private void AppendLog(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLog(line));
            return;
        }
        _log.AppendText(line + Environment.NewLine);
        _log.ScrollToEnd();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            SetStatus("操作正在进行，请等待完成后再关闭窗口。");
            return;
        }
        base.OnClosing(e);
    }
}
