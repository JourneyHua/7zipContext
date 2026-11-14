using System.Diagnostics;
using System.Xml.Serialization;
using ZipContext.Deployment;

internal static class DeploymentChecks
{
    internal static async Task<int> Child(string[] args)
    {
        try
        {
            using var lease = DeploymentLease.Acquire(args[1], args[4] == "-" ? null : args[4]);
            File.WriteAllText(args[2], lease.Token);
            if (args[3] == "hold") await Task.Delay(TimeSpan.FromMinutes(2));
            return 0;
        }
        catch (DeploymentBusyException) { File.WriteAllText(args[2], "busy"); return 23; }
    }

    internal static async Task Run(Action<string, bool> check, string root)
    {
        Directory.CreateDirectory(root);
        var locks = Path.Combine(root, "locks");
        var owner = DeploymentLease.Acquire(locks, null);
        var busy = false;
        try { using var second = DeploymentLease.Acquire(locks, null); }
        catch (DeploymentBusyException) { busy = true; }
        check("deployment lease blocks independent installer", busy);
        using (var child = Start(locks, Path.Combine(root, "nested"), "probe", owner.Token))
        {
            await Finish(child);
            check("deployment lease permits its delegated child task", child.ExitCode == 0);
        }
        using (var child = Start(locks, Path.Combine(root, "competing"), "probe", "-"))
        {
            await Finish(child);
            check("deployment lease blocks competing process", child.ExitCode == 23);
        }
        using (var child = Start(locks, Path.Combine(root, "wrong-token"), "probe", Guid.NewGuid().ToString("N")))
        {
            await Finish(child);
            check("wrong deployment token cannot join owner", child.ExitCode == 23);
        }
        var ready = Path.Combine(root, "borrower-ready");
        using (var borrower = Start(locks, ready, "hold", owner.Token))
        {
            try
            {
                await Ready(borrower, ready);
                check("delegated worker holds a real group handle", File.Exists(ready) && File.ReadAllText(ready) == owner.Token);
                owner.Dispose();
                busy = false;
                try { using var next = DeploymentLease.Acquire(locks, null); }
                catch (DeploymentBusyException) { busy = true; }
                check("active child excludes next installer after parent releases lease", busy);
            }
            finally
            {
                owner.Dispose();
                if (!borrower.HasExited) borrower.Kill(entireProcessTree: true);
                await borrower.WaitForExitAsync();
            }
        }
        var recovered = false;
        var clock = Stopwatch.StartNew();
        while (!recovered && clock.ElapsedMilliseconds < 5000)
        {
            try { using var next = DeploymentLease.Acquire(locks, null); recovered = true; }
            catch (DeploymentBusyException) { await Task.Delay(20); }
        }
        check("deployment group releases after final process exits", recovered);

        (string Source, string Target, string Work) Fixture(string name)
        {
            var group = Path.Combine(root, name);
            var source = Path.Combine(group, "candidate");
            var target = Path.Combine(group, "registered-root");
            Directory.CreateDirectory(source); Directory.CreateDirectory(target);
            foreach (var entry in new[] { "a.txt", "b.txt" })
            {
                File.WriteAllText(Path.Combine(source, entry), "new-" + entry);
                File.WriteAllText(Path.Combine(target, entry), "old-" + entry);
            }
            File.WriteAllText(Path.Combine(source, "added.txt"), "new-only");
            File.WriteAllText(Path.Combine(source, "7zipContext.ini"), "default");
            File.WriteAllText(Path.Combine(target, "7zipContext.ini"), "custom");
            File.WriteAllText(Path.Combine(target, "obsolete.txt"), "old-only");
            return (source, target, Path.Combine(group, "transaction"));
        }
        FileDeployment Prepare((string Source, string Target, string Work) f) =>
            FileDeployment.Prepare(f.Source, f.Target, f.Work, new[] { "7zipContext.ini" }, true);
        bool OldFiles((string Source, string Target, string Work) f) =>
            File.ReadAllText(Path.Combine(f.Target, "a.txt")) == "old-a.txt" &&
            File.ReadAllText(Path.Combine(f.Target, "b.txt")) == "old-b.txt" &&
            File.Exists(Path.Combine(f.Target, "obsolete.txt")) && !File.Exists(Path.Combine(f.Target, "added.txt"));

        var first = Fixture("success-and-rollback");
        var creation = Directory.GetCreationTimeUtc(first.Target);
        var transaction = Prepare(first);
        check("deployment preparation leaves existing files untouched", OldFiles(first) && File.Exists(transaction.JournalPath));
        File.WriteAllText(Path.Combine(first.Target, "7zipContext.ini"), "changed-after-prepare");
        transaction.Apply();
        check("deployment applies new files and removes obsolete files", File.ReadAllText(Path.Combine(first.Target, "a.txt")) == "new-a.txt" &&
            File.Exists(Path.Combine(first.Target, "added.txt")) && !File.Exists(Path.Combine(first.Target, "obsolete.txt")));
        check("deployment preserves latest user configuration", File.ReadAllText(Path.Combine(first.Target, "7zipContext.ini")) == "changed-after-prepare");
        check("registered target directory retains its identity", Directory.GetCreationTimeUtc(first.Target) == creation);
        transaction.Rollback(); // Simulates registration/self-check failure after file switch.
        check("post-switch failure restores original files and removes new ones", OldFiles(first) && transaction.Journal.State == "rolledBack");
        transaction.Cleanup();
        check("rolled-back transaction can clean up backups", !Directory.Exists(first.Work));

        var copyFailure = Fixture("copy-failure");
        transaction = Prepare(copyFailure);
        File.Delete(Path.Combine(copyFailure.Source, "b.txt"));
        var failed = false;
        try { transaction.Apply(); } catch (IOException) { failed = true; }
        check("partial copy failure automatically restores old files", failed && OldFiles(copyFailure) && transaction.Journal.State == "rolledBack");
        transaction.Cleanup();

        var interrupted = Fixture("interrupted");
        transaction = Prepare(interrupted);
        File.WriteAllText(Path.Combine(interrupted.Target, "a.txt"), "partially-switched");
        transaction.Journal.State = "applying";
        Save(transaction);
        var loaded = FileDeployment.Load(interrupted.Work, interrupted.Target);
        loaded.Rollback();
        check("persisted interrupted switch can be restored in a new instance", OldFiles(interrupted) && loaded.Journal.State == "rolledBack");
        loaded.Cleanup();

        var recoveryFailure = Fixture("retry-recovery");
        transaction = Prepare(recoveryFailure); transaction.Apply();
        using (var blocker = new FileStream(Path.Combine(recoveryFailure.Target, "a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            failed = false;
            try { transaction.Rollback(); } catch (AggregateException) { failed = true; }
            check("incomplete rollback retains journal and backup", failed && transaction.Journal.State == "rollbackFailed" &&
                File.Exists(transaction.JournalPath) && File.Exists(Path.Combine(recoveryFailure.Work, "backup", "a.txt")));
        }
        loaded = FileDeployment.Load(recoveryFailure.Work, recoveryFailure.Target); loaded.Rollback();
        check("rollback can resume after file lock is released", OldFiles(recoveryFailure));
        loaded.Cleanup();

        var committed = Fixture("commit");
        transaction = Prepare(committed); transaction.Apply();
        check("successful commit cleans backups after switch", transaction.Commit() is null && !Directory.Exists(committed.Work) &&
            File.ReadAllText(Path.Combine(committed.Target, "a.txt")) == "new-a.txt");
        failed = false; try { transaction.Rollback(); } catch (IOException) { failed = true; }
        check("committed transaction cannot restore obsolete files", failed);

        var cleanupFailure = Fixture("cleanup-warning");
        transaction = Prepare(cleanupFailure); transaction.Apply();
        using (var blocker = new FileStream(Path.Combine(cleanupFailure.Work, "backup", "a.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var warning = transaction.Commit();
            check("cleanup failure is a warning after committed installation", !string.IsNullOrEmpty(warning) && transaction.Journal.State == "committed" &&
                File.ReadAllText(Path.Combine(cleanupFailure.Target, "a.txt")) == "new-a.txt");
        }
        transaction.Cleanup();

        var invalid = Fixture("invalid-journal");
        transaction = Prepare(invalid);
        failed = false;
        try { FileDeployment.Load(invalid.Work, Path.Combine(root, "other-target")); } catch (IOException) { failed = true; }
        check("recovery refuses a mismatched target root", failed && OldFiles(invalid));
        transaction.Journal.Entries[0].Relative = "../outside.txt"; Save(transaction);
        failed = false; try { FileDeployment.Load(invalid.Work, invalid.Target); } catch (IOException) { failed = true; }
        check("recovery rejects path traversal before changing target", failed && OldFiles(invalid));
        failed = false;
        try { FileDeployment.Prepare(invalid.Source, Path.Combine(invalid.Source, "nested"), Path.Combine(root, "overlap"), Array.Empty<string>(), true); }
        catch (IOException) { failed = true; }
        check("deployment refuses overlapping source and target", failed);

        var freshSource = Path.Combine(root, "fresh-source"); Directory.CreateDirectory(freshSource);
        File.WriteAllText(Path.Combine(freshSource, "new.txt"), "fresh");
        var freshTarget = Path.Combine(root, "fresh-target");
        transaction = FileDeployment.Prepare(freshSource, freshTarget, Path.Combine(root, "fresh-work"), Array.Empty<string>(), false);
        transaction.Apply(); transaction.Rollback();
        check("fresh-install rollback leaves no program files", Directory.Exists(freshTarget) && Directory.GetFiles(freshTarget).Length == 0);
        transaction.Cleanup();

        // 事务日志自身必须能在「不支持 ReplaceFile 的卷」上保存：不能出现残留的改名文件。
        var journal = Fixture("journal-replace");
        transaction = Prepare(journal);
        transaction.Apply();   // 内部会多次改写 transaction.xml
        transaction.Rollback();
        var journalDir = Path.GetDirectoryName(transaction.JournalPath)!;
        check("journal rewrite leaves no stray files", Directory.GetFiles(journalDir, "transaction.xml*").Length == 1 &&
            File.Exists(transaction.JournalPath) && FileDeployment.Load(journal.Work, journal.Target).Journal.State == "rolledBack");
        transaction.Cleanup();
    }

    private static void Save(FileDeployment transaction)
    {
        using var file = File.Create(transaction.JournalPath);
        new XmlSerializer(typeof(DeploymentJournal)).Serialize(file, transaction.Journal);
    }
    private static Process Start(string root, string ready, string mode, string token)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--deployment-fixture", root, ready, mode, token }) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }
    private static async Task Ready(Process process, string ready)
    {
        var clock = Stopwatch.StartNew();
        while (!File.Exists(ready) && !process.HasExited && clock.ElapsedMilliseconds < 10000) await Task.Delay(20);
    }
    private static async Task Finish(Process process)
    {
        using var timeout = new CancellationTokenSource(10000);
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
}
