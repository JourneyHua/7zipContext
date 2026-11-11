using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ZipContext.App;

/// <summary>加密包密码输入框。返回 null 表示用户取消。</summary>
public class PasswordDialog : Window
{
    private readonly PasswordBox _box;
    private readonly CheckBox _remember;

    public string? Password { get; private set; }

    private PasswordDialog(string archiveName)
    {
        Title = "7zipContext - 输入密码";
        Width = 380;
        Height = 210;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = $"「{archiveName}」已加密，请输入密码：",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(label, 0);
        grid.Children.Add(label);

        _box = new PasswordBox { Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(_box, 1);
        grid.Children.Add(_box);

        _remember = new CheckBox
        {
            Content = "本次会话内记住密码",
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(_remember, 2);
        grid.Children.Add(_remember);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var ok = new Button { Content = "确定", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { Password = _box.Password; DialogResult = true; };
        var cancel = new Button { Content = "取消", Width = 80, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);

        Content = grid;
        Loaded += (_, _) => _box.Focus();
    }

    /// <summary>弹出密码框；null = 取消。</summary>
    public static string? Prompt(string archiveName)
    {
        var dlg = new PasswordDialog(archiveName);
        return dlg.ShowDialog() == true ? dlg.Password : null;
    }
}
