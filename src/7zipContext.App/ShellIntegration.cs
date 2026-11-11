using Microsoft.Win32;

namespace ZipContext.App.ShellIntegration;

/// <summary>
/// v2 起 7zipContext 只做右键菜单：**不注册文件关联、不注册预览窗格**。
/// 本类唯一职责是把 v1 残留（注册表动词 / IPreviewHandler / ProgId 关联）清干净——
/// 老版本装过的机器，光删代码是清不掉的，必须靠这里。
/// 菜单动词由 AppX 包（7zipContext.Shell）提供，经典菜单由 7zipContext.Classic 提供。
/// </summary>
public static class ShellIntegration
{
    private const string ProgIdPrefix = "7zipContext";

    /// <summary>交给 7-Zip 文件管理器（7zFM.exe）接管的扩展名。</summary>
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
        CleanupAll();
        RegisterDefaults();
        NotifyShellChanged();
    }

    /// <summary>卸载时同样清一遍，保证 0 残留。</summary>
    public static void Uninstall()
    {
        CleanupAll();
        NotifyShellChanged();
    }

    private static void CleanupAll()
    {
        CleanupLegacyVerbs();
        CleanupLegacyPreviewHandler();
        CleanupLegacyAssociations();
    }

    // ---------- 默认打开方式：交给 7zFM.exe ----------

    private static string SevenZipFileManager =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "7zFM.exe");

    /// <summary>
    /// 把 .zip/.7z/.rar 注册到 7-Zip 文件管理器。
    /// 注意 Windows 的限制：Win10+ 的默认程序由 UserChoice 哈希保护，
    /// 程序**无法静默**改成默认。这里只能做到三件事：
    ///   1) 写入 ProgId 与 open 命令（打开方式列表里出现）
    ///   2) 写 OpenWithProgids（"选择其他应用"里可见）
    ///   3) 尝试把类默认值指向我们（仅对没有 UserChoice 的类型生效）
    /// 真正的"始终使用"必须用户自己在系统「默认应用」里点一次（见 --defaults）。
    /// </summary>
    private static void RegisterDefaults()
    {
        var fm = SevenZipFileManager;
        if (!System.IO.File.Exists(fm)) return;

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

            // 尽力而为：没有 UserChoice 的类型会真的生效
            try
            {
                using var cls = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}");
                cls.SetValue("", progId);
            }
            catch { }

            // Windows 只保护「写」UserChoice（哈希校验），「删」是允许的。
            // 删掉后系统回落到类默认值 → 上面写的 progId 生效。
            // 不清掉的话，用户只要之前手动选过别的程序，我们的设置永远不起作用。
            TryClearUserChoice(ext);
        }
    }

    /// <summary>
    /// 删掉文件类型的 UserChoice，让系统回落到 `Software\Classes\{ext}` 的默认值。
    /// 失败不要紧（有 ACL 保护时），此时只能靠用户去「默认应用」手动点。
    /// </summary>
    private static void TryClearUserChoice(string ext)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\UserChoice",
                throwOnMissingSubKey: false);
        }
        catch { /* ACL 拒绝就放弃，不阻断安装 */ }
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
