using System.Text;

namespace ZipContext.Core;

/// <summary>同目录独占临时文件，写完后替换；并发写入不共用 .tmp 名称。</summary>
public static class AtomicTextFile
{
    public static string Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static void Write(string path, string text)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(file, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(text);
                writer.Flush();
                file.Flush(flushToDisk: true);
            }
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, path, overwrite: true); break; }
                catch (Exception ex) when (attempt < 7 &&
                    ex is IOException or UnauthorizedAccessException &&
                    (ex.HResult & 0xffff) is 5 or 32 or 33 && !Directory.Exists(path))
                {
                    // Windows 索引器/扫描器或旧版读取器可短暂拒绝替换；不删除目标文件。
                    Thread.Sleep(10 * (attempt + 1));
                }
            }
        }
        finally { try { File.Delete(temporary); } catch { } }
    }
}
