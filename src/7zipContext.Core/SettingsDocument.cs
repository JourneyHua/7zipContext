namespace ZipContext.Core;

public class SettingsDocument
{
    public int RootMax { get; set; } = 20;
    public int ChildrenPerFolder { get; set; } = 3;
    public bool ShowFilesInSubfolders { get; set; }
    public int MaxDepth { get; set; } = 1;

    // [archive] 段：分卷名规则 + 是否对所有文件显示「用 7-Zip 打开」
    public string SplitFirstSuffixes { get; set; } = ZipContext.Core.ArchiveRules.DefaultSplitFirstSuffixes;
    public string SplitLaterSuffixes { get; set; } = ZipContext.Core.ArchiveRules.DefaultSplitLaterSuffixes;
    public bool OpenWithSevenZip { get; set; } = true;

    public const string DefaultIni = """
        ; 7zipContext 压缩文件预览 —— 右键菜单里显示压缩包结构的规则
        ; 改完保存即可，下一次右键生效（不需要重启资源管理器）。
        ;
        ; rootMax             根目录最多显示几项；0 = 不自定义限制，仍受总计 200 行安全上限保护
        ; childrenPerFolder   每个子文件夹展开几个子文件夹；0 = 不展开子层
        ; showFilesInSubfolders  子层是否也显示文件（0/1）
        ; maxDepth            往下展开几层（0 = 只看根，1 = 到第二层，2 = 到第三层）

        [preview]
        rootMax=20
        childrenPerFolder=3
        showFilesInSubfolders=0
        maxDepth=1

        ; 已验证分卷入口配置；未知格式请用「用 7-Zip 打开」，不猜测解压入口
        ; splitFirstSuffixes   已支持首卷后缀；例如 .001,.z01,.part1.rar；.z01 需要末尾 .zip
        ; splitLaterSuffixes   分卷后续后缀，逗号分隔；例如 .002,.z02,.part2.rar
        ; openWithSevenZip     是否对所有文件/文件夹显示「用 7-Zip 打开」（0/1）

        [archive]
        splitFirstSuffixes=.001,.z01
        splitLaterSuffixes=.002,.z02
        openWithSevenZip=1
        """;

    public static SettingsDocument Load(string path)
    {
        var s = new SettingsDocument();
        try
        {
            if (!File.Exists(path)) return s;
            var section = "";
            foreach (var raw in AtomicTextFile.Read(path).Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim().ToLowerInvariant();
                var val = ZipContext.Core.IniValue.Read(line[(eq + 1)..]);
                if (section.Equals("preview", StringComparison.OrdinalIgnoreCase))
                {
                    switch (key)
                    {
                        case "rootmax": if (int.TryParse(val, out var r) && r >= 0) s.RootMax = r; break;
                        case "childrenperfolder": if (int.TryParse(val, out var c) && c >= 0) s.ChildrenPerFolder = c; break;
                        case "showfilesinsubfolders": s.ShowFilesInSubfolders = val is "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "maxdepth": if (int.TryParse(val, out var d) && d >= 0 && d <= 2) s.MaxDepth = d; break;
                    }
                }
                else if (section.Equals("archive", StringComparison.OrdinalIgnoreCase))
                {
                    switch (key)
                    {
                        case "splitfirstsuffixes":
                        case "splitfirst":
                            s.SplitFirstSuffixes = val;
                            break;
                        case "splitlatersuffixes":
                        case "splitlater":
                            s.SplitLaterSuffixes = val;
                            break;
                        case "openwithsevenzip":
                            s.OpenWithSevenZip = val.ToLowerInvariant() is "1" or "true" or "yes" or "on";
                            break;
                    }
                }
            }
        }
        catch (FileNotFoundException) { }
        return s;
    }

    public static List<string> ParseSuffixList(string raw)
    {
        var values = raw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.StartsWith('.') ? value : "." + value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (values.Any(value => !System.Text.RegularExpressions.Regex.IsMatch(value, @"^\.[a-zA-Z0-9.]+$")))
            throw new ArgumentException("分卷后缀只能包含英文、数字及点号。");
        return values;
    }

    private static string Canonical(string key) => key.ToLowerInvariant() switch
    {
        "splitfirst" => "splitfirstsuffixes", "splitlater" => "splitlatersuffixes", _ => key.ToLowerInvariant(),
    };

    public void Save(string path)
    {
        if (RootMax < 0 || ChildrenPerFolder < 0 || MaxDepth is < 0 or > 2) throw new ArgumentException("预览参数超出有效范围。");
        var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["preview/rootmax"] = RootMax.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["preview/childrenperfolder"] = ChildrenPerFolder.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["preview/showfilesinsubfolders"] = ShowFilesInSubfolders ? "1" : "0",
            ["preview/maxdepth"] = MaxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["archive/splitfirstsuffixes"] = string.Join(",", ParseSuffixList(SplitFirstSuffixes)),
            ["archive/splitlatersuffixes"] = string.Join(",", ParseSuffixList(SplitLaterSuffixes)),
            ["archive/openwithsevenzip"] = OpenWithSevenZip ? "1" : "0",
        };
        // Re-read latest document so edits to unknown keys since loading survive.
        var exists = File.Exists(path);
        var text = exists ? AtomicTextFile.Read(path) : DefaultIni;
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(new[] { newline }, StringSplitOptions.None).ToList();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        for (int i = 0; i < lines.Count; i++)
        {
            var raw = lines[i]; var trimmed = raw.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) { section = trimmed[1..^1].Trim(); continue; }
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            var equals = raw.IndexOf('=');
            if (equals <= 0) continue;
            var id = section + "/" + Canonical(raw[..equals].Trim());
            if (!updates.TryGetValue(id, out var value)) continue;
            found.Add(id);
            var tail = raw[(equals + 1)..];
            int comment = -1;
            for (int j = 0; j < tail.Length; j++)
                if ((tail[j] is ';' or '#') && (j == 0 || char.IsWhiteSpace(tail[j - 1]))) { comment = j; break; }
            var whitespace = new string(tail.TakeWhile(char.IsWhiteSpace).ToArray());
            var suffix = comment < 0 ? "" : (value.Length == 0 ? "" : " ") + tail[comment..];
            lines[i] = raw[..(equals + 1)] + whitespace + value + suffix;
        }
        foreach (var group in updates.Where(pair => !found.Contains(pair.Key)).GroupBy(pair => pair.Key.Split('/')[0]))
        {
            lines.Add("[" + group.Key + "]");
            foreach (var pair in group) lines.Add(pair.Key.Split('/')[1] + "=" + pair.Value);
        }
        var output = string.Join(newline, lines);
        // Preserve existing UTF-8 BOM; new config uses UTF-8 BOM.
        var bom = !exists || File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 239, 187, 191 });
        AtomicTextFile.Write(path, (bom ? "\uFEFF" : "") + output);
    }
}
