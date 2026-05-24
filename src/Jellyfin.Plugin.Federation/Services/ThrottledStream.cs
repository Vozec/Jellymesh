using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>Read-side throttle: blocks reader to keep average throughput at or below bytesPerSecond.</summary>
public class ThrottledStream : Stream
{
    private readonly Stream _inner;
    private readonly long _bytesPerSecond;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _totalRead;

    public ThrottledStream(Stream inner, long bytesPerSecond)
    {
        _inner = inner;
        _bytesPerSecond = bytesPerSecond;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Sync path: no Throttle (would block a thread-pool thread on Task.Delay and ignore
        // the caller's cancellation). Throttling applies only on the async path, which is
        // what the proxy controller and Kestrel's response stream use.
        return _inner.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        await Throttle(n, cancellationToken).ConfigureAwait(false);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var n = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        await Throttle(n, cancellationToken).ConfigureAwait(false);
        return n;
    }

    private async Task Throttle(int justRead, CancellationToken ct = default)
    {
        if (_bytesPerSecond <= 0 || justRead <= 0) return;
        _totalRead += justRead;
        var expectedElapsed = TimeSpan.FromSeconds(_totalRead / (double)_bytesPerSecond);
        var actualElapsed = _sw.Elapsed;
        if (expectedElapsed > actualElapsed)
        {
            try { await Task.Delay(expectedElapsed - actualElapsed, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { /* swallow on cancel */ }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
