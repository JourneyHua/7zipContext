using System.IO;
using System.Reflection;
using Microsoft.Win32;
using ZipContext.Core;

namespace ZipContext.App;

public sealed class AppSettings : SettingsDocument
{
    public static string IniPath => Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory, "7zipContext.ini");
    public static AppSettings Load()
    {
        SettingsDocument s;
        try { s = SettingsDocument.Load(IniPath); } catch { s = new SettingsDocument(); }
        return new AppSettings
        {
            RootMax=s.RootMax, ChildrenPerFolder=s.ChildrenPerFolder, MaxDepth=s.MaxDepth,
            ShowFilesInSubfolders=s.ShowFilesInSubfolders, SplitFirstSuffixes=s.SplitFirstSuffixes,
            SplitLaterSuffixes=s.SplitLaterSuffixes, OpenWithSevenZip=s.OpenWithSevenZip,
        };
    }
    public void Save() => base.Save(IniPath);
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
