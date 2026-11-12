using System.Diagnostics;
using System.Text.Json;
using ZipContext.App;
using ZipContext.Core;

internal static class EngineStateChecks
{
    internal static async Task<int> Child(string[] args)
    {
        try
        {
            using var gate = EngineOperationLock.Acquire(args[1]);
            File.WriteAllText(args[2], "acquired");
            if (args[3] == "hold") await Task.Delay(TimeSpan.FromMinutes(2));
            return 0;
        }
        catch (EngineBusyException) { File.WriteAllText(args[2], "busy"); return 23; }
    }

    internal static async Task Run(Action<string, bool> check, string work)
    {
        Directory.CreateDirectory(work);
        var lockRoot = Path.Combine(work, "locks");
        using (EngineOperationLock.Acquire(lockRoot))
        {
            var busy = false;
            try { using var other = EngineOperationLock.Acquire(lockRoot); }
            catch (EngineBusyException) { busy = true; }
            check("engine lock rejects concurrent same-process writer", busy);
            using var independent = EngineOperationLock.Acquire(Path.Combine(work, "independent-lock"));
            check("independent engine roots do not block each other", independent is not null);
            using var probe = Start(lockRoot, Path.Combine(work, "probe-busy"), "probe");
            await Finish(probe);
            check("engine lock rejects concurrent process", probe.ExitCode == 23 && File.ReadAllText(Path.Combine(work, "probe-busy")) == "busy");
        }
        using (var probe = Start(lockRoot, Path.Combine(work, "probe-free"), "probe"))
        {
            await Finish(probe);
            check("engine lock available after normal disposal", probe.ExitCode == 0);
        }
        var ready = Path.Combine(work, "hold-ready");
        using (var holder = Start(lockRoot, ready, "hold"))
        {
            try
            {
                var clock = Stopwatch.StartNew();
                while (!File.Exists(ready) && !holder.HasExited && clock.ElapsedMilliseconds < 10000) await Task.Delay(20);
                check("lock holder child started", File.Exists(ready) && File.ReadAllText(ready) == "acquired");
                var busy = false;
                try { using var competing = EngineOperationLock.Acquire(lockRoot); }
                catch (EngineBusyException) { busy = true; }
                check("live holder excludes competing writer", busy);
            }
            finally
            {
                if (!holder.HasExited) holder.Kill(entireProcessTree: true);
                await holder.WaitForExitAsync();
            }
        }
        var released = false;
        var releaseClock = Stopwatch.StartNew();
        while (!released && releaseClock.Elapsed < TimeSpan.FromSeconds(5))
        {
            try { using var recovered = EngineOperationLock.Acquire(lockRoot); released = true; }
            catch (EngineBusyException) { await Task.Delay(20); }
        }
        check("terminated process releases engine lock within bounded cleanup time", released);

        string Engine(string name)
        {
            var directory = Path.Combine(work, name);
            Directory.CreateDirectory(directory);
            foreach (var file in EngineLocator.RequiredFiles) File.WriteAllText(Path.Combine(directory, file), "fixture only, never executed");
            return directory;
        }
        var first = new EngineProvisioner.EngineInfo("1.0", Engine("engine A 中文"), "local", "hash-a", "time-a", "local");
        var second = new EngineProvisioner.EngineInfo("2.0", Engine("engine B 中文"), "source-b", "hash-b", "time-b", "downloaded");
        var root = Path.Combine(work, "state");
        using var stateGate = EngineOperationLock.Acquire(root);
        var store = new EngineStateStore(root);
        check("initial engine state commit succeeds", store.Commit(first) is null && store.ReadPointer() == first.Path && store.ReadMetadata() == first);
        check("engine switch binds version origin and hash to pointer", store.Commit(second) is null && store.ReadPointer() == second.Path && store.ReadMetadata() == second);
        AtomicTextFile.Write(store.PointerPath, first.Path);
        check("manual pointer rollback restores corresponding metadata", store.ReadMetadata() == first);

        var failure = new EngineStateStore(root, (path, value) =>
        {
            if (Path.GetFileName(path) == "engine.path") throw new IOException("simulated pointer failure");
            AtomicTextFile.Write(path, value);
        });
        var failed = false;
        try { failure.Commit(second); } catch (IOException) { failed = true; }
        check("pointer commit failure preserves active engine and metadata", failed && store.ReadPointer() == first.Path && store.ReadMetadata() == first);
        failed = false;
        try { failure.Commit(first with { Version = "1.1" }); } catch (IOException) { failed = true; }
        check("same-directory commit failure restores old metadata", failed && store.ReadMetadata() == first);

        var mirrorFailure = new EngineStateStore(root, (path, value) =>
        {
            if (Path.GetFileName(path) == "engine.json") throw new IOException("simulated compatibility mirror failure");
            AtomicTextFile.Write(path, value);
        });
        var warning = mirrorFailure.Commit(second);
        check("compatibility mirror failure reports warning without mixing state", !string.IsNullOrEmpty(warning) &&
            store.ReadPointer() == second.Path && store.ReadMetadata() == second);
        failed = false;
        try { store.Commit(first with { Path = Path.Combine(work, "incomplete") }); } catch (IOException) { failed = true; }
        check("incomplete engine cannot replace current pointer", failed && store.ReadMetadata() == second);

        var legacyRoot = Path.Combine(work, "legacy");
        Directory.CreateDirectory(legacyRoot);
        var legacy = new EngineStateStore(legacyRoot);
        AtomicTextFile.Write(legacy.PointerPath, first.Path);
        AtomicTextFile.Write(legacy.MetadataPath, JsonSerializer.Serialize(first));
        check("legacy matching metadata remains readable", legacy.ReadMetadata() == first);
        legacy.Commit(second);
        AtomicTextFile.Write(legacy.PointerPath, first.Path);
        check("legacy source and hash survive migration and rollback", legacy.ReadMetadata() == first);
        var mismatchRoot = Path.Combine(work, "mismatch");
        Directory.CreateDirectory(mismatchRoot);
        var mismatch = new EngineStateStore(mismatchRoot);
        AtomicTextFile.Write(mismatch.PointerPath, second.Path);
        AtomicTextFile.Write(mismatch.MetadataPath, JsonSerializer.Serialize(first));
        check("metadata for another path is never displayed as current", mismatch.ReadMetadata() is null);
        AtomicTextFile.Write(mismatch.MetadataPath, "{}");
        check("malformed metadata falls back without throwing", mismatch.ReadMetadata() is null);

        var snapshotsValid = true;
        var writing = Task.Run(() =>
        {
            for (var i = 0; i < 12; i++) store.Commit(i % 2 == 0 ? first : second);
        });
        for (var i = 0; i < 200; i++)
        {
            var metadata = store.ReadMetadata();
            snapshotsValid &= metadata == first || metadata == second;
            if (writing.IsCompleted && i > 50) break;
            await Task.Yield();
        }
        await writing;
        check("concurrent readers only observe complete matching engine records", snapshotsValid);

        var statusOne = Path.Combine(work, "request-one.json");
        var statusTwo = Path.Combine(work, "request-two.json");
        var ok = new EngineProvisioner.EnsureResult(true, "first request", "1.0", "local");
        var busyResult = new EngineProvisioner.EnsureResult(false, "busy request", null, "busy");
        var statusWritten = EngineProvisioner.WriteStatus(ok, statusOne) && EngineProvisioner.WriteStatus(busyResult, statusTwo);
        using var firstStatus = JsonDocument.Parse(AtomicTextFile.Read(statusOne));
        using var secondStatus = JsonDocument.Parse(AtomicTextFile.Read(statusTwo));
        check("independent request status files cannot overwrite each other", statusWritten &&
            firstStatus.RootElement.GetProperty("message").GetString() == "first request" &&
            secondStatus.RootElement.GetProperty("origin").GetString() == "busy");
        check("status write failure is reported", !EngineProvisioner.WriteStatus(ok, work));
        var atomic = Path.Combine(work, "atomic.txt");
        var values = Enumerable.Range(0, 8).Select(i => new string((char)('a' + i), 8192)).ToArray();
        await Task.WhenAll(values.Select(value => Task.Run(() => AtomicTextFile.Write(atomic, value))));
        check("concurrent atomic writes produce one whole value", values.Contains(AtomicTextFile.Read(atomic)));
        check("atomic state writes clean up unique temporary files", Directory.GetFiles(work, "*.tmp", SearchOption.AllDirectories).Length == 0);
    }

    private static Process Start(string root, string ready, string mode)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--engine-lock-fixture", root, ready, mode }) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }
    private static async Task Finish(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
}
