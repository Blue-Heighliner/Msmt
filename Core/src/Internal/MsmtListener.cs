namespace BlueHeighliner.Msmt.Internal;

/// <summary>
/// Accepts TCP connections and hands each one to a handler running on its own task, so one client's slow
/// or stalled handshake never delays accepting the next.
/// </summary>
internal interface IMsmtListener : IAsyncDisposable, IDisposable
{
    /// <summary>Gets the endpoint actually bound, resolving any requested ephemeral port.</summary>
    IPEndPoint LocalEndPoint { get; }

    /// <summary>Binds the listening socket and starts accepting in the background.</summary>
    /// <param name="host">The local IP address or DNS hostname to listen on.</param>
    /// <param name="port">The local port to listen on.</param>
    /// <param name="onAccepted">Handles one accepted socket, which it owns.</param>
    /// <param name="onError">Given an exception that did not stop the listener, such as one client's failed accept or a handler that threw.</param>
    /// <param name="handlerCancellation">Given to every handler, so shutting down the owner abandons handshakes still in progress.</param>
    /// <exception cref="SocketException">The listening socket could not be bound.</exception>
    void Start(string host, int port, Func<Socket, CancellationToken, Task> onAccepted, Action<Exception> onError, CancellationToken handlerCancellation);

    /// <summary>Stops accepting, leaving handlers already running to finish.</summary>
    void Stop();
}

/// <inheritdoc cref="IMsmtListener" />
internal sealed class MsmtListener : IMsmtListener
{
    private readonly CancellationTokenSource stopSource = new();
    private readonly List<Task> handlers = [];
    private readonly Lock handlersLock = new();
    private readonly TimeSpan acceptRetryDelay = TimeSpan.FromMilliseconds(100);

    private TcpListener? listener;
    private Task? acceptLoop;

    /// <inheritdoc />
    public IPEndPoint LocalEndPoint => (IPEndPoint)listener!.LocalEndpoint;

    /// <inheritdoc />
    public void Start(string host, int port, Func<Socket, CancellationToken, Task> onAccepted, Action<Exception> onError, CancellationToken handlerCancellation)
    {
        listener = new TcpListener(MsmtProtocol.ResolveAddress(host), port);
        listener.Start();
        acceptLoop = Task.Run(() => AcceptLoop(listener, onAccepted, onError, handlerCancellation));
    }

    /// <inheritdoc />
    public void Stop()
    {
        stopSource.Cancel();
        listener?.Stop();
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    /// <summary>Stops accepting and waits for every handler to finish.</summary>
    /// <returns>A task that completes once no handler is running.</returns>
    public async ValueTask DisposeAsync()
    {
        Stop();

        if (acceptLoop is not null)
        {
            await acceptLoop;
        }

        Task[] running;
        lock (handlersLock)
        {
            running = [.. handlers];
        }

        await Task.WhenAll(running);
    }

    private async Task AcceptLoop(TcpListener accepting, Func<Socket, CancellationToken, Task> onAccepted, Action<Exception> onError, CancellationToken handlerCancellation)
    {
        CancellationToken stop = stopSource.Token;

        while (!stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await accepting.AcceptSocketAsync(stop);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException || stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException exception)
            {
                onError(exception);

                // One client's failed accept (e.g. reset before it was accepted) must not stop the listener for
                // every other client; the delay keeps a persistent failure (e.g. out of file descriptors) from spinning.
                try
                {
                    await Task.Delay(acceptRetryDelay, stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            Task handler = Task.Run(() => RunHandler(socket, onAccepted, onError, handlerCancellation), CancellationToken.None);
            lock (handlersLock)
            {
                handlers.Add(handler);
            }

            _ = handler.ContinueWith(RemoveHandler, TaskScheduler.Default);
        }
    }

    private async Task RunHandler(Socket socket, Func<Socket, CancellationToken, Task> onAccepted, Action<Exception> onError, CancellationToken handlerCancellation)
    {
        try
        {
            await onAccepted(socket, handlerCancellation);
        }
        catch (Exception failure)
        {
            // Reporting a failed connection is the handler's job; one that throws anyway must not fault the
            // task the listener waits on at shutdown, or take down a listener serving other clients.
            onError(failure);

            try
            {
                socket.Close();
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
            }
        }
    }

    private void RemoveHandler(Task handler)
    {
        lock (handlersLock)
        {
            handlers.Remove(handler);
        }
    }
}
