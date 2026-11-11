using System.Windows;

namespace ZipContext.Setup;

public static class Program
{
    [STAThread]
    public static int Main()
    {
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new SetupWindow();
            app.MainWindow = window;
            app.Run(window);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"安装工具无法启动：{ex.Message}", "7zipContext 安装",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
