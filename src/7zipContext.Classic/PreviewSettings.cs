using System.Reflection;

namespace ZipContext.Classic;

/// <summary>
/// 预览显示设置，读取 dist\7zipContext.ini（与 7zipContext.exe 同目录，便携）。
/// 按文件修改时间缓存：改完 ini 下一次右键就生效，不需要重启资源管理器。
/// 文件缺失时只读默认值；配置仅由设置面板显式保存。
/// </summary>
internal sealed class PreviewSettings
{
    private ZipContext.Core.SettingsDocument _settings = new();
    public int RootMax => _settings.RootMax;
    public int ChildrenPerFolder => _settings.ChildrenPerFolder;
    public bool ShowFilesInSubfolders => _settings.ShowFilesInSubfolders;
    public int MaxDepth => _settings.MaxDepth;
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
                    _cachedStamp = DateTime.MinValue;

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
        var s = ZipContext.Core.SettingsDocument.Load(path);
        return new PreviewSettings { _settings=s };
    }
    public const string DefaultIni = ZipContext.Core.SettingsDocument.DefaultIni;
}