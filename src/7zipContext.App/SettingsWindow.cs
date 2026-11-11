using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZipContext.Core;

namespace ZipContext.App;

/// <summary>
/// 开始菜单图标 / 无参启动 / --settings 打开的设置面板。
/// 纯代码构建 UI（项目没有 XAML 构建管线，与 PasswordDialog 保持一致）。
/// 所有联网/耗时操作都在后台线程，绝不卡住窗口。
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };

    // 预览规则
    private readonly TextBox _rootMax = new() { Width = 70 };
    private readonly TextBox _childrenPerFolder = new() { Width = 70 };
    private readonly CheckBox _showFiles = new() { Content = "子层也显示文件" };
    private readonly ComboBox _maxDepth = new() { Width = 90 };

    // 引擎
    private readonly TextBox _engineInfo = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
        Height = 150,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(8),
        Background = Brushes.WhiteSmoke,
    };

    // 默认打开方式
    private readonly TextBlock _assocInfo = new() { TextWrapping = TextWrapping.Wrap };

    public SettingsWindow()
    {
        Title = "7zipContext 设置";
        Width = 620;
        Height = 520;
        MinWidth = 540;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        TrySetIcon();

        var dock = new DockPanel { Margin = new Thickness(16) };

        // 顶部标题
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = "7zipContext",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        });
        header.Children.Add(new TextBlock
        {
            Text = "7-Zip 右键菜单增强层 · 设置与状态",
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 2, 0, 0),
        });
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        // 底部：状态 + 关闭
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var close = new Button { Content = "关闭", Width = 88, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        _status.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_status);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "预览规则", Content = BuildPreviewTab() });
        tabs.Items.Add(new TabItem { Header = "7-Zip 引擎", Content = BuildEngineTab() });
        tabs.Items.Add(new TabItem { Header = "默认打开方式", Content = BuildAssocTab() });
        tabs.Items.Add(new TabItem { Header = "关于", Content = BuildAboutTab() });
        dock.Children.Add(tabs);

        Content = dock;
        LoadPreview();
        RefreshEngine();
        RefreshAssoc();
    }

    // ------------------------------------------------------------ 预览规则

    private UIElement BuildPreviewTab()
    {
        var panel = Stack();
        panel.Children.Add(Tip("经典菜单里「压缩文件预览」的显示规则。保存后下一次右键立即生效，不需要重启资源管理器。"));

        panel.Children.Add(Group("根目录",
            Row(Label("最多显示项数", 130), _rootMax, Tip("0 = 不限制；默认 20")),
            Row(Label("每个子文件夹展开", 130), _childrenPerFolder, Tip("只展开文件夹；0 = 不展开子层")),
            Row(Label("展开层数", 130), _maxDepth, Tip("0 = 只看根目录，1 = 到第二层，2 = 到第三层")),
            _showFiles));

        var buttons = Row();
        var save = new Button { Content = "保存", Width = 90, IsDefault = true };
        save.Click += (_, _) => SavePreview();
        var reset = new Button { Content = "恢复默认", Width = 90 };
        reset.Click += (_, _) => { LoadPreview(resetToDefault: true); SavePreview(); };
        var openIni = new Button { Content = "打开 ini 文件", Width = 110 };
        openIni.Click += (_, _) =>
        {
            // 首次使用时 ini 还没生成，先写一份带注释的默认版，避免提示「路径不存在」
            if (!File.Exists(AppSettings.IniPath))
            {
                try { new AppSettings().Save(); }
                catch (Exception ex) { SetStatus($"无法创建 ini：{ex.Message}", error: true); return; }
            }
            OpenPath(AppSettings.IniPath);
        };
        buttons.Children.Add(save);
        buttons.Children.Add(reset);
        buttons.Children.Add(openIni);
        panel.Children.Add(buttons);

        panel.Children.Add(Tip("提示：.zip 预览走 .NET 直读中央目录，不依赖 7-Zip 引擎；其它格式才调用引擎。"));
        return Wrap(panel);
    }

    private void LoadPreview(bool resetToDefault = false)
    {
        var s = resetToDefault ? new AppSettings() : AppSettings.Load();
        _rootMax.Text = s.RootMax.ToString();
        _childrenPerFolder.Text = s.ChildrenPerFolder.ToString();
        _showFiles.IsChecked = s.ShowFilesInSubfolders;
        _maxDepth.Items.Clear();
        foreach (var d in new[] { 0, 1, 2 }) _maxDepth.Items.Add(d);
        _maxDepth.SelectedItem = s.MaxDepth;
    }

    private void SavePreview()
    {
        try
        {
            if (!int.TryParse(_rootMax.Text.Trim(), out var root) || root < 0)
                throw new ArgumentException("「最多显示项数」必须是 0 或正整数");
            if (!int.TryParse(_childrenPerFolder.Text.Trim(), out var children) || children < 0)
                throw new ArgumentException("「每个子文件夹展开」必须是 0 或正整数");
            var settings = new AppSettings
            {
                RootMax = root,
                ChildrenPerFolder = children,
                ShowFilesInSubfolders = _showFiles.IsChecked == true,
                MaxDepth = _maxDepth.SelectedItem is int d ? d : 1,
            };
            settings.Save();
            SetStatus("已保存，下一次右键生效。");
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败：{ex.Message}", error: true);
        }
    }

    // ------------------------------------------------------------ 7-Zip 引擎

    private UIElement BuildEngineTab()
    {
        var panel = Stack();
        panel.Children.Add(Tip("仓库与发布产物都不携带 7-Zip 本体。安装阶段探测本机已有引擎，或联网下载官方安装包并校验解包。"));
        panel.Children.Add(_engineInfo);

        var buttons = Row();
        buttons.Children.Add(MakeButton("刷新状态", _ => RefreshEngine()));
        buttons.Children.Add(MakeButton("重新检测", async b => await RunEngine(() => EngineProvisioner.Ensure(null, null), b)));
        buttons.Children.Add(MakeButton("检查更新", async b => await RunEngine(() => EngineProvisioner.CheckUpdate(force: true), b)));
        buttons.Children.Add(MakeButton("升级引擎", async b => await RunEngine(() => EngineProvisioner.Update(null), b)));
        panel.Children.Add(buttons);

        var buttons2 = Row();
        buttons2.Children.Add(MakeButton("打开引擎目录", _ => OpenPath(EngineLocator.EngineDirectory() ?? EngineLocator.ConfigRoot)));
        buttons2.Children.Add(MakeButton("查看安装脚本", _ => OpenInstallScript()));
        panel.Children.Add(buttons2);

        panel.Children.Add(Tip("更新检查只提示，不自动替换；升级会保留旧版本目录，可手动回滚。GitHub 不可达时用 install.ps1 -EnginePath 或 -EngineSource。"));
        return Wrap(panel);
    }

    private void RefreshEngine() => _engineInfo.Text = EngineProvisioner.Describe();

    private async Task RunEngine(Func<EngineProvisioner.EnsureResult> work, Button btn)
    {
        btn.IsEnabled = false;
        SetStatus("正在处理，请稍候…");
        try
        {
            var result = await Task.Run(work);
            SetStatus(result.Message, error: !result.Ok);
            RefreshEngine();
        }
        catch (Exception ex)
        {
            SetStatus($"操作失败：{ex.Message}", error: true);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------ 默认打开方式

    private UIElement BuildAssocTab()
    {
        var panel = Stack();
        panel.Children.Add(Tip(".zip / .7z / .rar 的 ProgId 由本工具注册到 7-Zip 文件管理器（7zFM.exe）。"));
        panel.Children.Add(_assocInfo);

        var buttons = Row();
        buttons.Children.Add(MakeButton("刷新状态", _ => RefreshAssoc()));
        buttons.Children.Add(MakeButton("重新注册", _ =>
        {
            try { ShellIntegration.ShellIntegration.Install(); RefreshAssoc(); SetStatus("已重新注册打开方式。"); }
            catch (Exception ex) { SetStatus($"注册失败：{ex.Message}", error: true); }
        }));
        buttons.Children.Add(MakeButton("系统「默认应用」", _ => ShellIntegration.ShellIntegration.OpenDefaultAppsSettings()));
        panel.Children.Add(buttons);

        panel.Children.Add(Tip("Windows 用哈希保护 UserChoice，程序无法静默设置「始终使用」。若右键打开仍不是 7-Zip，请在系统设置页手动选一次。"));
        return Wrap(panel);
    }

    private void RefreshAssoc()
    {
        var (ok, total) = AppSettings.AssociationStatus();
        var fm = EngineLocator.Resolve("7zFM.exe");
        _assocInfo.Text = $"已注册 {ok}/{total} 个扩展名\n" +
                          (fm is null ? "7zFM.exe：未找到（请先准备 7-Zip 引擎）" : $"7zFM.exe：{fm}");
    }

    // ------------------------------------------------------------ 关于

    private UIElement BuildAboutTab()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        var panel = Stack();
        panel.Children.Add(Tip(
            $"7zipContext {version}\n\n" +
            "不是压缩软件，而是 7-Zip 的 shell 增强层：压缩、解压、进度窗、密码框全部由 7-Zip 本体完成，\n" +
            "本项目只负责 Win11 现代菜单与经典菜单的内联目录树。\n\n" +
            "本项目代码：MIT。7-Zip 由 Igor Pavlov 开发，LGPL v2.1+ 及 BSD/unRAR 条款，运行时按需从官方获取。\n" +
            "7zipContext 不是 7-Zip 官方项目，与其无关联、未获背书。"));

        var buttons = Row();
        buttons.Children.Add(MakeButton("项目主页", _ => OpenUrl("https://github.com/JourneyHua/7zipContext")));
        buttons.Children.Add(MakeButton("查看第三方声明", _ => OpenPath(
            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "THIRD-PARTY-NOTICES.md"))));
        panel.Children.Add(buttons);

        panel.Children.Add(Group("卸载",
            Tip("powershell -ExecutionPolicy Bypass -File scripts\\uninstall.ps1"),
            Tip("默认保留 dist\\ 与已下载的引擎；加 -Purge 删除 dist\\，加 -PurgeEngine 删除本工具下载的引擎。")));
        return Wrap(panel);
    }

    // ------------------------------------------------------------ 控件辅助

    private static StackPanel Stack() => new() { Margin = new Thickness(12) };

    private static ScrollViewer Wrap(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    private static StackPanel Row(params UIElement[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var i in items)
        {
            // 还没设过 Margin 的控件补一个右间距，否则输入框和后面的说明文字会贴在一起
            if (i is FrameworkElement fe && fe.Margin == default)
                fe.Margin = new Thickness(0, 0, 10, 0);
            sp.Children.Add(i);
        }
        return sp;
    }

    private static TextBlock Label(string text, double width) => new()
    {
        Text = text,
        Width = width,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Tip(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.DimGray,
        Margin = new Thickness(0, 0, 0, 10),
    };

    /// <summary>不用 GroupBox：它的 Header 会压在边框线上，中文标题看着像被划掉。</summary>
    private static UIElement Group(string title, params UIElement[] items)
    {
        var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        outer.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12),
        };
        var sp = new StackPanel();
        foreach (var i in items) sp.Children.Add(i);
        border.Child = sp;
        outer.Children.Add(border);
        return outer;
    }

    private static Button MakeButton(string text, Action<Button> onClick)
    {
        var b = new Button { Content = text, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4) };
        b.Click += (_, _) => onClick(b);
        return b;
    }

    private void SetStatus(string message, bool error = false)
    {
        _status.Text = message;
        _status.Foreground = error ? Brushes.Firebrick : Brushes.DimGray;
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) && !Directory.Exists(path))
            {
                MessageBox.Show($"路径不存在：{path}", "7zipContext", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "7zipContext", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { }
    }

    /// <summary>
    /// 窗口左上角用 exe 自身的图标（ApplicationIcon 写进了 PE 资源），
    /// 不需要额外的 .ico 文件随发布产物分发。
    /// </summary>
    private void TrySetIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
            var hIcon = NativeMethods.ExtractIcon(IntPtr.Zero, exe, 0);
            // ExtractIcon 返回 1 表示文件里没有图标，0/NULL 表示失败
            if (hIcon == IntPtr.Zero || hIcon == (IntPtr)1) return;
            try
            {
                Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                NativeMethods.DestroyIcon(hIcon);   // CreateBitmapSourceFromHIcon 已复制位图
            }
        }
        catch { /* 图标是装饰，取不到不影响功能 */ }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeName, int nIconIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool DestroyIcon(IntPtr hIcon);
    }

    /// <summary>只打开脚本供查看，绝不静默执行 PowerShell（会弹 UAC/改注册，不该由设置面板代劳）。</summary>
    private static void OpenInstallScript()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (dir is not null)
        {
            // dist\scripts\install.ps1（发布产物）或 ..\scripts\install.ps1（源码目录）
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir, "scripts", "install.ps1"),
                         Path.Combine(dir, "..", "scripts", "install.ps1"),
                     })
            {
                if (!File.Exists(candidate)) continue;
                OpenPath(Path.GetFullPath(candidate));
                return;
            }
        }
        MessageBox.Show("未找到 scripts\\install.ps1。\n重装请在源码目录运行：\n" +
                        "powershell -ExecutionPolicy Bypass -File scripts\\install.ps1",
                        "7zipContext", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
