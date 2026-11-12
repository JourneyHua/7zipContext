using System.IO;
using System.Text.Json;
using ZipContext.Core;

namespace ZipContext.App;

/// <summary>在部署/引擎锁内使用；先保存尝试，再请求，崩溃后也有退避。</summary>
internal sealed class UpdateCheckStore(string root, Func<DateTimeOffset>? clock = null)
{
    internal sealed record State(string Identity, DateTimeOffset AttemptedAt, DateTimeOffset? SucceededAt,
        DateTimeOffset NextAttemptAt, int Failures, bool LastAttemptSucceeded,
        EngineProvisioner.EnsureResult? Cached, string? Error);
    private readonly string _path = Path.Combine(root, "update-check.json");
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    internal EngineProvisioner.EnsureResult Check(string identity, string version, bool force,
        Func<EngineProvisioner.EnsureResult> fetch)
    {
        var now = _clock();
        State? state = null;
        try { state = JsonSerializer.Deserialize<State>(AtomicTextFile.Read(_path)); } catch { }
        if (state is not null && (state.Identity != identity || state.AttemptedAt > now ||
            state.NextAttemptAt < state.AttemptedAt || state.NextAttemptAt > state.AttemptedAt.AddHours(24) ||
            state.Failures < 0 || (state.LastAttemptSucceeded && state.Cached is null) ||
            state.SucceededAt > now || state.SucceededAt > state.AttemptedAt)) state = null;
        if (!force && state is not null && now < state.NextAttemptAt)
            return Describe(state, version, cached: true);
        var pending = new State(identity, now, state?.SucceededAt, now.AddMinutes(5),
            state?.Failures ?? 0, false, state?.Cached, "上次检查未完成。");
        try { Save(pending); }
        catch (Exception ex) { return new(false, "无法记录更新尝试，未发起请求：" + ex.Message, version, "failed"); }
        EngineProvisioner.EnsureResult result;
        try { result = fetch(); }
        catch (Exception ex) { result = new(false, ex.Message, version, "offline"); }
        var success = result.Ok && result.Origin != "offline";
        var failures = success ? 0 : Math.Min(pending.Failures, 5) + 1;
        var completed = pending with
        {
            SucceededAt = success ? now : pending.SucceededAt,
            NextAttemptAt = success ? now.AddHours(24) : now.AddMinutes(Math.Min(360, 15 * Math.Pow(2, failures - 1))),
            Failures = failures, LastAttemptSucceeded = success,
            Cached = success ? result : pending.Cached, Error = success ? null : result.Message,
        };
        try { Save(completed); }
        catch (Exception ex) { return new(false, result.Message + "\n保存检查结果失败：" + ex.Message, version, "failed"); }
        return Describe(completed, version, cached: false);
    }

    private void Save(State state) => AtomicTextFile.Write(_path, JsonSerializer.Serialize(state));
    private static EngineProvisioner.EnsureResult Describe(State state, string version, bool cached)
    {
        var freshness = $"\n最近尝试：{state.AttemptedAt:O}\n最近成功：{(state.SucceededAt is { } time ? time.ToString("O") : "无")}\n下次自动检查：{state.NextAttemptAt:O}";
        if (!state.LastAttemptSucceeded)
            return new(true, "检查未完成：" + state.Error +
                (state.Cached is null ? "\n没有当前引擎的历史结论。" : "\n历史结论（非本次联网结果）：" + state.Cached.Message) + freshness,
                version, "offline");
        var result = state.Cached!;
        return result with { Message = (cached ? "缓存结果：" : "") + result.Message + freshness,
            Origin = cached ? "cached" : result.Origin };
    }
}
