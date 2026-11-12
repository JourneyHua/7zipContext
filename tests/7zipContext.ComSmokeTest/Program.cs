using System.Diagnostics;
using System.Runtime.InteropServices;
using ZipContext.Shell;

namespace ZipContext.ComSmokeTest;

// 7zipContext 右键动词冒烟测试（进程内 + CLI 端到端）：
//   直接实例化 8 个 IExplorerCommand 动词类，覆盖 Explorer 菜单构建的同款调用：
//   GetState / GetTitle / GetIcon / Invoke
// 重点回归：Win11 新菜单「空选中项快速查询」不得返回 ECS_HIDDEN（曾导致新版菜单完全不显示）。
// 说明：CoCreateInstance 跨进程激活无法在沙箱测试进程中完成（SCM 拉起被拦截，0x80080005）；
//       真实 Explorer 激活已由「显示更多选项」中出现的动态标题（解压到 <包名>\）证实。
// 用法：dotnet run --project tests/7zipContext.ComSmokeTest
internal static class Program
{
    private const uint ECS_ENABLED = 0, ECS_HIDDEN = 2;

    private static int _passed, _failed;
    private static readonly NativeResources Resources = new();

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _passed++; Console.WriteLine($"  PASS  {name}"); }
        else { _failed++; Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : " — " + detail)}"); }
    }

    // ---------- shell item 数组（真实 Explorer 对象，经 P/Invoke 构造） ----------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromShellItem(IntPtr psi, ref Guid riid, out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern int CoTaskMemFree(IntPtr pv);

    private static ComInterop.IShellItemArray MakeArray(string path)
    {
        var riidItem = new Guid(ComInterop.IID_IShellItem);
        var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref riidItem, out var ppsi);
        if (hr != 0) { if(ppsi!=IntPtr.Zero) Marshal.Release(ppsi); Marshal.ThrowExceptionForHR(hr); }
        try
        {
            var riidArr = new Guid(ComInterop.IID_IShellItemArray);
            hr = SHCreateShellItemArrayFromShellItem(ppsi, ref riidArr, out var ppsa);
            if (hr != 0) { if(ppsa!=IntPtr.Zero) Marshal.Release(ppsa); Marshal.ThrowExceptionForHR(hr); }
            try { return Resources.Rcw<ComInterop.IShellItemArray>(ppsa); } finally { if(ppsa!=IntPtr.Zero) Marshal.Release(ppsa); }
        }
        finally { Marshal.Release(ppsi); }
    }

    private static string? CoTaskMemToStringAndFree(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); } finally { CoTaskMemFree(p); }
    }

    private static string FindDist()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "dist");
            if (File.Exists(Path.Combine(candidate, "7zipContext.exe"))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到 dist（请先跑 scripts/install.ps1）");
    }

    private static bool SpinWait(Func<bool> cond, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(250);
        }
        return cond();
    }

    [STAThread]
    private static int Main()
    {
        using var resources = Resources;
        var dist = FindDist();
        Console.WriteLine($"dist: {dist}");
        var work = Path.Combine(Path.GetTempPath(), "7zipContext-verb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        // Invoke 将启动 7zipContext.exe（ZIPCONTEXT_EXE 供测试定位 dist 里的真实 CLI）
        var ctxCli = Path.Combine(dist, "7zipContext.exe");
        Environment.SetEnvironmentVariable("ZIPCONTEXT_EXE", ctxCli);
        Console.WriteLine($"7zipContext cli: {ctxCli}");

        // ---------- 造测试材料 ----------
        // 引擎由安装阶段解析到 %LOCALAPPDATA%，不再随仓库/发布目录携带
        var sevenZip = ZipContext.Core.EngineLocator.Require("7z.exe");
        // workingDir 定为 stage：输入用相对名，包内路径才干净（输出用绝对路径）
        void Run7z(string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = sevenZip,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit(15000);
        }

        var stage = Path.Combine(work, "stage");
        Directory.CreateDirectory(Path.Combine(stage, "docs"));
        File.WriteAllText(Path.Combine(stage, "a.txt"), "a");
        File.WriteAllText(Path.Combine(stage, "docs", "b.txt"), "b");

        var multiZip = Path.Combine(work, "multi.zip");
        Run7z(stage, "a", "-tzip", multiZip, "a.txt", "docs", "-y", "-bso0", "-bsp0");
        var txt = Path.Combine(work, "plain.txt");
        File.WriteAllText(txt, "not an archive");
        var subDir = Path.Combine(work, "folder1");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "f.txt"), "f");

        // ---------- 期望表 ----------
        var expectations = new (string Name, bool OnArchive, bool OnText, bool OnFolder, string? TitleContains)[]
        {
            ("SmartExtract",       true,  false, false, null),
            ("SmartExtractDelete", true,  false, false, null),
            ("ExtractToName",      true,  false, false, "multi"),
            ("ExtractToFolder",    true,  false, false, null),
            // Preview 已退役：预览改由 7zipContext.Classic 在经典菜单内联渲染
            ("AddZip",             true,  true,  true,  null),
            ("Add7z",              true,  true,  true,  null),
            ("QuickZip",           true,  true,  true,  null),
            ("OpenWithSevenZip",  true,  true,  true,  "7-Zip"),
        };

        Console.WriteLine("\n[1] 动词实例化（8 个 IExplorerCommand 类）");
        var commands = new Dictionary<string, ComInterop.IExplorerCommand>();
        foreach (var (clsid, type) in VerbClsids.Map)
        {
            var cmd = (ComInterop.IExplorerCommand)Activator.CreateInstance(type)!;
            var shortName = type.Name.EndsWith("Command") ? type.Name[..^"Command".Length] : type.Name;
            commands[shortName] = cmd;
            var hr = cmd.GetCanonicalName(out var canon);
            Check($"实例化 {shortName} + GetCanonicalName={canon}", hr == 0 && canon == new Guid(clsid));
        }

        Console.WriteLine("\n[2] 新菜单快速构建路径回归（空选中项 + fOkToBeSlow=false 不许隐藏）");
        foreach (var (name, _, _, _, _) in expectations)
        {
            if (!commands.TryGetValue(name, out var cmd)) continue;
            var hr = cmd.GetState(null, false, out var state);
            Check($"{name}: GetState(null, fast) = ENABLED",
                hr == 0 && state == ECS_ENABLED, $"hr=0x{hr:X8} state={state}");
        }

        Console.WriteLine("\n[3] 真实选中项的显隐规则 / 标题 / 图标");
        var arrZip = MakeArray(multiZip);
        var arrTxt = MakeArray(txt);
        var arrDir = MakeArray(subDir);
        foreach (var (name, onArchive, onText, onFolder, titleHas) in expectations)
        {
            if (!commands.TryGetValue(name, out var cmd)) continue;

            cmd.GetState(arrZip, true, out var s1);
            Check($"{name}: 压缩包 → {(onArchive ? "显示" : "隐藏")}",
                s1 == (onArchive ? ECS_ENABLED : ECS_HIDDEN), $"state={s1}");

            cmd.GetState(arrTxt, true, out var s2);
            Check($"{name}: 普通文件 → {(onText ? "显示" : "隐藏")}",
                s2 == (onText ? ECS_ENABLED : ECS_HIDDEN), $"state={s2}");

            cmd.GetState(arrDir, true, out var s3);
            Check($"{name}: 文件夹 → {(onFolder ? "显示" : "隐藏")}",
                s3 == (onFolder ? ECS_ENABLED : ECS_HIDDEN), $"state={s3}");

            cmd.GetTitle(arrZip, out var pTitle);
            var title = CoTaskMemToStringAndFree(pTitle);
            var titleOk = title is { Length: > 0 } && (titleHas is null || title.Contains(titleHas, StringComparison.OrdinalIgnoreCase));
            Check($"{name}: GetTitle{(titleHas is null ? "" : $" 含「{titleHas}」")} = \"{title}\"", titleOk);

            cmd.GetTitle(null, out var pTitleFast);
            var titleFast = CoTaskMemToStringAndFree(pTitleFast);
            Check($"{name}: GetTitle(空选中) 不炸且非空", !string.IsNullOrEmpty(titleFast), $"\"{titleFast}\"");

            cmd.GetIcon(arrZip, out var pIcon);
            var icon = CoTaskMemToStringAndFree(pIcon);
            Check($"{name}: GetIcon 非空（新菜单不容忍空指针）", !string.IsNullOrEmpty(icon), $"icon={icon}");
        }

        Console.WriteLine("\n[4] Invoke 端到端（唤起 7zipContext.exe 干活）");

        void InvokeVia(string verbName, ComInterop.IShellItemArray array)
        {
            if (commands.TryGetValue(verbName, out var cmd))
                cmd.Invoke(array, IntPtr.Zero);
        }

        // 4a. 智能解压（规则二：双根建同名文件夹）
        {
            var invZip = Path.Combine(work, "inv-multi.zip");
            Run7z(stage, "a", "-tzip", invZip, "a.txt", "docs", "-y", "-bso0", "-bsp0");
            InvokeVia("SmartExtract", MakeArray(invZip));
            var ok = SpinWait(() => Directory.Exists(Path.Combine(work, "inv-multi"))
                && File.Exists(Path.Combine(work, "inv-multi", "a.txt"))
                && File.Exists(Path.Combine(work, "inv-multi", "docs", "b.txt")));
            Check("SmartExtract.Invoke → 建同名文件夹并落入内容", ok);
        }

        // 4b. 智能解压并删除（成功后压缩包消失 → 进回收站）
        {
            var invZip = Path.Combine(work, "inv-del.zip");
            Run7z(stage, "a", "-tzip", invZip, "a.txt", "docs", "-y", "-bso0", "-bsp0");
            InvokeVia("SmartExtractDelete", MakeArray(invZip));
            var ok = SpinWait(() => !File.Exists(invZip) && Directory.Exists(Path.Combine(work, "inv-del")));
            Check("SmartExtractDelete.Invoke → 解压成功且压缩包已删", ok);
        }

        // 4c. 添加到 zip（压缩动词）
        {
            var srcDir = Path.Combine(work, "packme");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "x.txt"), "x");
            InvokeVia("AddZip", MakeArray(Path.Combine(srcDir, "x.txt")));
            var ok = SpinWait(() => File.Exists(Path.Combine(srcDir, "x.zip")));
            Check("AddZip.Invoke → 生成 x.zip", ok);
        }

        Console.WriteLine("\n[5] 分卷压缩包：.001 显示解压，.002 不显示");
        {
            var randomBytes = new byte[3 * 1024 * 1024];
            Random.Shared.NextBytes(randomBytes);
            var random = Path.Combine(work, "random.bin");
            File.WriteAllBytes(random, randomBytes);
            var splitFirst = Path.Combine(work, "split.7z.001");
            Run7z(work, "a", "-t7z", "-v1m", Path.Combine(work, "split.7z"), "random.bin",
                "-y", "-bso0", "-bsp0");
            var splitLater = Path.Combine(work, "split.7z.002");
            Check("分卷包已生成", File.Exists(splitFirst) && File.Exists(splitLater));

            var arrFirst = MakeArray(splitFirst);
            foreach (var name in new[] { "SmartExtract", "SmartExtractDelete", "ExtractToName", "ExtractToFolder" })
            {
                if (!commands.TryGetValue(name, out var cmd)) continue;
                cmd.GetState(arrFirst, true, out var state);
                Check($"{name}: 单选 .001 → 显示", state == ECS_ENABLED, $"state={state}");
            }

            if (commands.TryGetValue("SmartExtract", out var laterCmd))
            {
                laterCmd.GetState(MakeArray(splitLater), true, out var laterState);
                Check("SmartExtract: 单选 .002 → 隐藏", laterState == ECS_HIDDEN, $"state={laterState}");
            }

            if (commands.TryGetValue("ExtractToName", out var titleCmd))
            {
                titleCmd.GetTitle(arrFirst, out var pSplitTitle);
                var splitTitle = CoTaskMemToStringAndFree(pSplitTitle);
                Check("ExtractToName: 分卷标题去掉 .001/.7z", splitTitle is { Length: > 0 } &&
                    splitTitle.Contains("split", StringComparison.OrdinalIgnoreCase), $"\"{splitTitle}\"");
            }

            InvokeVia("SmartExtract", arrFirst);
            var ok = SpinWait(() => File.Exists(Path.Combine(work, "split", "random.bin")));
            Check("SmartExtract.Invoke → 分卷只解一次", ok &&
                File.Exists(splitFirst) && File.Exists(splitLater));
        }

        Console.WriteLine($"\n========== 结果: {_passed} 通过 / {_failed} 失败 ==========");

        foreach (var p in Process.GetProcessesByName("7zipContext.Shell"))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        try { Directory.Delete(work, true); } catch { }
        return _failed == 0 ? 0 : 1;
    }
}
