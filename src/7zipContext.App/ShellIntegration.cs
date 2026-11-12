using System.IO;
using Microsoft.Win32;

namespace ZipContext.App.ShellIntegration;

/// <summary>
/// 清理 v1 残留并注册可选的 7-Zip 打开方式，不改变用户的默认程序。
/// v1 残留包含注册表动词、IPreviewHandler 和旧 ProgId——
/// 老版本装过的机器，光删代码是清不掉的，必须靠这里。
/// 菜单动词由 AppX 包（7zipContext.Shell）提供，经典菜单由 7zipContext.Classic 提供。
/// </summary>
public static class ShellIntegration
{
    private const string ProgIdPrefix = "7zipContext";

    /// <summary>为 7-Zip 文件管理器（7zFM.exe）注册可选打开方式的扩展名。</summary>
    private static readonly string[] DefaultExtensions = { ".zip", ".7z", ".rar" };

    /// <summary>v1 的预览窗格处理器 CLSID 与 ShellEx 接口 IID（仅用于清理）。</summary>
    private const string LegacyPreviewClsid = "{B9A10010-8C3F-4A2E-9E6D-5F1A2B3C4D10}";
    private const string PreviewHandlerIid = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    private static readonly string[] LegacyArchiveVerbKeys =
        { "SmartExtract", "SmartExtractDelete", "ExtractToName", "ExtractToFolder", "Preview" };
    private static readonly string[] LegacyFileVerbKeys =
        { "AddZip", "Add7z", "QuickZip" };

    private static readonly string[] ArchiveExtensions =
        { ".zip", ".7z", ".rar", ".tar", ".tgz", ".gz", ".bz2", ".xz", ".zst", ".cab", ".iso", ".arj", ".lzh" };

    /// <summary>安装时清理 v1 残留（重装/升级老版本都走这里），并把 7zFM 注册成打开方式。</summary>
    public static void Install()
    {
        using var deployment = ZipContext.Deployment.DeploymentLease.Acquire(ZipContext.Core.EngineLocator.ConfigRoot,
            Environment.GetEnvironmentVariable(ZipContext.Deployment.DeploymentLease.EnvironmentName));
        using var gate = EngineOperationLock.Acquire(ZipContext.Core.EngineLocator.ConfigRoot);
        ZipContext.Core.EngineLocator.InvalidateCache();
        CleanupAll();
        RegisterOpenWith();
        NotifyShellChanged();
    }

    /// <summary>卸载时同样清一遍，保证 0 残留。</summary>
    public static void Uninstall()
    {
        using var deployment = ZipContext.Deployment.DeploymentLease.Acquire(ZipContext.Core.EngineLocator.ConfigRoot,
            Environment.GetEnvironmentVariable(ZipContext.Deployment.DeploymentLease.EnvironmentName));
        using var gate = EngineOperationLock.Acquire(ZipContext.Core.EngineLocator.ConfigRoot);
        CleanupAll();
        NotifyShellChanged();
    }

    private static void CleanupAll()
    {
        CleanupLegacyVerbs();
        CleanupLegacyPreviewHandler();
        CleanupLegacyAssociations();
    }

    // ---------- 可选打开方式：7zFM.exe ----------

    /// <summary>
    /// 7zFM.exe 来自安装阶段解析出的引擎目录（本机已有或下载解包）。
    /// 引擎缺失时返回 null，RegisterOpenWith 会跳过注册而不是写一条死路径。
    /// </summary>
    private static string? SevenZipFileManager =>
        ZipContext.Core.EngineLocator.Resolve("7zFM.exe");

    /// <summary>
    /// 为 .zip/.7z/.rar 注册 ProgId 和 OpenWithProgids，使 7-Zip 可供选择。
    /// 不写扩展名默认值，也不删除 UserChoice；默认应用由用户在系统设置选择。
    /// </summary>
    private static void RegisterOpenWith()
    {
        var fm = SevenZipFileManager;
        if (fm is null) return;   // 引擎未就绪：不写死路径，等下次 install 再注册
        RegisterOpenWithPath(fm);
    }

    /// <summary>调用者持有引擎锁；只刷新可选打开方式，不做清理或更改默认程序。</summary>
    internal static void RefreshOpenWithForEngine(string directory)
    {
        if (!ZipContext.Core.EngineLocator.IsUsable(directory))
            throw new IOException("引擎目录不完整，不能刷新打开方式。");
        RegisterOpenWithPath(Path.Combine(directory, "7zFM.exe"));
        NotifyShellChanged();
    }

    private static void RegisterOpenWithPath(string fm)
    {
        foreach (var ext in DefaultExtensions)
        {
            var progId = $"{ProgIdPrefix}{ext}";

            using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}"))
            {
                k.SetValue("", $"7-Zip 压缩文件 ({ext})");
                using (var icon = k.CreateSubKey("DefaultIcon"))
                    icon.SetValue("", $"\"{fm}\",0");
                using (var cmd = k.CreateSubKey(@"shell\open\command"))
                    cmd.SetValue("", $"\"{fm}\" \"%1\"");
            }

            using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids"))
                k.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);

        }
    }

    /// <summary>打开系统「默认应用」设置页，让用户点一次"始终使用"。</summary>
    public static void OpenDefaultAppsSettings()    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:defaultapps",
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private static void CleanupLegacyVerbs()
    {
        foreach (var ext in ArchiveExtensions)
            foreach (var key in LegacyArchiveVerbKeys)
                TryDeleteTree($@"Software\Classes\SystemFileAssociations\{ext}\shell\7zipContext.{key}");

        foreach (var target in new[] { @"Software\Classes\*\shell", @"Software\Classes\Directory\shell" })
            foreach (var key in LegacyFileVerbKeys)
                TryDeleteTree($@"{target}\7zipContext.{key}");
    }

    private static void CleanupLegacyPreviewHandler()
    {
        TryDeleteTree($@"Software\Classes\CLSID\{LegacyPreviewClsid}");
        foreach (var ext in ArchiveExtensions)
            TryDeleteTree($@"Software\Classes\{ext}\ShellEx\{PreviewHandlerIid}");

        using var k = Registry.CurrentUser.OpenSubKey(@"Software\7zipContext", writable: true);
        k?.DeleteValue("PreviewHandler", throwOnMissingValue: false);
    }

    private static void CleanupLegacyAssociations()
    {
        foreach (var ext in new[] { ".zip", ".7z", ".tar", ".tgz", ".gz", ".bz2", ".xz" })
        {
            TryDeleteTree($@"Software\Classes\{ProgIdPrefix}{ext}");
            using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true);
            k?.DeleteValue($"{ProgIdPrefix}{ext}", throwOnMissingValue: false);
        }
    }

    private static void TryDeleteTree(string path)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
        catch { /* 清理尽力而为 */ }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static void NotifyShellChanged()
    {
        const uint SHCNE_ASSOCCHANGED = 0x08000000;
        const uint SHCNF_IDLIST = 0x0000;
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }
}
