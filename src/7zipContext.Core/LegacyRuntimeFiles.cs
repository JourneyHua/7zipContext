using System.Text.Json;

namespace ZipContext.Core;

/// <summary>Identify obsolete runtime assets from the installed application's dependency manifest.</summary>
public static class LegacyRuntimeFiles
{
    public static string[] Find(string installedRoot, string candidateRoot)
    {
        var dist = Path.Combine(installedRoot, "dist");
        if (!File.Exists(Path.Combine(dist, "coreclr.dll"))) return Array.Empty<string>();
        var deps = Path.Combine(dist, "7zipContext.deps.json");
        using var document = JsonDocument.Parse(File.ReadAllText(deps));
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in document.RootElement.GetProperty("targets").EnumerateObject())
        foreach (var library in target.Value.EnumerateObject())
        {
            if (!library.Name.StartsWith("runtimepack.Microsoft.NETCore.App.Runtime.win-x64/", StringComparison.Ordinal) &&
                !library.Name.StartsWith("runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/", StringComparison.Ordinal)) continue;
            foreach (var section in new[] { "runtime", "native", "resources" })
            {
                if (!library.Value.TryGetProperty(section, out var assets)) continue;
                foreach (var asset in assets.EnumerateObject())
                {
                    var parts = asset.Name.Replace('\\', '/').Split('/');
                    if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains(':')) || Path.IsPathRooted(asset.Name))
                        throw new IOException("旧运行库清单包含无效路径，升级已停止。");
                    var name = parts[^1];
                    if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("7zipContext", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ZipContext", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("旧运行库清单包含项目组件，升级已停止。");
                    var relative = Path.Combine(new[] { "dist" }.Concat(parts).ToArray());
                    if (File.Exists(Path.Combine(installedRoot, relative)) && !File.Exists(Path.Combine(candidateRoot, relative))) files.Add(relative);
                }
            }
        }
        if (!files.Contains(Path.Combine("dist", "coreclr.dll")))
            throw new IOException("无法确认旧运行库的文件归属，升级已停止；请保留原安装与备份。");
        return files.ToArray();
    }
}
