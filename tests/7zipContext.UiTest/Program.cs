using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ZipContext.UiTest;

/// <summary>
/// 真实 UI 复现：造一个和用户压缩包同构的 zip，打开资源管理器 → 选中 → Shift+F10 拉起经典右键菜单 → 截屏。
/// 用途：观察「压缩文件预览」到底渲染成什么样（普通条目 / 级联子菜单 / 树）。
/// 用法：7zipContext.UiTest.exe [截图输出路径]
/// </summary>
internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string? cls, string? title);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder cls, int max);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [STAThread]
    private static void Main(string[] args)
    {
        // 模式：menu <zip> <outPng> —— 真实弹出经典右键菜单并截屏
        if (args.Length >= 3 && args[0] == "menu")
        {
            MenuShot.ShowAndCapture(args[1], args[2], Console.WriteLine);
            return;
        }

        // 模式：live <zip> <outPng> [菜单序号] —— 活动菜单逐项 dump，可选触发某项
        if (args.Length >= 3 && args[0] == "live")
        {
            int? idx = args.Length >= 4 ? int.Parse(args[3]) : null;
            MenuLive.Run(args[1], args[2], idx, Console.WriteLine);
            return;
        }

        var outPng = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "7zipContext-menu.png");

        // 1) 造同构压缩包
        var dir = Path.Combine(Path.GetTempPath(), "7zipContext-uitest");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var stage = Path.Combine(dir, "stage");
        Directory.CreateDirectory(Path.Combine(stage, "html", "cs-CZ"));
        Directory.CreateDirectory(Path.Combine(stage, "html", "da-DK"));
        Directory.CreateDirectory(Path.Combine(stage, "html", "de-DE"));
        Directory.CreateDirectory(Path.Combine(stage, "html", "windows"));
        File.WriteAllText(Path.Combine(stage, "html", "cs-CZ", "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(stage, "html", "da-DK", "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(stage, "html", "de-DE", "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(stage, "html", "windows", "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(stage, "VERSION"), "1.0.0");

        var zip = Path.Combine(dir, "PolicyTemplates.zip");
        var sevenZip = FindSevenZip();
        var psi = new ProcessStartInfo { FileName = sevenZip, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stage };
        foreach (var a in new[] { "a", "-tzip", zip, "html", "VERSION", "-y", "-bso0", "-bsp0" }) psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!) p.WaitForExit(20000);
        Console.WriteLine($"zip created: {zip} exists={File.Exists(zip)}");

        // 2) 打开资源管理器并选中该文件
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{dir}\"", UseShellExecute = true });
        Thread.Sleep(3500);

        var hwnd = FindExplorerWindow(dir);
        Console.WriteLine($"explorer hwnd={hwnd}");
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine("--- all CabinetWClass windows ---");
            EnumWindows((h, _) =>
            {
                var cls = new System.Text.StringBuilder(256);
                GetClassNameW(h, cls, 256);
                if (cls.ToString() == "CabinetWClass")
                {
                    var t = new System.Text.StringBuilder(512);
                    GetWindowTextW(h, t, 512);
                    Console.WriteLine($"  {h} \"{t}\"");
                }
                return true;
            }, IntPtr.Zero);
            return;
        }
        ShowWindow(hwnd, 9 /* SW_RESTORE */);
        SetForegroundWindow(hwnd);
        Thread.Sleep(700);

        // 3) 用 Shell.Application 精确选中该文件（避免 type-ahead 失焦）
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(shellType!)!;
            dynamic? target = null;
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    string loc = w.Document.Folder.Self.Path;
                    if (string.Equals(loc, dir, StringComparison.OrdinalIgnoreCase)) { target = w; break; }
                }
                catch { }
            }
            if (target is not null)
            {
                dynamic folder = target.Document.Folder;
                dynamic item = folder.ParseName("PolicyTemplates.zip");
                target.Document.SelectItem(item, 1);
                Console.WriteLine("selected via Shell.Application");
            }
            else Console.WriteLine("explorer window not found via Shell.Application");
        }
        catch (Exception ex) { Console.WriteLine("select failed: " + ex.Message); }

        SetForegroundWindow(hwnd);
        Thread.Sleep(400);

        // 4) Shift+F10 拉起经典右键菜单
        SendKeys.SendWait("+{F10}");
        Thread.Sleep(1500);

        // 5) 截图
        var bounds = Screen.PrimaryScreen!.Bounds;
        using (var bmp = new Bitmap(bounds.Width, bounds.Height))
        {
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
            bmp.Save(outPng, ImageFormat.Png);
        }
        Console.WriteLine($"screenshot saved: {outPng}");

        Thread.Sleep(300);
        SendKeys.SendWait("{ESC}");
        Thread.Sleep(200);
        SendKeys.SendWait("{ESC}");
    }

    private static IntPtr FindExplorerWindow(string folder)
    {
        var name = Path.GetFileName(folder);
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            var cls = new System.Text.StringBuilder(256);
            GetClassNameW(h, cls, 256);
            if (cls.ToString() != "CabinetWClass") return true;
            var title = new System.Text.StringBuilder(512);
            GetWindowTextW(h, title, 512);
            if (title.ToString().Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>从可执行文件所在目录向上找 dist\tools\7z.exe（不写死本机路径）。</summary>
    private static string FindSevenZip()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "dist", "tools", "7z.exe");
            if (File.Exists(candidate)) return candidate;
            var direct = Path.Combine(dir, "tools", "7z.exe");
            if (File.Exists(direct)) return direct;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return "7z.exe"; // 退回 PATH
    }

}
