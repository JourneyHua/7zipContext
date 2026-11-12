using System.IO;

namespace ZipContext.App;

internal sealed class EngineBusyException : IOException
{
    public EngineBusyException() : base("另一个引擎操作正在进行，请等待完成后重试。") { }
}

/// <summary>当前用户目录中的跨进程独占句柄；进程退出或崩溃会自动释放。</summary>
internal static class EngineOperationLock
{
    public static IDisposable Acquire(string root)
    {
        Directory.CreateDirectory(root);
        try
        {
            return new FileStream(Path.Combine(root, "engine.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        { throw new EngineBusyException(); }
    }
}
