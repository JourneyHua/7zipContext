using System.Diagnostics;
using System.Text;

namespace ZipContext.Core;

/// <summary>7z 后端封装。所有子进程调用集中在此。</summary>
public sealed class SevenZipService
{
    private readonly string _sevenZipPath;

    /// <summary>压缩/解压走 7zG.exe：它自带进度窗和密码框，我们不再自己画 UI。
    /// 找不到 7zG.exe 时自动退回 7z.exe（控制台通道），保证功能不因缺文件而废掉。</summary>
    private readonly string? _sevenZipGuiPath;

    public SevenZipService(string? sevenZipPath = null)
    {
        _sevenZipPath = sevenZipPath ?? ResolveDefaultPath();
        // 7zG.exe 与 7z.exe 同目录：有就用 GUI（进度窗+密码框），没有就退回控制台
        var dir = Path.GetDirectoryName(_sevenZipPath);
        if (!string.IsNullOrEmpty(dir))
        {
            var gui = Path.Combine(dir, "7zG.exe");
            if (File.Exists(gui)) _sevenZipGuiPath = gui;
        }
    }

    /// <summary>true = 写操作（解压/压缩）走 7zG.exe。默认开；测试可关掉以便断言输出。</summary>
    public bool UseGui { get; set; } = true;

    private bool GuiAvailable => UseGui && _sevenZipGuiPath is not null;

    /// <summary>
    /// 引擎由安装阶段写入 %LOCALAPPDATA%\7zipContext\engine.path 指针，
    /// 这里只读指针 + 本机探测，**绝不联网**（菜单路径可能在 explorer 进程内）。
    /// 显式传入 sevenZipPath 时仍优先（测试用）。
    /// </summary>
    private static string ResolveDefaultPath() => EngineLocator.Require("7z.exe");

    public sealed record RunResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Success => ExitCode == 0;
    }

    /// <summary>异步运行 7z。password 为 null 时不传 -p（交互式会挂起，所以无密码也传 -p 空串由调用方决定）。</summary>
    private async Task<RunResult> RunAsync(IEnumerable<string> args, string? password,
        CancellationToken ct = default, string? workingDirectory = null, int maxOutputChars = int.MaxValue)
    {
        var allArgs = new List<string>(args);
        // 只在真有密码时传 -p。裸 -p 会让 7z 交互式提示并挂起；
        // 不传 -p 时若包已加密，stdin 已关死 → 7z 快速报错 → 被 LooksLikePasswordProblem 接住。
        if (password is not null)
            allArgs.Add($"-p{password}");
        allArgs.Add("-y"); // 全部确认
        allArgs.Add("-bso0"); // 抑制标准输出里的进度（保留列表输出）
        allArgs.Add("-bsp0");

        var psi = new ProcessStartInfo
        {
            FileName = _sevenZipPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        foreach (var a in allArgs) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        // 无论如何给 stdin 一个 EOF，防止任何子命令等待输入。
        proc.StandardInput.Close();

        // 同时排空两条管道，避免 stderr 写满后子进程与 stdout 互相等待。
        void Kill() { try { proc.Kill(entireProcessTree: true); } catch { } }
        var outputExceeded = 0;
        async Task<string> ReadOutput(StreamReader reader)
        {
            try
            {
                if (maxOutputChars == int.MaxValue) return await reader.ReadToEndAsync(ct);
                var text = new StringBuilder();
                var buffer = new char[4096];
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
                {
                    if (text.Length > maxOutputChars - read)
                    {
                        Interlocked.Exchange(ref outputExceeded, 1);
                        throw new SevenZipLimitException();
                    }
                    text.Append(buffer, 0, read);
                }
                return text.ToString();
            }
            catch { Kill(); throw; } // 输出超限时立即终止，不能等仍在写管道的进程。
        }
        var stdout = ReadOutput(proc.StandardOutput);
        var stderr = ReadOutput(proc.StandardError);
        try
        {
            await Task.WhenAll(stdout, stderr, proc.WaitForExitAsync(ct));
            return new RunResult(proc.ExitCode, await stdout, await stderr);
        }
        catch when (Volatile.Read(ref outputExceeded) != 0)
        {
            throw new SevenZipLimitException();
        }
        catch
        {
            Kill();
            throw;
        }
    }

    /// <summary>GUI 通道：7zG.exe 自带进度窗与密码框，不再捕获输出，只看退出码。</summary>
    private async Task<RunResult> RunGuiAsync(List<string> args, string? password,
        CancellationToken ct, string? workingDirectory)
    {
        var allArgs = new List<string>(args);
        if (password is not null) allArgs.Add($"-p{password}");
        allArgs.Add("-y");

        var psi = new ProcessStartInfo
        {
            FileName = _sevenZipGuiPath!,
            UseShellExecute = false,
            CreateNoWindow = false, // 它要弹自己的进度窗，绝不能隐藏
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        foreach (var a in allArgs) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        try { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new RunResult(proc.ExitCode, string.Empty, DescribeExit(proc.ExitCode));
    }

    private static string DescribeExit(int code) => code switch
    {
        0 => "成功",
        1 => "已完成，但部分文件有警告",
        2 => "致命错误（压缩包损坏、密码错误或写入失败）",
        7 => "命令行参数错误",
        8 => "内存不足",
        255 => "用户已取消",
        _ => $"7-Zip 退出码 {code}",
    };

    /// <summary>写操作（解压/压缩）统一入口：有 7zG.exe 就走 GUI，否则退回控制台。</summary>
    private Task<RunResult> RunWriteAsync(List<string> args, string? password,
        CancellationToken ct, string? workingDirectory = null)
        => GuiAvailable
            ? RunGuiAsync(args, password, ct, workingDirectory)
            : RunCheckedAsync(args, password, ct, workingDirectory);

    /// <summary>列出压缩包内容。7z l -slt 输出键值对块。</summary>
    public async Task<IReadOnlyList<ArchiveEntry>> ListAsync(string archivePath,
        string? password = null, CancellationToken ct = default,
        int maxEntries = int.MaxValue, int maxOutputChars = int.MaxValue)
    {
        var res = await RunAsync(new[] { "l", "-slt", "-sccUTF-8", archivePath }, password, ct,
            maxOutputChars: maxOutputChars);
        if (!res.Success && LooksLikePasswordProblem(res))
            throw new PasswordRequiredException(archivePath);
        if (!res.Success)
            throw new SevenZipException($"读取压缩包失败（退出码 {res.ExitCode}）：{FirstErrorLine(res)}", exitCode: res.ExitCode);

        return ParseListing(res.StdOut, maxEntries, ct);
    }

    /// <summary>识别「缺密码/密码错」类失败。只认 password 措辞与加密头打不开，
    /// 不能匹配裸 "encrypted" —— 列表输出本身就有 Encrypted = - 字段。</summary>
    private static bool LooksLikePasswordProblem(RunResult res)
    {
        var text = res.StdOut + "\n" + res.StdErr;
        if (res.ExitCode == 255)
            return text.Contains("Enter password", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("canceled", StringComparison.OrdinalIgnoreCase);
        return text.Contains("password", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Can not open encrypted archive", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstErrorLine(RunResult res)
    {
        var text = string.IsNullOrWhiteSpace(res.StdErr) ? res.StdOut : res.StdErr;
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "未知错误";
    }

    internal static List<ArchiveEntry> ParseListing(string stdout, int maxEntries = int.MaxValue,
        CancellationToken ct = default)
    {
        var entries = new List<ArchiveEntry>();
        string? path = null;
        bool isFolder = false, encrypted = false;
        long size = 0, packed = 0;
        DateTime? modified = null;

        void Flush()
        {
            if (path is null) return;
            if (entries.Count >= maxEntries) throw new SevenZipLimitException();
            // 跳过块首的 "Path = <archive 本身>"（l -slt 第一块是归档信息，其后有 ---------- 分隔）
            entries.Add(new ArchiveEntry(path, isFolder, size, packed, modified, encrypted));
            path = null; isFolder = false; encrypted = false; size = 0; packed = 0; modified = null;
        }

        bool inEntries = false;
        foreach (var raw in stdout.Split('\n'))
        {
            ct.ThrowIfCancellationRequested();
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("----------")) { inEntries = true; continue; }
            if (!inEntries) continue;

            if (line.Length == 0) { Flush(); continue; }

            var eq = line.IndexOf(" = ", StringComparison.Ordinal);
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 3)..].Trim();

            switch (key)
            {
                case "Path": path = val.Replace('\\', '/'); break;
                case "Folder": isFolder = val is "+" or "yes"; break;
                case "Attributes": if (val.StartsWith('D')) isFolder = true; break;
                case "Size": long.TryParse(val, out size); break;
                case "Packed Size": long.TryParse(val, out packed); break;
                case "Encrypted": encrypted = val is "+" or "yes"; break;
                case "Modified":
                    if (DateTime.TryParse(val, out var dt)) modified = dt;
                    break;
            }
        }
        Flush();

        // 7z 会把纯文件夹条目列出来（Folder = +）；未显式列出的中间目录由树构造补出。
        return entries;
    }

    /// <summary>读取引擎实际报告的分卷数量；未报告或报告不一致时不推测。</summary>
    public async Task<int?> ReadVolumeCountAsync(string archivePath, string? password = null,
        CancellationToken ct = default)
    {
        var result = await RunAsync(new[] { "l", "-slt", "-sccUTF-8", archivePath }, password, ct);
        if (!result.Success) return null;
        var counts = new HashSet<int>();
        foreach (var raw in result.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            // 只看包头；条目名称也可能包含 "Volumes = ..."。
            if (line == "----------") break;
            if (!line.StartsWith("Volumes = ", StringComparison.Ordinal)) continue;
            if (!int.TryParse(line[10..], out var count) || count <= 0) return null;
            counts.Add(count);
        }
        return counts.Count == 1 ? counts.Single() : null;
    }

    /// <summary>解压到指定目录。返回是否成功。</summary>
    public async Task<RunResult> ExtractAsync(string archivePath, string destDir,
        string? password = null, IReadOnlyCollection<string>? onlyPaths = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var args = new List<string> { "x", archivePath, $"-o{destDir}" };
        if (onlyPaths is { Count: > 0 })
        {
            var listFile = Path.GetTempFileName();
            await File.WriteAllLinesAsync(listFile, onlyPaths, ct);
            args.Add($"@{listFile}");
            try { return await RunWriteAsync(args, password, ct); }
            finally { File.Delete(listFile); }
        }
        return await RunWriteAsync(args, password, ct);
    }

    /// <summary>运行并把「密码类失败」转换为 PasswordRequiredException。</summary>
    private async Task<RunResult> RunCheckedAsync(List<string> args, string? password,
        CancellationToken ct, string? workingDirectory = null)
    {
        var res = await RunAsync(args, password, ct, workingDirectory);
        if (!res.Success && LooksLikePasswordProblem(res))
            throw new PasswordRequiredException(args.FirstOrDefault(a => !a.StartsWith('-')) ?? "?");
        return res;
    }

    /// <summary>压缩。format: zip / 7z / tar / tar.gz 等 7z -t 参数。
    /// workingDirectory 非空时在该目录下压缩（输入参数用相对名，包内路径才干净）。</summary>
    public async Task<RunResult> CompressAsync(string format, string outputArchive,
        IEnumerable<string> inputPaths, string? password = null,
        int level = 5, string? workingDirectory = null, CancellationToken ct = default)
    {
        var args = new List<string> { "a", $"-t{format}", $"-mx={level}", outputArchive };
        args.AddRange(inputPaths);

        // 加密时同时加密文件名（7z）；zip 只能加密内容。
        if (!string.IsNullOrEmpty(password) && format.StartsWith("7z"))
            args.Add("-mhe=on");

        return await RunWriteAsync(args, password, ct, workingDirectory);
    }

    /// <summary>完整性测试。</summary>
    public Task<RunResult> TestAsync(string archivePath, string? password = null,
        CancellationToken ct = default)
        => RunCheckedAsync(new List<string> { "t", archivePath }, password, ct);

    /// <summary>从压缩包删除条目。</summary>
    public Task<RunResult> DeleteEntriesAsync(string archivePath, IEnumerable<string> paths,
        CancellationToken ct = default)
    {
        var args = new List<string> { "d", archivePath };
        args.AddRange(paths);
        return RunAsync(args, null, ct);
    }

    /// <summary>把文件/文件夹添加进已有压缩包。</summary>
    public Task<RunResult> AddToArchiveAsync(string archivePath, IEnumerable<string> inputPaths,
        CancellationToken ct = default)
    {
        var args = new List<string> { "a", archivePath };
        args.AddRange(inputPaths);
        return RunAsync(args, null, ct);
    }
}

/// <summary>预览调用方设置的条目数或输出大小上限已触发。</summary>
public sealed class SevenZipLimitException : Exception
{
    public SevenZipLimitException() : base("压缩包列表超出预览限制。") { }
}

public sealed class SevenZipException : Exception
{
    public int? ExitCode { get; }
    public SevenZipException(string message, int? exitCode = null) : base(message) => ExitCode = exitCode;
    public SevenZipException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>加密包缺密码 / 密码错误。</summary>
public sealed class PasswordRequiredException : Exception
{
    public string ArchivePath { get; }
    public PasswordRequiredException(string archivePath)
        : base("压缩包已加密，需要密码。")
        => ArchivePath = archivePath;
}
