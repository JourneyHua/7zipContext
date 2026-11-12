using System.Text.Json;
using ZipContext.App;

internal static class UpdateCheckChecks
{
    internal static void Run(Action<string, bool> check, string root)
    {
        Directory.CreateDirectory(root);
        var now = DateTimeOffset.Parse("2026-11-12T00:00:00Z");
        int requests = 0;
        var store = new UpdateCheckStore(root, () => now);
        EngineProvisioner.EnsureResult Good() { requests++; return new(true, "latest", "1", "up-to-date"); }
        EngineProvisioner.EnsureResult Bad() { requests++; throw new IOException("offline fixture"); }
        var result = store.Check("engine-a|1", "1", false, Bad);
        check("update failure is offline", result.Origin == "offline" && requests == 1);
        result = store.Check("engine-a|1", "1", false, Good);
        check("update failure backoff avoids request", result.Origin == "offline" && requests == 1);
        now = now.AddMinutes(15);
        store.Check("engine-a|1", "1", false, Bad);
        now = now.AddMinutes(29);
        store.Check("engine-a|1", "1", false, Good);
        check("update repeated failure increases backoff", requests == 2);
        now = now.AddMinutes(1);
        result = store.Check("engine-a|1", "1", false, Good);
        check("update retries when delay expires", requests == 3 && result.Origin == "up-to-date");
        result = store.Check("engine-a|1", "1", false, Bad);
        check("update success cached for one day", requests == 3 && result.Origin == "cached");
        check("update cache displays freshness", result.Message.Contains("最近尝试") && result.Message.Contains("最近成功") && result.Message.Contains("下次自动检查"));
        store.Check("engine-a|1", "1", true, Bad);
        check("update force bypasses success cache", requests == 4);
        result = store.Check("engine-a|1", "1", false, Good);
        check("update offline cache never becomes fresh success", result.Origin == "offline" && result.Message.Contains("历史结论") && requests == 4);
        result = store.Check("engine-b|1", "1", false, Bad);
        check("update path change discards old conclusion", requests == 5 && !result.Message.Contains("latest"));
        store.Check("engine-b|2", "2", false, Good);
        check("update version change bypasses old throttle", requests == 6);
        now = now.AddHours(24);
        store.Check("engine-b|2", "2", false, Good);
        check("update success expires after one day", requests == 7);
        var path = Path.Combine(root, "update-check.json");
        File.WriteAllText(path, "broken json");
        store.Check("engine-b|2", "2", false, Good);
        check("update corrupt state recovers", requests == 8);
        var before = JsonSerializer.Deserialize<UpdateCheckStore.State>(File.ReadAllText(path))!;
        store.Check("engine-b|2", "2", true, Bad);
        var after = JsonSerializer.Deserialize<UpdateCheckStore.State>(File.ReadAllText(path))!;
        check("update failure retains last successful time", after.SucceededAt == before.SucceededAt && after.Failures == 1);
        for (int i = 0; i < 10; i++) store.Check("engine-b|2", "2", true, Bad);
        after = JsonSerializer.Deserialize<UpdateCheckStore.State>(File.ReadAllText(path))!;
        check("update failure backoff capped at six hours", after.NextAttemptAt - after.AttemptedAt == TimeSpan.FromHours(6));
        now = now.AddDays(-2);
        var count = requests;
        store.Check("engine-b|2", "2", false, Good);
        check("update future timestamps cannot freeze checking", requests == count + 1);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            count = requests;
            result = store.Check("engine-b|2", "2", true, Good);
            check("update cannot request without recording attempt", !result.Ok && requests == count);
        }
    }
}