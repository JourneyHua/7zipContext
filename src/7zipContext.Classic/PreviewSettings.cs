using System.Reflection;

namespace ZipContext.Classic;

/// <summary>
/// 预览显示设置，读取 dist\7zipContext.ini（与 7zipContext.exe 同目录，便携）。
/// 按文件修改时间缓存：改完 ini 下一次右键就生效，不需要重启资源管理器。
/// 文件缺失时用默认值，并尝试写一份带注释的默认文件（写不了就算了）。
/// </summary>
internal sealed class PreviewSettings
{
    public int RootMax { get; private set; } = 20;
    public int ChildrenPerFolder { get; private set; } = 3;
    public bool ShowFilesInSubfolders { get; private set; }
    public int MaxDepth { get; private set; } = 1;

    private static PreviewSettings _cached = new();
    private static DateTime _cachedStamp = DateTime.MinValue;
    private static string? _path;

    public static PreviewSettings Current
    {
        get
        {
            try
            {
                var p = IniPath;
                if (p is null) return _cached;

                if (!File.Exists(p))
                {
                    _cached = new PreviewSettings();
                    TryWriteDefault(p);
                    return _cached;
                }

                var stamp = File.GetLastWriteTimeUtc(p);
                if (stamp == _cachedStamp) return _cached;

                _cached = Parse(p);
                _cachedStamp = stamp;
                return _cached;
            }
            catch
            {
                return _cached; // 读配置失败一律用上一份/默认值，绝不让菜单挂掉
            }
        }
    }

    public static string? IniPath
    {
        get
        {
            if (_path is not null) return _path;
            var asm = Assembly.GetExecutingAssembly().Location;
            var dir = Path.GetDirectoryName(asm);
            if (string.IsNullOrEmpty(dir)) return null;
            _path = Path.Combine(dir, "7zipContext.ini");
            return _path;
        }
    }

    private static PreviewSettings Parse(string path)
    {
        var s = new PreviewSettings();
        var section = "";

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (!section.Equals("preview", StringComparison.OrdinalIgnoreCase)) continue;

            switch (key.ToLowerInvariant())
            {
                case "rootmax":
                    if (int.TryParse(val, out var r) && r >= 0) s.RootMax = r;
                    break;
                case "childrenperfolder":
                    if (int.TryParse(val, out var c) && c >= 0) s.ChildrenPerFolder = c;
                    break;
                case "showfilesinsubfolders":
                    s.ShowFilesInSubfolders = val is "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                case "maxdepth":
                    if (int.TryParse(val, out var d) && d >= 0 && d <= 2) s.MaxDepth = d;
                    break;
            }
        }
        return s;
    }

    private static void TryWriteDefault(string path)
    {
        try
        {
            File.WriteAllText(path, DefaultIni, new System.Text.UTF8Encoding(true));
        }
        catch { /* 只读目录就静默跳过 */ }
    }

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
""";
}
