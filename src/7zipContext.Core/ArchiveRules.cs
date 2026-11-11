using System.Text.RegularExpressions;

namespace ZipContext.Core;

/// <summary>
/// 压缩包识别规则（dist\7zipContext.ini 的 [archive] 段）。
/// 目标：以后遇到奇怪分卷名只改配置，不改代码。
/// 菜单路径允许读本地 ini；这里零网络、零子进程。
/// </summary>
public sealed class ArchiveRules
{
    public HashSet<string> SplitFirstSuffixes { get; private set; }

    public HashSet<string> SplitLaterSuffixes { get; private set; }

    public bool OpenWithSevenZip { get; private set; } = true;

    public ArchiveRules()
    {
        SplitFirstSuffixes = ParseSuffixes(DefaultSplitFirstSuffixes, DefaultSplitFirstSuffixes);
        SplitLaterSuffixes = ParseSuffixes(DefaultSplitLaterSuffixes, DefaultSplitLaterSuffixes);
    }

    private static ArchiveRules _cached = new();
    private static DateTime _cachedStamp = DateTime.MinValue;
    private static string? _path;

    public static ArchiveRules Current
    {
        get
        {
            try
            {
                var p = IniPath;
                if (p is null || !File.Exists(p)) return _cached;

                var stamp = File.GetLastWriteTimeUtc(p);
                if (stamp == _cachedStamp) return _cached;

                _cached = Parse(p);
                _cachedStamp = stamp;
                return _cached;
            }
            catch
            {
                return _cached; // 读配置失败用上一份/默认值，绝不让菜单挂掉
            }
        }
    }

    public static string? IniPath
    {
        get
        {
            if (_path is not null) return _path;
            var asm = typeof(ArchiveRules).Assembly.Location;
            var dir = Path.GetDirectoryName(asm);
            if (string.IsNullOrEmpty(dir)) return null;
            _path = Path.Combine(dir, "7zipContext.ini");
            return _path;
        }
    }

    public static string DefaultSplitFirstSuffixes => ".001,.z01";
    public static string DefaultSplitLaterSuffixes => ".002,.z02";

    private static ArchiveRules Parse(string path)
    {
        var rules = new ArchiveRules();
        var section = "";
        var firstRaw = DefaultSplitFirstSuffixes;
        var laterRaw = DefaultSplitLaterSuffixes;
        var openWith = "1";

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            if (!section.Equals("archive", StringComparison.OrdinalIgnoreCase)) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var val = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "splitfirstsuffixes":
                case "splitfirst":
                    firstRaw = val;
                    break;
                case "splitlatersuffixes":
                case "splitlater":
                    laterRaw = val;
                    break;
                case "openwithsevenzip":
                    openWith = val;
                    break;
            }
        }

        rules.SplitFirstSuffixes = ParseSuffixes(firstRaw, DefaultSplitFirstSuffixes);
        rules.SplitLaterSuffixes = ParseSuffixes(laterRaw, DefaultSplitLaterSuffixes);
        rules.OpenWithSevenZip = openWith is "1" or "true" or "yes" or "on";
        return rules;
    }

    private static HashSet<string> ParseSuffixes(string raw, string fallback)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var suffix = part.Trim();
            if (suffix.Length == 0) continue;
            if (!suffix.StartsWith('.')) suffix = "." + suffix;
            set.Add(suffix);
        }
        if (set.Count > 0) return set;

        foreach (var part in fallback.Split(',', StringSplitOptions.RemoveEmptyEntries))
            set.Add(part.Trim());
        return set;
    }

    /// <summary>把配置序列化回 ini 值形式，供设置面板显示/保存。</summary>
    public string SplitFirstValue => string.Join(",", SplitFirstSuffixes);
    public string SplitLaterValue => string.Join(",", SplitLaterSuffixes);
}

/// <summary>分卷后缀匹配与同组文件查找。</summary>
public static class SplitVolumeNames
{
    // .001 / .z01 / .part1.rar 都能拆成 prefix + 序号 + tail
    private static readonly Regex NumberedSuffix = new(
        @"^(?<prefix>.*?)(?<number>\d{1,6})(?<tail>[^0-9]*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool HasSuffix(string path, HashSet<string> suffixes) =>
        suffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    public static string? MatchSuffix(string path, HashSet<string> suffixes) =>
        suffixes.FirstOrDefault(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>去掉分卷后缀，得到同组基础名（不含 .001/.z01/.part1.rar）。</summary>
    public static string StripSplitSuffix(string path, HashSet<string> suffixes)
    {
        var suffix = MatchSuffix(path, suffixes);
        return suffix is null ? path : path[..^suffix.Length];
    }

    /// <summary>
    /// 取同一分卷组的已存在文件。支持：
    ///   1) 配置里的后续后缀（base + .002 / base + .z02）
    ///   2) 首卷后缀带序号时自动递增（.001→.002→.003，.part1.rar→.part2.rar）
    /// </summary>
    public static IReadOnlyList<string> GroupFiles(string firstPath,
        HashSet<string> firstSuffixes, HashSet<string> laterSuffixes)
    {
        var first = Path.GetFullPath(firstPath);
        var firstSuffix = MatchSuffix(first, firstSuffixes);
        if (firstSuffix is null) return new[] { first };

        var dir = Path.GetDirectoryName(first) ?? "";
        var baseName = Path.GetFileName(first[..^firstSuffix.Length]);
        var files = new List<string> { first };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first };

        void Add(string candidate)
        {
            if (seen.Add(candidate) && File.Exists(candidate)) files.Add(candidate);
        }

        foreach (var laterSuffix in laterSuffixes)
            Add(Path.Combine(dir, baseName + laterSuffix));

        var match = NumberedSuffix.Match(firstSuffix);
        if (match.Success && int.TryParse(match.Groups["number"].Value, out var start))
        {
            var width = match.Groups["number"].Value.Length;
            var prefix = match.Groups["prefix"].Value;
            var tail = match.Groups["tail"].Value;
            for (var i = start + 1; i < start + 1000; i++)
            {
                var candidate = Path.Combine(dir,
                    baseName + prefix + i.ToString().PadLeft(width, '0') + tail);
                if (!File.Exists(candidate)) break;
                Add(candidate);
            }
        }

        return files;
    }
}
