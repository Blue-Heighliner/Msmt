namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// A TLS 1.3 connection over a socket that reads and writes concurrently and never blocks a thread.
/// BouncyCastle's blocking stream can't do both at once (a write fails while a read is waiting) and
/// ignores cancellation once blocked, so this drives its non-blocking protocol API instead: a read loop
/// feeds received bytes in and collects decrypted data, and a single write loop sends whatever the
/// protocol produces, in the order it was produced, so nothing here ever needs a socket close to be
/// interrupted.
/// </summary>
internal interface IMsmtTlsChannel : IDisposable
{
    /// <summary>Gets the underlying socket, exposed for socket options and diagnostics.</summary>
    Socket Socket { get; }

    /// <summary>Gets a value indicating whether this channel has been closed or aborted.</summary>
    bool IsClosed { get; }

    /// <summary>Gets a task that completes once this channel has closed and both of its loops have finished.</summary>
    Task Completion { get; }

    /// <summary>Performs the TLS handshake as the connecting side, then starts this channel's loops.</summary>
    /// <param name="client">Supplies the TLS configuration and receives the server's identity.</param>
    /// <param name="cancellation">Abandons the handshake, aborting this channel.</param>
    /// <returns>A task that completes once the handshake has finished.</returns>
    Task ConnectAsClient(MsmtTlsClient client, CancellationToken cancellation);

    /// <summary>Performs the TLS handshake as the accepting side, then starts this channel's loops.</summary>
    /// <param name="server">Supplies the TLS configuration and receives the client's identity.</param>
    /// <param name="cancellation">Abandons the handshake, aborting this channel.</param>
    /// <returns>A task that completes once the handshake has finished.</returns>
    Task AcceptAsServer(MsmtTlsServer server, CancellationToken cancellation);

    /// <summary>Reads decrypted application data, waiting until some is available.</summary>
    /// <param name="buffer">Receives the data.</param>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>The number of bytes read, or <c>0</c> once the remote side has closed the connection.</returns>
    /// <exception cref="IOException">The connection failed.</exception>
    ValueTask<int> Read(Memory<byte> buffer, CancellationToken cancellation);

    /// <summary>Encrypts and queues application data for sending, waiting whenever the socket is too far behind to accept more.</summary>
    /// <param name="data">The data to send. Not retained after this call returns.</param>
    /// <param name="cancellation">Cancels waiting to queue; data already queued is still sent.</param>
    /// <returns>A task that completes once all of <paramref name="data"/> has been queued.</returns>
    /// <exception cref="IOException">The connection is closed or failed.</exception>
    Task Write(ReadOnlyMemory<byte> data, CancellationToken cancellation);

    /// <summary>Sends a TLS 1.3 key update, also requesting the peer update its own sending keys. Only available on the connecting side.</summary>
    void Rekey();

    /// <summary>Closes the connection gracefully: sends a TLS close notification, lets everything queued go out, then closes the socket.</summary>
    /// <returns>A task that completes once the connection has closed.</returns>
    Task Close();

    /// <summary>Closes the connection immediately, discarding anything not yet sent.</summary>
    /// <param name="reason">Why it is being aborted, thrown by pending and later reads and writes, or <see langword="null"/> for a plain close.</param>
    void Abort(Exception? reason = null);
}

/// <inheritdoc cref="IMsmtTlsChannel" />
/// <param name="socket">The connected socket to run TLS over. Owned by this channel.</param>
/// <param name="stallTimeout">How long sending one chunk may make no progress before the channel is aborted with a <see cref="TimeoutException"/>, or <see langword="null"/> for no limit.</param>
internal sealed class MsmtTlsChannel(Socket socket, TimeSpan? stallTimeout) : IMsmtTlsChannel
{
    private readonly Lock protocolLock = new();
    // Bounded so a peer sending faster than the application reads can't make this side buffer without limit:
    // once it fills, the read loop stops reading the socket and TCP flow control pushes back on the sender.
    private readonly Channel<byte[]> inbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<OutputChunk> outbound = Channel.CreateUnbounded<OutputChunk>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim credits = new(16);
    private readonly CancellationTokenSource abortSource = new();
    private readonly TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan closeTimeout = TimeSpan.FromSeconds(2);
    private readonly int chunkSize = 16 * 1024;

    private TlsProtocol? protocol;
    private RekeyableTlsClientProtocol? clientProtocol;
    private Task? readLoop;
    private Task? writeLoop;
    private byte[] leftover = [];
    private int leftoverOffset;
    private Exception? fault;
    private int isClosing;
    private int isAborted;

    /// <inheritdoc />
    public Socket Socket => socket;

    /// <inheritdoc />
    public bool IsClosed => Volatile.Read(ref isAborted) != 0;

    /// <inheritdoc />
    public Task Completion => completionSource.Task;

    /// <inheritdoc />
    public async Task ConnectAsClient(MsmtTlsClient client, CancellationToken cancellation)
    {
        RekeyableTlsClientProtocol connecting = new();
        clientProtocol = connecting;
        protocol = connecting;
        connecting.Connect(client);
        await Handshake(cancellation);
    }

    /// <inheritdoc />
    public async Task AcceptAsServer(MsmtTlsServer server, CancellationToken cancellation)
    {
        TlsServerProtocol accepting = new();
        protocol = accepting;
        accepting.Accept(server);
        await Handshake(cancellation);
    }

    /// <inheritdoc />
    public async ValueTask<int> Read(Memory<byte> buffer, CancellationToken cancellation)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (leftoverOffset >= leftover.Length)
        {
            try
            {
                if (!await inbound.Reader.WaitToReadAsync(cancellation))
                {
                    // A waiting reader is told the channel completed without being given the reason it failed.
                    if (fault is not null)
                    {
                        ExceptionDispatchInfo.Capture(fault).Throw();
                    }

                    return 0;
                }
            }
            catch (ChannelClosedException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }

            if (inbound.Reader.TryRead(out byte[]? chunk))
            {
                leftover = chunk;
                leftoverOffset = 0;
            }
        }

        int count = Math.Min(buffer.Length, leftover.Length - leftoverOffset);
        leftover.AsMemory(leftoverOffset, count).CopyTo(buffer);
        leftoverOffset += count;
        return count;
    }

    /// <inheritdoc />
    public async Task Write(ReadOnlyMemory<byte> data, CancellationToken cancellation)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, abortSource.Token);
        ReadOnlyMemory<byte> remaining = data;

        while (remaining.Length > 0)
        {
            ReadOnlyMemory<byte> slice = remaining[..Math.Min(remaining.Length, chunkSize)];
            remaining = remaining[slice.Length..];

            try
            {
                await credits.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (IsClosed)
            {
                throw ClosedException();
            }

            bool queued = false;
            try
            {
                lock (protocolLock)
                {
                    ThrowIfClosed();
                    protocol!.WriteApplicationData(slice.Span);
                    queued = Enqueue(DrainOutput(), releasesCredit: true);
                }
            }
            finally
            {
                if (!queued)
                {
                    credits.Release();
                }
            }
        }
    }

    /// <inheritdoc />
    public void Rekey()
    {
        lock (protocolLock)
        {
            ThrowIfClosed();
            clientProtocol!.Rekey();
            Enqueue(DrainOutput(), releasesCredit: false);
        }
    }

    /// <inheritdoc />
    public async Task Close()
    {
        if (Interlocked.Exchange(ref isClosing, 1) != 0)
        {
            await Completion;
            return;
        }

        try
        {
            lock (protocolLock)
            {
                if (protocol is { IsClosed: false })
                {
                    protocol.Close();
                    Enqueue(DrainOutput(), releasesCredit: false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
        }

        outbound.Writer.TryComplete();

        if (writeLoop is not null)
        {
            await Task.WhenAny(writeLoop, Task.Delay(closeTimeout));
        }

        Abort();
    }

    /// <inheritdoc />
    public void Abort(Exception? reason = null)
    {
        if (Interlocked.Exchange(ref isAborted, 1) != 0)
        {
            return;
        }

        fault = reason;
        abortSource.Cancel();

        try
        {
            socket.Close();
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
        }

        inbound.Writer.TryComplete(reason);
        outbound.Writer.TryComplete();

        if (readLoop is null)
        {
            completionSource.TrySetResult();
        }
    }

    /// <inheritdoc />
    public void Dispose() => Abort();

    private async Task Handshake(CancellationToken cancellation)
    {
        byte[] buffer = new byte[chunkSize + 512];

        try
        {
            while (true)
            {
                List<byte[]> output;
                bool isHandshaking;
                lock (protocolLock)
                {
                    output = DrainOutput();
                    isHandshaking = protocol!.IsHandshaking;
                }

                foreach (byte[] chunk in output)
                {
                    await socket.SendAsync(chunk, SocketFlags.None, cancellation);
                }

                if (!isHandshaking)
                {
                    break;
                }

                int read = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellation);
                if (read == 0)
                {
                    throw new IOException("The remote peer closed the connection during the TLS handshake.");
                }

                lock (protocolLock)
                {
                    protocol!.OfferInput(buffer, 0, read);
                }
            }
        }
        catch (Exception exception)
        {
            Abort(exception);
            throw;
        }

        List<byte[]> early;
        lock (protocolLock)
        {
            early = DrainInput();
        }

        foreach (byte[] data in early)
        {
            await inbound.Writer.WriteAsync(data, CancellationToken.None);
        }

        readLoop = Task.Run(ReadLoop);
        writeLoop = Task.Run(WriteLoop);
        _ = Task.WhenAll(readLoop, writeLoop).ContinueWith(_ => completionSource.TrySetResult(), TaskScheduler.Default);
    }

    private async Task ReadLoop()
    {
        byte[] buffer = new byte[chunkSize + 512];

        try
        {
            while (true)
            {
                int read = await socket.ReceiveAsync(buffer, SocketFlags.None, abortSource.Token);
                if (read == 0)
                {
                    inbound.Writer.TryComplete();
                    return;
                }

                List<byte[]> received;
                bool isClosed;
                try
                {
                    lock (protocolLock)
                    {
                        protocol!.OfferInput(buffer, 0, read);
                        received = DrainInput();
                        Enqueue(DrainOutput(), releasesCredit: false);
                        isClosed = protocol.IsClosed;
                    }
                }
                catch (Exception)
                {
                    // BouncyCastle queues its fatal alert before throwing; flush it best-effort.
                    lock (protocolLock)
                    {
                        Enqueue(DrainOutput(), releasesCredit: false);
                    }

                    throw;
                }

                foreach (byte[] data in received)
                {
                    await inbound.Writer.WriteAsync(data, abortSource.Token);
                }

                if (isClosed)
                {
                    inbound.Writer.TryComplete();
                    return;
                }
            }
        }
        catch (Exception exception) when (!IsClosed || exception is not (OperationCanceledException or ObjectDisposedException or SocketException))
        {
            Abort(exception);
        }
        catch (Exception)
        {
            inbound.Writer.TryComplete();
        }
    }

    private async Task WriteLoop()
    {
        try
        {
            await foreach (OutputChunk chunk in outbound.Reader.ReadAllAsync())
            {
                try
                {
                    using CancellationTokenSource send = CancellationTokenSource.CreateLinkedTokenSource(abortSource.Token);
                    if (stallTimeout is { } limit)
                    {
                        send.CancelAfter(limit);
                    }

                    await socket.SendAsync(chunk.Data, SocketFlags.None, send.Token);
                }
                catch (OperationCanceledException) when (!abortSource.IsCancellationRequested)
                {
                    Abort(new TimeoutException("The remote peer stopped accepting data within the configured stall timeout."));
                    return;
                }
                finally
                {
                    if (chunk.ReleasesCredit)
                    {
                        credits.Release();
                    }
                }
            }

            try
            {
                socket.Shutdown(SocketShutdown.Send);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
            }
        }
        catch (Exception exception) when (!IsClosed)
        {
            Abort(exception);
        }
        catch (Exception)
        {
        }
    }

    private bool Enqueue(List<byte[]> output, bool releasesCredit)
    {
        for (int index = 0; index < output.Count; index++)
        {
            outbound.Writer.TryWrite(new OutputChunk(output[index], releasesCredit && index == output.Count - 1));
        }

        return releasesCredit && output.Count > 0;
    }

    private List<byte[]> DrainOutput()
    {
        List<byte[]> output = [];
        int available;
        while ((available = protocol!.GetAvailableOutputBytes()) > 0)
        {
            byte[] chunk = new byte[available];
            protocol.ReadOutput(chunk, 0, available);
            output.Add(chunk);
        }

        return output;
    }

    private List<byte[]> DrainInput()
    {
        List<byte[]> input = [];
        int available;
        while ((available = protocol!.GetAvailableInputBytes()) > 0)
        {
            byte[] chunk = new byte[available];
            protocol.ReadInput(chunk, 0, available);
            input.Add(chunk);
        }

        return input;
    }

    private void ThrowIfClosed()
    {
        if (IsClosed || protocol!.IsClosed)
        {
            throw ClosedException();
        }
    }

    private IOException ClosedException() => new("The TLS connection is closed.", fault);

    private readonly record struct OutputChunk(byte[] Data, bool ReleasesCredit);
}
