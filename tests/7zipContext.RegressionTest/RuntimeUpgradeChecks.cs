using System.Text.Json;
using ZipContext.Core;
using ZipContext.Deployment;

internal static class RuntimeUpgradeChecks
{
    internal static void Run(string work, Action<string, bool> check)
    {
        var root = Path.Combine(work, "runtime-upgrade");
        var installed = Path.Combine(root, "installed");
        var candidate = Path.Combine(root, "candidate");
        Directory.CreateDirectory(Path.Combine(installed, "dist"));
        Directory.CreateDirectory(Path.Combine(candidate, "dist"));
        void Old(string name, string text) => File.WriteAllText(Path.Combine(installed, "dist", name), text);
        Old("coreclr.dll", "old-runtime");
        Old("System.Private.CoreLib.dll", "old-corelib");
        Old("7zipContext.ini", "user-settings");
        Old("custom.txt", "user-file");
        Old("7zipContext.dll", "old-app");
        var deps = Path.Combine(installed, "dist", "7zipContext.deps.json");
        void Manifest(string asset) => File.WriteAllText(deps, JsonSerializer.Serialize(new
        {
            targets = new Dictionary<string, object> { ["win-x64"] = new Dictionary<string, object>
            {
                ["runtimepack.Microsoft.NETCore.App.Runtime.win-x64/8.0.33"] = new
                {
                    native = new Dictionary<string, object> { [asset] = new { } },
                    runtime = new Dictionary<string, object> { ["System.Private.CoreLib.dll"] = new { } }
                }
            } }
        }));
        Manifest("coreclr.dll");
        File.WriteAllText(Path.Combine(candidate, "dist", "7zipContext.dll"), "new-app");
        File.WriteAllText(Path.Combine(candidate, "dist", "7zipContext.ini"), "default-settings");
        var removals = LegacyRuntimeFiles.Find(installed, candidate);
        check("runtime upgrade identifies only manifest-owned runtime assets", removals.Length == 2 && removals.All(p => !p.EndsWith(".ini")));
        var transaction = FileDeployment.Prepare(candidate, installed, Path.Combine(root, "transaction"),
            new[] { Path.Combine("dist", "7zipContext.ini") }, false, removals);
        transaction.Apply();
        check("runtime upgrade removes obsolete coreclr and CoreLib", !File.Exists(Path.Combine(installed, "dist", "coreclr.dll")) && !File.Exists(Path.Combine(installed, "dist", "System.Private.CoreLib.dll")));
        check("runtime upgrade preserves INI and unrelated files", File.ReadAllText(Path.Combine(installed, "dist", "7zipContext.ini")) == "user-settings" && File.ReadAllText(Path.Combine(installed, "dist", "custom.txt")) == "user-file");
        transaction.Rollback();
        check("runtime upgrade rollback restores runtime and old app", File.ReadAllText(Path.Combine(installed, "dist", "coreclr.dll")) == "old-runtime" && File.ReadAllText(Path.Combine(installed, "dist", "7zipContext.dll")) == "old-app");
        transaction.Cleanup();
        Manifest("../escape.dll");
        var rejected = false;
        try { LegacyRuntimeFiles.Find(installed, candidate); } catch (IOException) { rejected = true; }
        check("runtime upgrade rejects manifest traversal", rejected);
        Manifest("7zipContext.dll");
        rejected = false;
        try { LegacyRuntimeFiles.Find(installed, candidate); } catch (IOException) { rejected = true; }
        check("runtime upgrade rejects project components in runtime ownership", rejected);
    }
}
