using System.Text.RegularExpressions;

namespace ZipContext.Core;

/// <summary>
/// 压缩包识别规则（dist\7zipContext.ini 的 [archive] 段）。
/// 已验证分卷格式的入口可配置；未知格式不猜测解压入口，使用独立打开动词。
/// 菜单路径允许读本地 ini；这里零网络、零子进程。
/// </summary>
public sealed class ArchiveRules
{
    public HashSet<string> SplitFirstSuffixes { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> SplitLaterSuffixes { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool OpenWithSevenZip { get; private set; } = true;

    // 保留配置里的原始顺序：HashSet 的枚举顺序不保证，直接序列化会让每次保存都产生无意义差异。
    private string[] _firstOrder = Array.Empty<string>();
    private string[] _laterOrder = Array.Empty<string>();

    public ArchiveRules()
    {
        SetSuffixes(SettingsDocument.ParseSuffixList(DefaultSplitFirstSuffixes),
            SettingsDocument.ParseSuffixList(DefaultSplitLaterSuffixes));
    }

    private static ArchiveRules _cached = new();
    private static DateTime _cachedStamp = DateTime.MinValue;
    private static string? _path;
    // explorer.exe 内会并发首次读取：不能出现「新时间戳 + 旧对象」的半更新状态。
    private static readonly object CacheLock = new();

    public static ArchiveRules Current
    {
        get
        {
            try
            {
                var p = IniPath;
                if (p is null || !File.Exists(p))
                {
                    lock (CacheLock)
                    {
                        _cachedStamp = DateTime.MinValue;
                        return _cached = new ArchiveRules();
                    }
                }

                var stamp = File.GetLastWriteTimeUtc(p);
                lock (CacheLock)
                {
                    if (stamp == _cachedStamp) return _cached;

                    _cached = Parse(p);
                    _cachedStamp = stamp;
                    return _cached;
                }
            }
            catch
            {
                return Volatile.Read(ref _cached); // 读配置失败用上一份/默认值，绝不让菜单挂掉
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
        var settings = SettingsDocument.Load(path);
        var rules = new ArchiveRules();
        rules.SetSuffixes(SettingsDocument.ParseSuffixList(settings.SplitFirstSuffixes),
            SettingsDocument.ParseSuffixList(settings.SplitLaterSuffixes));
        rules.OpenWithSevenZip = settings.OpenWithSevenZip;
        return rules;
    }

    /// <summary>统一入口：建索引用大小写不敏感集合，序列化用原始顺序。</summary>
    private void SetSuffixes(IEnumerable<string> first, IEnumerable<string> later)
    {
        _firstOrder = first.ToArray();
        _laterOrder = later.ToArray();
        SplitFirstSuffixes = new HashSet<string>(_firstOrder, StringComparer.OrdinalIgnoreCase);
        SplitLaterSuffixes = new HashSet<string>(_laterOrder, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>把配置序列化回 ini 值形式，供设置面板显示/保存。按配置原始顺序输出。</summary>
    public string SplitFirstValue => string.Join(",", _firstOrder);
    public string SplitLaterValue => string.Join(",", _laterOrder);
}

/// <summary>分卷后缀匹配与同组文件查找。</summary>
public static class SplitVolumeNames
{
    // .001 / .z01 / .part1.rar 都能拆成 prefix + 序号 + tail
    private static readonly Regex NumberedSuffix = new(
        @"^(?<prefix>.*?)(?<number>[0-9]+)(?<tail>[^0-9]*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool HasSuffix(string path, HashSet<string> suffixes) =>
        suffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    public static string? MatchSuffix(string path, HashSet<string> suffixes) =>
        suffixes.OrderByDescending(s => s.Length)
            .FirstOrDefault(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>去掉分卷后缀，得到同组基础名（不含 .001/.z01/.part1.rar）。</summary>
    public static string StripSplitSuffix(string path, HashSet<string> suffixes)
    {
        var suffix = MatchSuffix(path, suffixes);
        return suffix is null ? path : path[..^suffix.Length];
    }

    public sealed record DeletionPlan(IReadOnlyList<string> Files, bool CanDelete, string? Reason);

    /// <summary>按同一编号家族找文件；真正删除前必须另查 DeletionPlan.CanDelete。</summary>
    public static IReadOnlyList<string> GroupFiles(string firstPath,
        HashSet<string> firstSuffixes, HashSet<string> laterSuffixes) =>
        PlanDeletion(firstPath, firstSuffixes, laterSuffixes).Files;

    /// <summary>未知命名、编号空缺或传统分卷 ZIP 均拒绝自动删除，不跨家族拼接后缀。</summary>
    public static DeletionPlan PlanDeletion(string firstPath,
        HashSet<string> firstSuffixes, HashSet<string> laterSuffixes)
    {
        var first = Path.GetFullPath(firstPath);
        var firstSuffix = MatchSuffix(first, firstSuffixes);
        // 删除保护不依赖用户是否启用了常见格式的菜单入口。
        if (firstSuffix is null)
        {
            var conventional = Regex.Match(Path.GetFileName(first),
                @"(?<suffix>\.(?:[0-9]+|z[0-9]+|part[0-9]+\.rar))$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (conventional.Success) firstSuffix = conventional.Groups["suffix"].Value;
        }
        var single = new[] { first };
        DeletionPlan Deny(string reason) => new(single, false, reason);
        try
        {
            if (!File.Exists(first)) return Deny("源文件已不存在");
            var dir = Path.GetDirectoryName(first)!;
            if (firstSuffix is null)
            {
                if (HasSuffix(first, laterSuffixes)) return Deny("所选文件是后续卷");
                // 传统 ZIP 从末尾 .zip 打开；旧式 RAR 从 .rar 打开，尚无实际用卷清单。
                var ext = Path.GetExtension(first);
                var stem = Path.GetFileNameWithoutExtension(first);
                if ((ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals(".rar", StringComparison.OrdinalIgnoreCase)) &&
                    Directory.EnumerateFiles(dir).Any(p => Regex.IsMatch(Path.GetFileName(p),
                        "^" + Regex.Escape(stem) +
                        (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ? @"\.z[0-9]+$" : @"\.r[0-9]+$"),
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                    return Deny("检测到传统分卷，无法可靠确认实际使用的全部卷");
                return new(single, true, null);
            }

            var match = NumberedSuffix.Match(firstSuffix);
            if (!match.Success || !long.TryParse(match.Groups["number"].Value, out var start) || start != 1)
                return Deny("分卷命名没有受支持的首卷编号");
            var baseName = Path.GetFileName(first[..^firstSuffix.Length]);
            var width = match.Groups["number"].Value.Length;
            var prefix = match.Groups["prefix"].Value;
            var tail = match.Groups["tail"].Value;
            var family = new Regex("^" + Regex.Escape(baseName + prefix) +
                @"(?<number>[0-9]+)" + Regex.Escape(tail) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var numbered = new SortedDictionary<long, string>();
            var ambiguous = false;
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                var item = family.Match(Path.GetFileName(path));
                if (!item.Success) continue;
                var digits = item.Groups["number"].Value;
                if (!long.TryParse(digits, out var number) || number < start ||
                    digits != number.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width, '0') ||
                    !numbered.TryAdd(number, path)) ambiguous = true;
            }
            var files = numbered.Values.ToArray();
            var contiguous = numbered.Count > 0 && numbered.Keys.First() == start &&
                numbered.Keys.Last() - start == numbered.Count - 1;
            var known = firstSuffix.Equals(".001", StringComparison.OrdinalIgnoreCase) ||
                (prefix.Equals(".part", StringComparison.OrdinalIgnoreCase) &&
                 tail.Equals(".rar", StringComparison.OrdinalIgnoreCase));
            var reason = ambiguous ? "分卷编号存在重复或不一致的补零方式" :
                !contiguous ? "分卷编号不连续" :
                !known ? "该分卷格式尚不能可靠确认删除范围（含传统分卷 ZIP 和自定义格式）" : null;
            return new(files, reason is null, reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Deny("无法检查完整分卷组：" + ex.Message);
        }
    }
}
