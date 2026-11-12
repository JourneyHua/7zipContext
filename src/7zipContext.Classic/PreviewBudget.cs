using System.Diagnostics;

namespace ZipContext.Classic;

/// <summary>预览安全上限独立于 ini；rootMax=0 也不能关闭这些保护。</summary>
internal sealed class PreviewBudget
{
    public const int TotalMilliseconds = 250;
    public const int ReadMilliseconds = 200; // 给菜单插入留出时间
    public const long MaxArchiveBytes = 512L * 1024 * 1024;
    public const int MaxEntries = 10_000;
    public const int MaxNodes = 20_000;
    public const int MaxLines = 200;
    public const int MaxPathChars = 1024;
    public const int MaxPathDepth = 32;
    public const int MaxNameChars = 1024 * 1024;
    public const int MaxOutputChars = 4 * 1024 * 1024;
    public const int MaxDirectoryBytes = 8 * 1024 * 1024;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _milliseconds;
    public CancellationToken Token { get; }

    public PreviewBudget(int milliseconds, CancellationToken token)
        => (_milliseconds, Token) = (milliseconds, token);

    public void Check()
    {
        Token.ThrowIfCancellationRequested();
        if (_clock.ElapsedMilliseconds >= _milliseconds) throw new OperationCanceledException();
    }
}

internal sealed class PreviewLimitException : Exception { }

/// <summary>ZIP 元数据读取也计入预算，防伪造目录大小绕过预检；所有 IO 均在受控工作线程。</summary>
internal sealed class PreviewReadStream : Stream
{
    private readonly Stream _inner;
    private readonly PreviewBudget _budget;
    private long _readBytes;

    public PreviewReadStream(Stream inner, PreviewBudget budget) => (_inner, _budget) = (inner, budget);
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        _budget.Check();
        // 两倍目录预算包括 ZIP 头尾重读，仍有明确的累计读取上限。
        var remaining = 2L * PreviewBudget.MaxDirectoryBytes - _readBytes;
        if (remaining <= 0) throw new PreviewLimitException();
        var read = _inner.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
        _readBytes += read;
        _budget.Check();
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        _budget.Check();
        return _inner.Seek(offset, origin);
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
