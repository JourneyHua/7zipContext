using System.Text;

namespace ZipContext.Core;

/// <summary>Best effort cross-process log: no lock retry, bounded entry and one backup.</summary>
public static class BoundedLog
{
    public const int MaximumBytes = 1024 * 1024;
    public static void TryAppend(string path, string message)
    {
        try
        {
            var text = message.Length > 4096 ? message[..4096] + " [truncated]\n" : message;
            var bytes = Encoding.UTF8.GetBytes(text);
            using var gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            if (File.Exists(path + ".1") && new FileInfo(path + ".1").Length > MaximumBytes) File.Delete(path + ".1");
            if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaximumBytes)
            {
                File.Delete(path + ".1");
                if (new FileInfo(path).Length > MaximumBytes) File.Delete(path);
                else File.Move(path, path + ".1");
            }
            using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            output.Write(bytes);
        }
        catch { /* Busy or inaccessible diagnostics must not fail a menu operation. */ }
    }
}
