using System.Text;

namespace ZipContext.Core;

/// <summary>
/// 只读的 7-Zip 引擎定位器。**零网络、零子进程、零注册表写、微秒级**。
/// 经典菜单 handler 跑在 explorer.exe 进程内（列目录 250ms 硬超时），
/// 所以这里绝不能联网、不能起进程、不能做慢 IO：只读一个本地指针文件 + 几次 File.Exists。
/// 引擎的探测/下载/校验/解包全部交给安装阶段的 EngineProvisioner。
/// </summary>
public static class EngineLocator
{
    /// <summary>用户级配置根：%LOCALAPPDATA%\7zipContext\。</summary>
    public static string ConfigRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "7zipContext");

    /// <summary>引擎版本目录的根：...\engine\{版本}\。</summary>
    public static string EngineRoot => Path.Combine(ConfigRoot, "engine");

    /// <summary>纯文本指针文件，内容只有一行：当前生效的引擎目录绝对路径。</summary>
    public static string PointerPath => Path.Combine(ConfigRoot, "engine.path");

    /// <summary>引擎元数据（版本/来源/校验/安装时间），供更新检查与排障读。</summary>
    public static string MetadataPath => Path.Combine(ConfigRoot, "engine.json");

    /// <summary>上次联网检查的 UTC 时间戳（ISO 8601），用于「每天最多一次」节流。</summary>
    public static string LastCheckPath => Path.Combine(ConfigRoot, "last-update-check");

    /// <summary>上次检查结果缓存，离线时也能回放。</summary>
    public static string LastResultPath => Path.Combine(ConfigRoot, "last-update-result.json");

    /// <summary>四件套齐全才算「可用引擎」；缺 7zG.exe 会丢进度窗与密码框。</summary>
    public static readonly string[] RequiredFiles = { "7z.exe", "7zG.exe", "7zFM.exe", "7z.dll" };

    /// <summary>解包官方安装器时必须保留的四件套（Lang\ 与官方 License.txt 另行处理）。</summary>
    public static readonly string[] PayloadFiles = RequiredFiles;

    private static readonly object Sync = new();
    private static string? _cachedDir;
    private static DateTime _cachedAt;

    /// <summary>返回可用的引擎目录；找不到返回 null。</summary>
    public static string? EngineDirectory()
    {
        // 缓存 2 秒即可覆盖一次菜单构建里的多次调用，又不会让升级后迟迟不生效。
        lock (Sync)
        {
            if (_cachedDir is not null && (DateTime.UtcNow - _cachedAt).TotalSeconds < 2
                && IsUsable(_cachedDir))
                return _cachedDir;
        }

        string? found = null;
        try
        {
            var pointer = ReadPointer();
            if (pointer is not null && IsUsable(pointer)) found = pointer;
        }
        catch { /* 指针损坏/无权限：继续走兜底，绝不向 explorer 抛异常 */ }

        if (found is null) found = FindOnPath();
        if (found is null) found = FindInCommonLocations();

        lock (Sync)
        {
            _cachedDir = found;
            _cachedAt = DateTime.UtcNow;
        }
        return found;
    }

    /// <summary>取某个引擎可执行文件的完整路径；找不到返回 null。</summary>
    public static string? Resolve(string fileName)
    {
        var dir = EngineDirectory();
        if (dir is null) return null;
        var p = Path.Combine(dir, fileName);
        return File.Exists(p) ? p : null;
    }

    /// <summary>要求引擎存在，否则抛带操作指引的异常（只给 CLI/测试用，不进菜单路径）。</summary>
    public static string Require(string fileName = "7z.exe") =>
        Resolve(fileName) ?? throw new FileNotFoundException(
            "未找到 7-Zip 引擎。请运行 install.ps1 完成首次安装；" +
            "离线环境可用 -EnginePath 指向本机已有的 7-Zip 目录。");

    /// <summary>引擎是否可用（四件套齐全）。</summary>
    public static bool IsUsable(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        foreach (var f in RequiredFiles)
            if (!File.Exists(Path.Combine(dir, f))) return false;
        return true;
    }

    /// <summary>读取指针文件；损坏或不存在返回 null。</summary>
    public static string? ReadPointer()
    {
        if (!File.Exists(PointerPath)) return null;
        var text = AtomicTextFile.Read(PointerPath).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>原子写入指针（先写临时文件再替换，避免菜单读到半截内容）。</summary>
    public static void WritePointer(string dir)
    {
        AtomicTextFile.Write(PointerPath, dir);
        InvalidateCache();
    }

    public static void InvalidateCache()
    {
        lock (Sync) { _cachedDir = null; }
    }

    private static string? FindOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var raw in path.Split(Path.PathSeparator))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0) continue;
            var exe = Path.Combine(dir, "7z.exe");
            if (!File.Exists(exe)) continue;

            // PATH 命中若是 shim（scoop 等只放 7z.exe），要顺着 shim 找到真实目录，
            // 否则四件套校验会失败、白白触发一次下载。
            var actual = DereferenceShim(dir);
            if (actual is not null && IsUsable(actual)) return actual;
            if (IsUsable(dir)) return dir;
        }
        return null;
    }

    /// <summary>scoop shim 是同名 .shim 文本，里面写着真实 exe 路径。</summary>
    private static string? DereferenceShim(string dir)
    {
        try
        {
            var shim = Path.Combine(dir, "7z.shim");
            if (!File.Exists(shim)) return null;
            foreach (var line in File.ReadAllLines(shim))
            {
                var i = line.IndexOf('=');
                if (i < 0) continue;
                var key = line[..i].Trim();
                if (!key.Equals("path", StringComparison.OrdinalIgnoreCase)) continue;
                var target = line[(i + 1)..].Trim().Trim('"');
                if (target.Length == 0) continue;
                var real = Path.GetDirectoryName(target);
                return real;
            }
        }
        catch { }
        return null;
    }

    private static readonly string[] CommonRelative =
    {
        @"7-Zip",
        @"Programs\7-Zip",
        @"scoop\apps\7zip\current",
    };

    private static string? FindInCommonLocations()
    {
        var candidates = new List<string>();
        var pf = Environment.GetEnvironmentVariable("ProgramW6432")
                 ?? Environment.GetEnvironmentVariable("ProgramFiles");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "7-Zip"));
        if (!string.IsNullOrEmpty(local))
            foreach (var r in CommonRelative) candidates.Add(Path.Combine(local, r));
        if (!string.IsNullOrEmpty(user))
            foreach (var r in CommonRelative) candidates.Add(Path.Combine(user, r));

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (IsUsable(c)) return c;
        return null;
    }
}
