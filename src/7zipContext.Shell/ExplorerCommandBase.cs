using System.Runtime.InteropServices;

namespace ZipContext.Shell;

/// <summary>
/// IExplorerCommand 动词基类。每个右键项一个实例；
/// GetState 按选中项类型显隐，Invoke 启动 7zipContext.exe 执行对应 CLI 动词。
/// 全部方法必须快速返回（Explorer UI 线程），长操作一律丢给 7zipContext.exe 进程。
/// 注意：基类不 ComVisible——由子类携带 CLSID 暴露给 COM（comhost 要求）。
/// </summary>
public class ExplorerCommandBase : ComInterop.IExplorerCommand
{
    /// <summary>菜单文字。含 {name} 占位符时用首个选中项的基名替换。</summary>
    protected virtual string Title => "7zipContext";
    protected virtual string? Icon => null;
    protected virtual string? ToolTip => null;
    protected virtual Guid CanonicalGuid => Guid.Empty;
    protected virtual uint Flags => 0;

    /// <summary>7zipContext.exe 的 CLI 动词（--smart-extract 等）。</summary>
    protected virtual string Verb => "--open";

    /// <summary>可见性判定：传入选中路径，返回是否显示。</summary>
    protected virtual bool IsVisible(IReadOnlyList<string> paths) => true;

    /// <summary>空选中项快速查询时是否仍返回启用。全局开关关闭的动词（如用 7-Zip 打开）应设为 false。</summary>
    protected virtual bool VisibleWhenEmpty => true;

    public int GetTitle(ComInterop.IShellItemArray? psiItemArray, out IntPtr ppszName)
    {
        // 异常绝不能越过 COM 边界：标题失败只退回中性文字，不能让 dllhost 终止。
        try
        {
            var title = Title;
            if (title.Contains("{name}", StringComparison.Ordinal))
            {
                var paths = ComInterop.GetPaths(psiItemArray);
                // 解压类菜单文字用“实际解压目标”命名：分卷多选时取 .001，而不是父目录或 .002。
                var displayPaths = IsExtractVerb(Verb)
                    ? ZipContext.Core.ArchiveSelection.TargetsForExtraction(paths)
                    : paths;
                if (displayPaths.Count > 0)
                {
                    // 与 7zipContext.App 压缩输出共用命名规则：单选同名 / 多选父文件夹名
                    title = title.Replace("{name}", ZipContext.Core.NamingHelper.TargetBaseName(displayPaths));
                }
                else
                {
                    // 快速构建阶段还没有选中项 → 用中性文案占位
                    title = title.Replace("{name}", "同名");
                }
            }
            ppszName = Marshal.StringToCoTaskMemUni(title);
        }
        catch (Exception ex)
        {
            Fail("GetTitle 失败", ex);
            ppszName = Marshal.StringToCoTaskMemUni("7zipContext");
        }
        return ComInterop.S_OK;
    }

    public int GetIcon(ComInterop.IShellItemArray? psiItemArray, out IntPtr ppszIcon)
    {
        // 必须返回有效图标引用：空指针在 Win11 新菜单构建路径上不被容忍
        // （曾导致菜单项整体消失 / 显示默认丑图标）。
        // 注意：dllhost 代理宿主里 AppContext.BaseDirectory 为空，
        // 用相对路径会得到 "7zipContext.Shell.exe,0" 这种指不到文件的字符串 → 图标丢失。
        try
        {
            var dir = AssemblyDir();
            var icon = Icon ?? (string.IsNullOrEmpty(dir)
                ? "7zipContext.exe,0"
                : Path.Combine(dir, "7zipContext.exe") + ",0");
            ppszIcon = Marshal.StringToCoTaskMemUni(icon);
        }
        catch (Exception ex)
        {
            // 兜底文案不能让异常越过 COM 边界。
            Fail("GetIcon 失败", ex);
            ppszIcon = Marshal.StringToCoTaskMemUni("7zipContext.exe,0");
        }
        return ComInterop.S_OK;
    }

    /// <summary>程序集所在目录（dllhost 里 AppContext.BaseDirectory 可能为空，这里最可靠）。</summary>
    private static string AssemblyDir()
    {
        try { return Path.GetDirectoryName(typeof(ExplorerCommandBase).Assembly.Location) ?? ""; }
        catch { return ""; }
    }

    public int GetToolTip(ComInterop.IShellItemArray? psiItemArray, out IntPtr ppszInfotip)
    {
        ppszInfotip = ToolTip is null
            ? IntPtr.Zero
            : Marshal.StringToCoTaskMemUni(ToolTip);
        return ToolTip is null ? ComInterop.E_NOTIMPL : ComInterop.S_OK;
    }

    public int GetCanonicalName(out Guid pguidCommandName)
    {
        pguidCommandName = CanonicalGuid;
        return ComInterop.S_OK;
    }

    public int GetState(ComInterop.IShellItemArray? psiItemArray, bool fOkToBeSlow, out uint pCmdState)
    {
        // Win11 新版右键菜单构建初期会以「空选中项」快速查询一次。
        // 根因修复：此时必须乐观返回 ECS_ENABLED —— 返回 ECS_HIDDEN 会被永久丢弃，
        // 这正是新版菜单完全不显示 7zipContext 的原因（「显示更多 options」走的是完整查询所以能看到）。
        try
        {
            var paths = ComInterop.GetPaths(psiItemArray);
            if (paths.Count == 0)
            {
                // 但被 ini 全局关闭的动词（如「用 7-Zip 打开」）在空查询时就该隐藏。
                pCmdState = VisibleWhenEmpty ? ComInterop.ECS_ENABLED : ComInterop.ECS_HIDDEN;
                return ComInterop.S_OK;
            }
            // 有真实选中项时按规则显隐（压缩包→解压项；任意文件/文件夹→压缩项）
            pCmdState = IsVisible(paths) ? ComInterop.ECS_ENABLED : ComInterop.ECS_HIDDEN;
        }
        catch (Exception ex)
        {
            // 判定失败时保持可见：宁可多显示一项，也不要因异常让菜单项消失或让 dllhost 崩掉。
            Fail("GetState 失败", ex);
            pCmdState = ComInterop.ECS_ENABLED;
        }
        return ComInterop.S_OK;
    }

    public int Invoke(ComInterop.IShellItemArray? psiItemArray, IntPtr pbc)
    {
        IReadOnlyList<string> targets;
        try
        {
            var paths = ComInterop.GetPaths(psiItemArray);
            if (paths.Count == 0) return ComInterop.E_FAIL;

            // 解压动词传分卷入口及普通压缩包，统一去重；压缩动词仍传完整选择。
            targets = IsExtractVerb(Verb)
                ? ZipContext.Core.ArchiveSelection.TargetsForExtraction(paths)
                : paths;
            if (targets.Count == 0) return ComInterop.E_FAIL;
        }
        catch (Exception ex)
        {
            return Fail("读取选中项失败", ex);
        }

        // 长操作全部交给 7zipContext.exe（独立进程，崩溃绝不拖垮 Explorer）
        // 注意：命令行必须用 ArgumentList（UseShellExecute=false）——
        // ShellExecute 路径不接受 ArgumentList，且在 dllhost 代理宿主里可能直接失败。
        try
        {
            var exePath = ResolveExe();
            if (exePath is null) return Fail("找不到 7zipContext.exe", null);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add(Verb);
            foreach (var p in targets) psi.ArgumentList.Add(p);

            System.Diagnostics.Process.Start(psi);
            return ComInterop.S_OK;
        }
        catch (Exception ex)
        {
            return Fail("启动 7zipContext.exe 失败", ex);
        }
    }

    private static bool IsExtractVerb(string verb) => verb is
        "--smart-extract" or "--smart-extract-delete" or
        "--extract-to-name" or "--extract-to";

    /// <summary>定位 7zipContext.exe：程序集所在目录 → AppContext.BaseDirectory → 进程目录 → 环境变量覆盖（测试用）。</summary>
    private static string? ResolveExe()
    {
        var overridePath = Environment.GetEnvironmentVariable("ZIPCONTEXT_EXE");
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath)) return overridePath;

        var asmDir = string.Empty;
        try { asmDir = Path.GetDirectoryName(typeof(ExplorerCommandBase).Assembly.Location) ?? ""; } catch { }
        var baseDir = AppContext.BaseDirectory ?? "";
        var procDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";

        foreach (var dir in new[] { asmDir, baseDir, procDir })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir, "7zipContext.exe"),
                         Path.Combine(dir, "..", "7zipContext.exe"),
                     })
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    /// <summary>Invoke 失败时落盘诊断（dllhost 里没有控制台，只能写文件）。</summary>
    private int Fail(string what, Exception? ex)
    {
        try
        {
            var log = Path.Combine(Path.GetTempPath(), "7zipContext-invoke-error.log");
            ZipContext.Core.BoundedLog.TryAppend(log,
                $"{DateTime.Now:HH:mm:ss} [{GetType().Name}] {what} | base={AppContext.BaseDirectory} | proc={Environment.ProcessPath} | {ex}\n");
        }
        catch { /* 诊断失败不影响返回 */ }
        return ComInterop.E_FAIL;
    }

    public int GetFlags(out uint pFlags)
    {
        pFlags = Flags;
        return ComInterop.S_OK;
    }

    public int EnumSubCommands(out IntPtr ppEnum)
    {
        ppEnum = IntPtr.Zero;
        return ComInterop.E_NOTIMPL;
    }
}
