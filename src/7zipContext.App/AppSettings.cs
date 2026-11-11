using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace ZipContext.App;

/// <summary>
/// 设置窗口用的配置读写。键名与 7zipContext.Classic/PreviewSettings.cs 保持一致
/// （经典菜单跑在 explorer 进程内，这里不改动它的读取逻辑）。
/// ini 与 exe 同目录，即 dist\7zipContext.ini；保存后下一次右键立即生效。
/// </summary>
public sealed class AppSettings
{
    public int RootMax { get; set; } = 20;
    public int ChildrenPerFolder { get; set; } = 3;
    public bool ShowFilesInSubfolders { get; set; }
    public int MaxDepth { get; set; } = 1;

    // [archive] 段：分卷名规则 + 是否对所有文件显示「用 7-Zip 打开」
    public string SplitFirstSuffixes { get; set; } = ZipContext.Core.ArchiveRules.DefaultSplitFirstSuffixes;
    public string SplitLaterSuffixes { get; set; } = ZipContext.Core.ArchiveRules.DefaultSplitLaterSuffixes;
    public bool OpenWithSevenZip { get; set; } = true;

    public static string IniPath => Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory,
        "7zipContext.ini");

    public const string DefaultIni = """
        ; 7zipContext 压缩文件预览 —— 右键菜单里显示压缩包结构的规则
        ; 改完保存即可，下一次右键生效（不需要重启资源管理器）。
        ;
        ; rootMax             根目录最多显示几项；0 = 不限制
        ; childrenPerFolder   每个子文件夹展开几个子文件夹；0 = 不展开子层
        ; showFilesInSubfolders  子层是否也显示文件（0/1）
        ; maxDepth            往下展开几层（0 = 只看根，1 = 到第二层，2 = 到第三层）

        [preview]
        rootMax=20
        childrenPerFolder=3
        showFilesInSubfolders=0
        maxDepth=1

        ; 压缩包识别规则：遇到奇怪分卷名只改这里，不用改程序
        ; splitFirstSuffixes   分卷首卷后缀，逗号分隔；例如 .001,.z01,.part1.rar
        ; splitLaterSuffixes   分卷后续后缀，逗号分隔；例如 .002,.z02,.part2.rar
        ; openWithSevenZip     是否对所有文件/文件夹显示「用 7-Zip 打开」（0/1）

        [archive]
        splitFirstSuffixes=.001,.z01
        splitLaterSuffixes=.002,.z02
        openWithSevenZip=1
        """;

    public static AppSettings Load()
    {
        var s = new AppSettings();
        try
        {
            if (!File.Exists(IniPath)) return s;
            var section = "";
            foreach (var raw in File.ReadAllLines(IniPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim().ToLowerInvariant();
                var val = line[(eq + 1)..].Trim();
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
                            if (!string.IsNullOrWhiteSpace(val)) s.SplitFirstSuffixes = val;
                            break;
                        case "splitlatersuffixes":
                        case "splitlater":
                            if (!string.IsNullOrWhiteSpace(val)) s.SplitLaterSuffixes = val;
                            break;
                        case "openwithsevenzip":
                            s.OpenWithSevenZip = val is "1" or "true" or "yes" or "on";
                            break;
                    }
                }
            }
        }
        catch { /* 读不到就用默认值 */ }
        return s;
    }

    public void Save()
    {
        // 逐行重建，不用链式 Replace（避免注释/值互相误伤或未来加键时踩坑）
        var lines = new List<string>();
        foreach (var raw in DefaultIni.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=');
            if (eq <= 0 || line.TrimStart().StartsWith(';') || line.TrimStart().StartsWith('#'))
            {
                lines.Add(line);
                continue;
            }
            var key = line[..eq].Trim().ToLowerInvariant();
            lines.Add(key switch
            {
                "rootmax" => $"rootMax={RootMax}",
                "childrenperfolder" => $"childrenPerFolder={ChildrenPerFolder}",
                "showfilesinsubfolders" => $"showFilesInSubfolders={(ShowFilesInSubfolders ? 1 : 0)}",
                "maxdepth" => $"maxDepth={MaxDepth}",
                "splitfirstsuffixes" => $"splitFirstSuffixes={SplitFirstSuffixes}",
                "splitlatersuffixes" => $"splitLaterSuffixes={SplitLaterSuffixes}",
                "openwithsevenzip" => $"openWithSevenZip={(OpenWithSevenZip ? 1 : 0)}",
                _ => line,
            });
        }
        File.WriteAllText(IniPath, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(true));
    }

    /// <summary>三个扩展名的 ProgId 是否已指向 7zFM.exe。</summary>
    public static (int Ok, int Total) AssociationStatus()
    {
        var ok = 0;
        foreach (var ext in new[] { ".zip", ".7z", ".rar" })
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\7zipContext{ext}\shell\open\command");
                var v = k?.GetValue("") as string;
                if (v is not null && v.Contains("7zFM.exe", StringComparison.OrdinalIgnoreCase)) ok++;
            }
            catch { }
        }
        return (ok, 3);
    }
}
