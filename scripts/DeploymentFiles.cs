#nullable disable
// Shared by .NET 8 and Windows PowerShell 5.1 Add-Type (the loader removes #nullable).
// Keep the implementation compatible with C# 5 / .NET Framework 4.x.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace ZipContext.Deployment
{
    public sealed class DeploymentBusyException : IOException
    {
        public DeploymentBusyException() : base("安装、卸载、发布或引擎操作正在进行，请稍后重试。") { }
    }

    // Owner election and the shared group handle are separate: child handles continue to
    // exclude a new owner even if the original parent process terminates unexpectedly.
    public sealed class DeploymentLease : IDisposable
    {
        public const string EnvironmentName = "ZIPCONTEXT_DEPLOYMENT_TOKEN";
        private FileStream _owner;
        private FileStream _member;
        public string Token { get; private set; }
        private DeploymentLease(string token, FileStream owner, FileStream member)
        { Token = token; _owner = owner; _member = member; }

        public static DeploymentLease Acquire(string root, string inheritedToken)
        {
            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);
            var tokenFile = Path.Combine(root, "deployment.token");
            var groupFile = Path.Combine(root, "deployment.lease");
            if (!String.IsNullOrEmpty(inheritedToken))
            {
                FileStream member = null;
                try
                {
                    if (ReadToken(tokenFile) == inheritedToken)
                    {
                        member = new FileStream(groupFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        if (ReadToken(tokenFile) == inheritedToken)
                        {
                            var joined = new DeploymentLease(inheritedToken, null, member);
                            member = null;
                            return joined;
                        }
                    }
                }
                catch (IOException) { }
                finally { if (member != null) member.Dispose(); }
            }
            FileStream owner = null;
            try
            {
                owner = new FileStream(Path.Combine(root, "deployment.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                var token = Guid.NewGuid().ToString("N");
                using (var probe = new FileStream(groupFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    File.WriteAllText(tokenFile, token, new UTF8Encoding(false));
                var group = new FileStream(groupFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                return new DeploymentLease(token, owner, group);
            }
            catch (IOException ex)
            {
                if (owner != null) owner.Dispose();
                if ((ex.HResult & 0xffff) == 32 || (ex.HResult & 0xffff) == 33) throw new DeploymentBusyException();
                throw;
            }
            catch { if (owner != null) owner.Dispose(); throw; }
        }

        private static string ReadToken(string path)
        {
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(file)) return reader.ReadToEnd().Trim();
            }
            catch { return ""; }
        }
        public void Dispose()
        {
            if (_member != null) { _member.Dispose(); _member = null; }
            if (_owner != null) { _owner.Dispose(); _owner = null; }
        }
    }

    public sealed class DeploymentEntry
    {
        public string Relative = "";
        public bool HadOriginal;
        public bool Remove;
        public int Attributes;
    }
    public sealed class DeploymentJournal
    {
        public string Source = "";
        public string Target = "";
        public string Work = "";
        public string State = "prepared";
        public List<DeploymentEntry> Entries = new List<DeploymentEntry>();
    }

    /// <summary>Stable target root; backup before mutation; durable journal for exception/crash recovery.</summary>
    public sealed class FileDeployment
    {
        public DeploymentJournal Journal { get; private set; }
        public string JournalPath { get { return Path.Combine(Journal.Work, "transaction.xml"); } }
        private FileDeployment(DeploymentJournal journal) { Journal = journal; }

        public static FileDeployment Prepare(string source, string target, string work,
            string[] preserveExisting, bool removeObsolete)
        {
            return Prepare(source, target, work, preserveExisting, removeObsolete, null);
        }

        public static FileDeployment Prepare(string source, string target, string work,
            string[] preserveExisting, bool removeObsolete, string[] removeExisting)
        {
            source = Full(source); target = Full(target); work = Full(work);
            if (Same(source, Path.GetPathRoot(source)) || Same(target, Path.GetPathRoot(target)) || Same(work, Path.GetPathRoot(work)))
                throw new IOException("事务目录不能为磁盘根目录。 ");
            if (Overlaps(source, target) || Overlaps(target, work) || Overlaps(source, work))
                throw new IOException("准备、目标和备份目录必须彼此独立。 ");
            if (Directory.Exists(work)) throw new IOException("备份目录已存在，不能覆盖未完成的事务。 ");
            var sources = Files(source);
            var originals = Directory.Exists(target) ? Files(target) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var preserved = new HashSet<string>(preserveExisting ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var journal = new DeploymentJournal { Source = source, Target = target, Work = work };
            var names = new HashSet<string>(sources.Keys, StringComparer.OrdinalIgnoreCase);
            if (removeObsolete) names.UnionWith(originals.Keys);
            foreach (var relative in removeExisting ?? new string[0])
            {
                Child(target, relative); // Validate before creating backups or changing files.
                if (originals.ContainsKey(relative) && !sources.ContainsKey(relative)) names.Add(relative);
            }
            var ordered = new List<string>(names); ordered.Sort(StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(work);
            foreach (var relative in ordered)
            {
                if (preserved.Contains(relative) && originals.ContainsKey(relative)) continue;
                var entry = new DeploymentEntry { Relative = relative, HadOriginal = originals.ContainsKey(relative), Remove = !sources.ContainsKey(relative) };
                if (entry.HadOriginal)
                {
                    var original = originals[relative];
                    entry.Attributes = (int)File.GetAttributes(original);
                    var backup = Child(Path.Combine(work, "backup"), relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    File.Copy(original, backup, false);
                }
                journal.Entries.Add(entry);
            }
            var transaction = new FileDeployment(journal);
            transaction.Save();
            return transaction;
        }

        public static FileDeployment Load(string work, string expectedTarget)
        {
            work = Full(work);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            DeploymentJournal journal;
            using (var reader = XmlReader.Create(Path.Combine(work, "transaction.xml"), settings))
                journal = (DeploymentJournal)new XmlSerializer(typeof(DeploymentJournal)).Deserialize(reader);
            if (!Same(work, journal.Work) || !Same(expectedTarget, journal.Target) || Overlaps(journal.Target, work))
                throw new IOException("恢复日志目录与预期安装目标不匹配。 ");
            if (Array.IndexOf(new[] { "prepared", "applying", "applied", "rollingBack", "rolledBack", "rollbackFailed", "committed" }, journal.State) < 0)
                throw new IOException("恢复日志状态无效，未修改目标文件。 ");
            foreach (var entry in journal.Entries)
            {
                Child(journal.Target, entry.Relative);
                Child(Path.Combine(work, "backup"), entry.Relative);
            }
            return new FileDeployment(journal);
        }

        public void Apply()
        {
            if (Journal.State != "prepared") throw new IOException("此文件事务不能重复执行。 ");
            Journal.State = "applying"; Save();
            try
            {
                if (Directory.Exists(Journal.Target)) Files(Journal.Target);
                Files(Journal.Source);
                Directory.CreateDirectory(Journal.Target); // never rename/delete the registered AppX root
                foreach (var entry in Journal.Entries)
                {
                    var target = Child(Journal.Target, entry.Relative);
                    if (entry.Remove) { if (File.Exists(target)) File.Delete(target); }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(Child(Journal.Source, entry.Relative), target, true);
                    }
                }
                Journal.State = "applied"; Save();
            }
            catch (Exception original)
            {
                try { Rollback(); }
                catch (Exception recovery)
                { throw new IOException("文件切换失败，恢复也未完成；备份保留在 " + Journal.Work,
                    new AggregateException(original, recovery)); }
                throw new IOException("文件切换失败，旧文件已恢复：" + original.Message, original);
            }
        }

        public void Rollback()
        {
            if (Journal.State == "committed") throw new IOException("已提交的事务不能回滚。 ");
            var errors = new List<Exception>();
            // prepared never modified the target; do not touch its files during recovery.
            if (Journal.State != "prepared" && Journal.State != "rolledBack")
            {
                if (Directory.Exists(Journal.Target)) Files(Journal.Target);
                Files(Journal.Work);
                Journal.State = "rollingBack"; Save();
                for (var i = Journal.Entries.Count - 1; i >= 0; i--)
                {
                    var entry = Journal.Entries[i];
                    try
                    {
                        var target = Child(Journal.Target, entry.Relative);
                        if (entry.HadOriginal)
                        {
                            var backup = Child(Path.Combine(Journal.Work, "backup"), entry.Relative);
                            if (!File.Exists(backup)) throw new IOException("缺少备份：" + entry.Relative);
                            if (!EqualFiles(target, backup))
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(target));
                                if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                                File.Copy(backup, target, true);
                            }
                            File.SetAttributes(target, (FileAttributes)entry.Attributes);
                        }
                        else if (File.Exists(target)) { File.SetAttributes(target, FileAttributes.Normal); File.Delete(target); }
                    }
                    catch (Exception ex) { errors.Add(ex); }
                }
            }
            Journal.State = errors.Count == 0 ? "rolledBack" : "rollbackFailed"; Save();
            if (errors.Count != 0) throw new AggregateException("部分文件恢复失败，保留备份。", errors);
        }

        public string Commit()
        {
            if (Journal.State != "applied") throw new IOException("只能提交已完成文件切换的事务。 ");
            Journal.State = "committed";
            try { Save(); } catch { Journal.State = "applied"; throw; }
            try { Cleanup(); return null; }
            catch (Exception ex) { return "安装已提交，但备份清理失败：" + Journal.Work + "；" + ex.Message; }
        }
        public void Cleanup()
        {
            if (Journal.State != "committed" && Journal.State != "rolledBack") throw new IOException("未完成事务的备份不能删除。 ");
            DeleteOwnedTree(Journal.Work);
        }

        public static void CopyTree(string source, string target, bool preserveIni)
        {
            source = Full(source); target = Full(target);
            if (Same(source, target)) return;
            if (Same(source, Path.GetPathRoot(source)) || Same(target, Path.GetPathRoot(target)))
                throw new IOException("复制目录不能为磁盘根目录。 ");
            if (Overlaps(source, target)) throw new IOException("复制目录不能互相包含。 ");
            foreach (var pair in Files(source))
            {
                var destination = Child(target, pair.Key);
                if (preserveIni && Path.GetFileName(destination).Equals("7zipContext.ini", StringComparison.OrdinalIgnoreCase) && File.Exists(destination)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(pair.Value, destination, true);
            }
        }

        public static void DeleteOwnedTree(string directory)
        {
            directory = Full(directory);
            if (Same(directory, Path.GetPathRoot(directory))) throw new IOException("不能删除磁盘根目录。 ");
            if (!Directory.Exists(directory)) return;
            var files = Files(directory); // rejects reparse points before recursive deletion
            foreach (var file in files.Values) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }

        private void Save()
        {
            var temporary = JournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { new XmlSerializer(typeof(DeploymentJournal)).Serialize(file, Journal); file.Flush(true); }
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(JournalPath)) File.Replace(temporary, JournalPath, null);
                        else File.Move(temporary, JournalPath);
                        break;
                    }
                    catch (IOException) { if (attempt == 4) throw; Thread.Sleep(20); }
                }
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
        private static bool EqualFiles(string first, string second)
        {
            if (!File.Exists(first) || new FileInfo(first).Length != new FileInfo(second).Length) return false;
            using (var hash = SHA256.Create())
            using (var a = File.OpenRead(first))
            using (var b = File.OpenRead(second))
                return Convert.ToBase64String(hash.ComputeHash(a)) == Convert.ToBase64String(hash.ComputeHash(b));
        }
        private static Dictionary<string, string> Files(string root)
        {
            RejectLink(root);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>(); pending.Enqueue(root);
            while (pending.Count != 0)
            {
                var directory = pending.Dequeue();
                foreach (var path in Directory.GetFiles(directory)) { RejectLink(path); result.Add(path.Substring(root.Length + 1), path); }
                foreach (var path in Directory.GetDirectories(directory)) { RejectLink(path); pending.Enqueue(path); }
            }
            return result;
        }
        private static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("事务目录不支持链接或重解析点：" + path);
        }
        private static string Full(string path)
        {
            var full = Path.GetFullPath(path);
            return full.Length > Path.GetPathRoot(full).Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }
        private static bool Same(string a, string b) { return String.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase); }
        private static bool Overlaps(string a, string b) { return Same(a, b) || Full(a).StartsWith(Full(b) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Full(b).StartsWith(Full(a) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        private static string Child(string root, string relative)
        {
            foreach (var part in relative.Split(new[] { '\\', '/' }))
                if (part == "." || part == ".." || part.IndexOf(':') >= 0) throw new IOException("恢复日志包含无效相对路径。 ");
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (String.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || !path.StartsWith(Full(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("文件路径越出事务目录。 ");
            return path;
        }
    }
}
