using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using ZipContext.Core;

namespace ZipContext.App;

/// <summary>
/// 安装阶段的 7-Zip 引擎获取器。**只在 CLI 里跑，菜单路径绝不碰它**。
/// 优先级：自身缓存 → PATH（含 scoop shim）→ 注册表 → 常见安装路径 → 联网下载。
/// 下载源固定为官方 GitHub Release；用 API 返回的 digest 做 SHA256 校验，
/// 再用 Windows 自带 tar.exe 直接解官方安装器（免安装、免管理员、免写 Program Files）。
/// </summary>
public static class EngineProvisioner
{
    private const string ReleaseApi = "https://api.github.com/repos/ip7z/7zip/releases/latest";
    private const int DownloadTimeoutSeconds = 20;

    /// <summary>给 install.ps1 读的一次性状态文件（WinExe 没有控制台输出，靠它回传结果）。</summary>
    public static string StatusPath => Path.Combine(EngineLocator.ConfigRoot, "last-ensure.json");

    public sealed record EngineInfo(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("installedAt")] string InstalledAt,
        [property: JsonPropertyName("origin")] string Origin);

    public sealed record EnsureResult(bool Ok, string Message, string? Version, string? Origin, bool HasWarnings = false)
    {
        public string ToJson() => JsonSerializer.Serialize(new
        {
            ok = Ok, message = Message, version = Version, origin = Origin, warnings = HasWarnings,
        });
    }

    private sealed record ReleaseInfo(string Tag, string Url, string? Digest, bool HasWarnings = false);

    // ---------------------------------------------------------------- 对外命令

    /// <summary>首次安装：确保引擎可用。本机已有就直接采纳，绝不联网。</summary>
    public static EnsureResult Ensure(string? explicitPath, string? explicitSource, bool forceDownload = false)
        => explicitSource is not null ? throw new ArgumentException("自定义下载源已停用，请使用官方源或 -EnginePath 本机离线引擎。") : WithEngineLock(() => EnsureCore(explicitPath, forceDownload));

    private static EnsureResult EnsureCore(string? explicitPath, bool forceDownload)
    {
        try
        {
            Directory.CreateDirectory(EngineLocator.ConfigRoot);

            // -EnginePath：用户手动指定离线目录，最高优先级，跳过一切探测与联网。
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                var full = Path.GetFullPath(explicitPath);
                if (!EngineLocator.IsUsable(full))
                    return Fail($"-EnginePath 指向的目录不完整：{full}（需要 7z.exe / 7zG.exe / 7zFM.exe / 7z.dll）");
                var manualVersion = ReadVersionFromExe(Path.Combine(full, "7z.exe")) ?? "unknown";
                var warning = Adopt(full, manualVersion, "local", "user-supplied", "");
                return Ok(WithWarning($"已采用手动指定的 7-Zip 引擎 {manualVersion}：{full}", warning), manualVersion, "manual", !string.IsNullOrWhiteSpace(warning));
            }

            if (!forceDownload)
            {
                // 已有指针且可用时保留原元数据（尤其是 downloaded 的来源与 SHA256），
                // 否则每次重装都会把下载记录抹成 "local"。
                var pointer = EngineLocator.ReadPointer();
                if (EngineLocator.IsUsable(pointer))
                {
                    var meta = ReadMetadata();
                    var same = meta is not null && string.Equals(
                        Path.GetFullPath(meta.Path), Path.GetFullPath(pointer!),
                        StringComparison.OrdinalIgnoreCase);
                    var v = same && meta!.Version.Length > 0
                        ? meta.Version
                        : ReadVersionFromExe(Path.Combine(pointer!, "7z.exe")) ?? "unknown";
                    var warning = !same ? Adopt(pointer!, v, "local", "preinstalled", "") : RefreshAssociations(pointer!);
                    return Ok(WithWarning($"7-Zip 引擎 {v} 已就绪，无需下载：{pointer}", warning), v,
                              same ? meta!.Origin : "local", !string.IsNullOrWhiteSpace(warning));
                }

                var local = ProbeLocal();
                if (local is not null)
                {
                    var version = ReadVersionFromExe(Path.Combine(local, "7z.exe")) ?? "unknown";
                    var warning = Adopt(local, version, "local", "preinstalled", "");
                    return Ok(WithWarning($"检测到本机已装 7-Zip {version}，直接复用，无需下载：{local}", warning), version, "local", !string.IsNullOrWhiteSpace(warning));
                }
            }

            var (succeeded, reason, info) = DownloadLatest(forceDownload);
            return succeeded
                ? Ok(reason, info?.Tag, "downloaded", info?.HasWarnings == true)
                : Fail(reason);
        }
        catch (Exception ex)
        {
            return Fail($"获取 7-Zip 引擎失败：{ex.Message}");
        }
    }

    /// <summary>只检查、不下载；每天最多联网一次（--force 可跳过节流）。</summary>
    public static EnsureResult CheckUpdate(bool force)
        => WithEngineLock(() => CheckUpdateCore(force));

    private static EnsureResult CheckUpdateCore(bool force)
    {
        var current = ReadVersion() ?? "unknown";
        var directory = EngineLocator.EngineDirectory();
        var identity = (directory is null ? "missing" : Path.GetFullPath(directory).ToUpperInvariant()) + "|" + current;
        return new UpdateCheckStore(EngineLocator.ConfigRoot).Check(identity, current, force, () =>
        {
            var rel = FetchLatest(out var apiError);
            if (rel is null) return new EnsureResult(true, $"联网检查失败：{apiError}。不影响现有引擎。", current, "offline");
            var cmp = CompareVersion(rel.Tag, current);
            var message = cmp > 0
                ? $"发现新版本：当前 {current} → 最新 {rel.Tag}。运行 7zipContext.exe --update-engine 可升级。"
                : $"当前 {current} 已是最新（官方 {rel.Tag}）。";
            return new EnsureResult(true, message, current, cmp > 0 ? "update-available" : "up-to-date");
        });
    }
    /// <summary>显式升级：下载最新版 → 校验 → 解包 → 原子切换指针；旧版保留可回滚。</summary>
    public static EnsureResult Update(string? explicitSource)
        => explicitSource is not null ? throw new ArgumentException("自定义下载源已停用，请使用官方源或 -EnginePath 本机离线引擎。") : WithEngineLock(UpdateCore);

    private static EnsureResult UpdateCore()
    {
        try
        {
            var before = ReadVersion() ?? "unknown";
            var (succeeded, reason, info) = DownloadLatest(force: true);
            if (!succeeded) return Fail(reason);   // 保留底层具体原因，别再用笼统提示覆盖
            return Ok($"已升级 7-Zip：{before} → {info?.Tag}（旧版本目录保留，可手动回滚 engine.path）。\n{reason}",
                      info?.Tag, "updated", info?.HasWarnings == true);
        }
        catch (Exception ex)
        {
            return Fail($"升级失败：{ex.Message}");
        }
    }

    /// <summary>当前引擎信息（排障用）。</summary>
    public static string Describe()
    {
        var dir = EngineLocator.ReadPointer();
        if (!EngineLocator.IsUsable(dir)) dir = EngineLocator.EngineDirectory();
        if (dir is null)
            return "未找到可用的 7-Zip 引擎。\n请运行 install.ps1，或使用 -EnginePath 指向本机已有的 7-Zip 目录。";
        var meta = StateStore.ReadMetadata(dir);
        var sb = new StringBuilder();
        sb.AppendLine($"引擎目录：{dir}");
        sb.AppendLine($"版本：{meta?.Version ?? ReadVersionFromExe(Path.Combine(dir, "7z.exe")) ?? "unknown"}");
        sb.AppendLine($"来源：{meta?.Origin ?? "本机已有"}");
        if (!string.IsNullOrEmpty(meta?.Source)) sb.AppendLine($"下载地址：{meta.Source}");
        if (!string.IsNullOrEmpty(meta?.Sha256)) sb.AppendLine($"SHA256：{meta.Sha256}");
        if (!string.IsNullOrEmpty(meta?.InstalledAt)) sb.AppendLine($"记录时间：{meta.InstalledAt}");
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- 本地探测

    /// <summary>按优先级探测本机已有引擎，返回四件套齐全的目录。</summary>
    private static string? ProbeLocal()
    {
        // 1) 自己的缓存
        var pointer = EngineLocator.ReadPointer();
        if (EngineLocator.IsUsable(pointer)) return pointer;

        // 2) PATH（含 scoop shim 解引用）
        var onPath = ProbePath();
        if (onPath is not null) return onPath;

        // 3) 注册表安装位置
        var fromReg = ProbeRegistry();
        if (fromReg is not null) return fromReg;

        // 4) 常见安装路径
        return ProbeCommonLocations();
    }

    private static string? ProbePath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var raw in path.Split(Path.PathSeparator))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0 || !File.Exists(Path.Combine(dir, "7z.exe"))) continue;
            var real = DereferenceShim(dir) ?? dir;
            if (EngineLocator.IsUsable(real)) return real;
        }
        return null;
    }

    private static string? DereferenceShim(string dir)
    {
        try
        {
            var shim = Path.Combine(dir, "7z.shim");
            if (!File.Exists(shim)) return null;
            foreach (var line in File.ReadAllLines(shim))
            {
                var i = line.IndexOf('=');
                if (i < 0 || !line[..i].Trim().Equals("path", StringComparison.OrdinalIgnoreCase)) continue;
                var target = line[(i + 1)..].Trim().Trim('"');
                if (target.Length == 0) continue;
                return Path.GetDirectoryName(target);
            }
        }
        catch { }
        return null;
    }

    private static string? ProbeRegistry()
    {
        foreach (var view in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var keyPath in new[] { @"SOFTWARE\7-Zip", @"SOFTWARE\WOW6432Node\7-Zip" })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(view, RegistryView.Registry64);
                using var k = baseKey.OpenSubKey(keyPath);
                if (k is null) continue;
                foreach (var name in new[] { "Path64", "Path", "InstallPath" })
                {
                    var v = k.GetValue(name) as string;
                    if (!string.IsNullOrWhiteSpace(v) && EngineLocator.IsUsable(v)) return v;
                }
            }
            catch { }
        }
        return null;
    }

    private static string? ProbeCommonLocations()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramW6432")
                 ?? Environment.GetEnvironmentVariable("ProgramFiles");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>();
        if (!string.IsNullOrEmpty(pf))
        {
            list.Add(Path.Combine(pf, "7-Zip"));
            list.Add(Path.Combine(pf, "(x86)", "7-Zip"));
        }
        foreach (var root in new[] { local, user })
        {
            if (string.IsNullOrEmpty(root)) continue;
            list.Add(Path.Combine(root, "Programs", "7-Zip"));
            list.Add(Path.Combine(root, "scoop", "apps", "7zip", "current"));
        }
        foreach (var c in list.Distinct(StringComparer.OrdinalIgnoreCase))
            if (EngineLocator.IsUsable(c)) return c;
        return null;
    }

    // ---------------------------------------------------------------- 下载 / 校验 / 解包

    /// <summary>下载官方最新版并落地；返回 (是否成功, 可读原因, 版本信息)。</summary>
    private static (bool Ok, string Reason, ReleaseInfo? Info) DownloadLatest(
        bool force)
    {
        ReleaseInfo? info = null;
        var temp = Path.Combine(Path.GetTempPath(), "7zipContext-engine-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            string url;
            string? digest;
            string tag;

            var rel = FetchLatest(out var apiError);
            if (rel is null)
                return (false, $"无法取得官方发布信息（{apiError}）。请检查网络，或用 install.ps1 -EnginePath <本机 7-Zip 目录> 离线安装。", null);
            url = rel.Url;
            digest = rel.Digest;
            tag = rel.Tag;
            info = rel;
            if (string.IsNullOrWhiteSpace(digest)) return (false, "官方资产缺少 SHA256 digest，已拒绝下载。", info);
            if (!force && CompareVersion(rel.Tag, ReadVersion() ?? "0") <= 0
                && EngineLocator.IsUsable(EngineLocator.ReadPointer()))
                return (true, $"已是最新版 {rel.Tag}，无需下载。", rel);
            var installer = Path.Combine(temp, "installer.exe");
            if (!Download(url, installer, out var downloadError))
                return (false,
                    $"下载失败：{downloadError}\n地址：{url}\n" +
                    "可用 -EnginePath 使用本机已有的 7-Zip。", info);

            var actualHash = Sha256(installer);
            if (digest is not null)
            {
                var expected = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? digest[7..] : digest;
                if (!string.Equals(actualHash, expected, StringComparison.OrdinalIgnoreCase))
                    return (false, $"SHA256 校验不通过，已丢弃下载文件。\n期望 {expected}\n实际 {actualHash}", info);
            }
            var extract = Path.Combine(temp, "extract");
            Directory.CreateDirectory(extract);
            var tar = Path.Combine(
                Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "System32", "tar.exe");
            if (!File.Exists(tar))
                return (false, "系统缺少 tar.exe（Windows 10 1803+ 自带），无法解包官方安装器。", info);
            if (!RunProcess(tar, new[] { "-xf", installer, "-C", extract }, 60_000, out var tarError))
                return (false, $"解包失败：{tarError}", info);
            if (!File.Exists(Path.Combine(extract, "7z.exe")))
                return (false, "安装包内未找到 7z.exe，可能不是官方 x64 安装器。", info);

            // 版本来自网络或 URL，不能作为未经检查的目录路径使用。
            if (tag is "." or ".." || tag.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return (false, "发布版本号不能用作目录名，已放弃本次安装。", info);
            if (!EngineLocator.IsUsable(extract) || !File.Exists(Path.Combine(extract, "License.txt")))
                return (false, "安装包缺少引擎文件或 License.txt，已放弃本次安装。", info);

            // 始终写新目录，不能先删正在生效的同版本引擎；中途失败仍保留旧指针。
            var dest = Path.Combine(EngineLocator.EngineRoot, tag);
            if (Directory.Exists(dest))
                dest = Path.Combine(EngineLocator.EngineRoot, tag + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dest);
            foreach (var f in EngineLocator.PayloadFiles)
                File.Copy(Path.Combine(extract, f), Path.Combine(dest, f), true);
            // 中文界面语言文件（存在才拷，不是所有版本都带）
            var langSrc = Path.Combine(extract, "Lang");
            if (Directory.Exists(langSrc))
            {
                var langDest = Path.Combine(dest, "Lang");
                Directory.CreateDirectory(langDest);
                foreach (var f in Directory.EnumerateFiles(langSrc, "zh-*.txt"))
                    File.Copy(f, Path.Combine(langDest, Path.GetFileName(f)), true);
            }
            // LGPL：随引擎目录保留官方许可文本
            var lic = Path.Combine(extract, "License.txt");
            if (File.Exists(lic)) File.Copy(lic, Path.Combine(dest, "License.txt"), true);

            if (!EngineLocator.IsUsable(dest))
                return (false, "解包后四件套不齐全，已放弃本次安装。", info);

            var warning = Adopt(dest, tag, "downloaded", url, actualHash);
            info ??= new ReleaseInfo(tag, url, digest);
            info = info with { HasWarnings = !string.IsNullOrWhiteSpace(warning) };
            return (true, WithWarning($"已安装 7-Zip {tag}：{dest}", warning), info);
        }
        catch (Exception ex)
        {
            return (false, $"安装引擎时出错：{ex.Message}", info);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    /// <summary>
    /// 构造 HttpClient：优先用会话/系统环境变量里的代理（HTTPS_PROXY 等），
    /// 否则用 .NET 默认代理（WinHTTP/系统代理）。国内网络常靠这一步才能连上 GitHub。
    /// </summary>
    private static HttpClient NewHttpClient()
    {
        var handler = new HttpClientHandler { UseProxy = true, AllowAutoRedirect = true };
        var proxyVar = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                       ?? Environment.GetEnvironmentVariable("https_proxy")
                       ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
                       ?? Environment.GetEnvironmentVariable("http_proxy");
        if (!string.IsNullOrWhiteSpace(proxyVar) && Uri.TryCreate(proxyVar, UriKind.Absolute, out var proxyUri))
            handler.Proxy = new System.Net.WebProxy(proxyUri, BypassOnLocal: true);

        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(DownloadTimeoutSeconds) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("7zipContext-EngineProvisioner/1.0");
        return http;
    }

    private static bool Download(string url, string target, out string error)
    {
        error = "";
        for (var attempt = 1; attempt <= 2; attempt++)  // 超时 20s + 重试 1 次
        {
            try
            {
                using var http = NewHttpClient();
                var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                if (bytes.Length < 100_000)
                {
                    error = $"响应过小（{bytes.Length} 字节），可能不是安装包";
                    continue;
                }
                File.WriteAllBytes(target, bytes);
                return true;
            }
            catch (Exception ex)
            {
                error = ex is TaskCanceledException ? $"超时（>{DownloadTimeoutSeconds}s）" : ex.Message;
            }
        }
        return false;
    }

    private static ReleaseInfo? FetchLatest(out string error)
    {
        error = "未知原因";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var http = NewHttpClient();
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                var json = http.GetStringAsync(ReleaseApi).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var tag = root.GetProperty("tag_name").GetString() ?? "";
                if (tag.Length == 0) { error = "API 未返回 tag_name"; return null; }

                foreach (var asset in root.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    // 只认官方 x64 安装器：7z2606-x64.exe；排除 arm64 / msi / extra / src
                    if (!name.EndsWith("-x64.exe", StringComparison.OrdinalIgnoreCase)
                        || !name.StartsWith("7z", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("arm", StringComparison.OrdinalIgnoreCase)) continue;
                    var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                    if (url.Length == 0) continue;
                    string? digest = null;
                    if (asset.TryGetProperty("digest", out var d)) digest = d.GetString();
                    if (digest is null || digest.Length == 0) continue; // 无校验值不下载（供应链安全）
                    return new ReleaseInfo(tag, url, digest);
                }
                error = "API 里没有带 digest 的 x64 安装器资产";
                return null;
            }
            catch (Exception ex)
            {
                error = ex is TaskCanceledException ? $"超时（>{DownloadTimeoutSeconds}s）" : ex.Message;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- 元数据 / 节流

    private static EngineStateStore StateStore => new(EngineLocator.ConfigRoot);

    private static string? Adopt(string dir, string version, string origin, string source, string hash)
    {
        var meta = new EngineInfo(version, dir, source, hash,
            DateTimeOffset.Now.ToString("O"), origin);
        var warning = StateStore.Commit(meta);
        EngineLocator.InvalidateCache();
        var registrationWarning = RefreshAssociations(dir);
        return string.Join("\n", new[] { warning, registrationWarning }.Where(s => !string.IsNullOrEmpty(s)));
    }

    private static EngineInfo? ReadMetadata()
    {
        return StateStore.ReadMetadata();
    }

    private static string? ReadVersion()
    {
        var directory = EngineLocator.ReadPointer();
        if (!EngineLocator.IsUsable(directory)) directory = EngineLocator.EngineDirectory();
        var meta = StateStore.ReadMetadata(directory);
        if (meta is not null && meta.Version.Length > 0) return meta.Version;
        return directory is null ? null : ReadVersionFromExe(Path.Combine(directory, "7z.exe"));
    }

    /// <summary>从 `7z.exe` 的 stdout 首行解析版本号（只用于本机已有引擎的显示）。</summary>
    private static string? ReadVersionFromExe(string exe)
    {
        try
        {
            if (RunProcess(exe, Array.Empty<string>(), 3000, out var output) && output is not null)
            {
                foreach (var line in output.Split('\n'))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"7-Zip\s+(\d+\.\d+)");
                    if (m.Success) return m.Groups[1].Value;
                }
            }
        }
        catch { }
        return null;
    }

    // ---------------------------------------------------------------- 工具

    private static EnsureResult Ok(string msg, string? v, string origin, bool hasWarnings = false) => new(true, msg, v, origin, hasWarnings);
    private static EnsureResult Fail(string msg) => new(false, msg, ReadVersion(), "failed");

    private static string WithWarning(string message, string? warning) =>
        string.IsNullOrWhiteSpace(warning) ? message : message + "\n" + warning;

    private static string? RefreshAssociations(string directory)
    {
        try { ShellIntegration.ShellIntegration.RefreshOpenWithForEngine(directory); return null; }
        catch (Exception ex) { return "引擎可用，但可选打开方式刷新失败，请稍后重新注册：" + ex.Message; }
    }

    private static EnsureResult WithEngineLock(Func<EnsureResult> work)
    {
        try
        {
            using var deployment = ZipContext.Deployment.DeploymentLease.Acquire(EngineLocator.ConfigRoot,
                Environment.GetEnvironmentVariable(ZipContext.Deployment.DeploymentLease.EnvironmentName));
            using var gate = EngineOperationLock.Acquire(EngineLocator.ConfigRoot);
            EngineLocator.InvalidateCache();
            return work();
        }
        catch (EngineBusyException ex) { return new(false, ex.Message, null, "busy"); }
        catch (ZipContext.Deployment.DeploymentBusyException ex) { return new(false, ex.Message, null, "busy"); }
        catch (Exception ex) { return Fail("引擎操作失败：" + ex.Message); }
    }

    public static bool WriteStatus(EnsureResult result, string? path = null)
    {
        try
        {
            AtomicTextFile.Write(path ?? StatusPath, result.ToJson());
            return true;
        }
        catch { return false; }
    }

    private static string Sha256(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static bool RunProcess(string exe, IEnumerable<string> args, int timeoutMs, out string? output)
    {
        output = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return false; }
            output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>按数值逐段比较版本（26.10 > 26.06，不能按字符串比）。</summary>
    public static int CompareVersion(string a, string b)
    {
        var pa = ParseVersion(a);
        var pb = ParseVersion(b);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static int[] ParseVersion(string v)
    {
        var parts = (v ?? "").Trim().Split('.', '-', '_');
        var nums = new List<int>();
        foreach (var p in parts)
            if (int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n)) nums.Add(n);
            else break;
        return nums.Count == 0 ? new[] { 0 } : nums.ToArray();
    }
}
