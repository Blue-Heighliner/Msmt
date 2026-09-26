namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Wraps a connection's raw transport stream, beneath TLS, reporting every completed read or write to an
/// <see cref="IMsmtWatchdog"/> so a stall timeout measures time without any bytes moving rather than a
/// phase's total duration.
/// </summary>
/// <param name="inner">The transport stream to wrap and dispose along with this stream.</param>
/// <param name="watchdog">Notified of progress.</param>
internal sealed class MsmtWatchedStream(Stream inner, IMsmtWatchdog watchdog) : Stream
{
    /// <inheritdoc />
    public override bool CanRead => inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => inner.CanWrite;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        NotifyIfProgressed(read);
        return read;
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        NotifyIfProgressed(read);
        return read;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken);
        NotifyIfProgressed(read);
        return read;
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        watchdog.NotifyProgress();
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        watchdog.NotifyProgress();
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        watchdog.NotifyProgress();
    }

    /// <inheritdoc />
    public override void Flush() => inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void NotifyIfProgressed(int read)
    {
        if (read > 0)
        {
            watchdog.NotifyProgress();
        }
    }
}
