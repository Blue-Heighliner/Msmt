namespace BlueHeighliner.Msmt;

/// <summary>
/// Sends the acknowledgement for a single message received via <see cref="IMsmtPeer.Received"/>, if the
/// sender requested one (see <see cref="MsmtReceivedEventArgs.IsResponseRequested"/>).
/// </summary>
public interface IMsmtResponder
{
    /// <summary>
    /// Gets the decision made so far, or <see cref="MsmtResponseKind.None"/> if neither <see
    /// cref="Accept()"/> nor <see cref="Reject()"/> has been called yet.
    /// </summary>
    MsmtResponseKind Response { get; }

    /// <summary>Sends a positive acknowledgement with an empty payload.</summary>
    /// <exception cref="InvalidOperationException"><see cref="MsmtReceivedEventArgs.IsResponseRequested"/> was <see langword="false"/>, or a response was already sent.</exception>
    void Accept();

    /// <summary>Sends a positive acknowledgement carrying <paramref name="payload"/>.</summary>
    /// <param name="payload">
    /// The acknowledgement payload, rented from a pool. Ownership transfers to this responder, which
    /// disposes it once the acknowledgement has been sent.
    /// </param>
    /// <exception cref="InvalidOperationException"><see cref="MsmtReceivedEventArgs.IsResponseRequested"/> was <see langword="false"/>, or a response was already sent.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    void Accept(IMemoryOwner<byte> payload);

    /// <summary>Sends a negative acknowledgement with an empty payload.</summary>
    /// <exception cref="InvalidOperationException"><see cref="MsmtReceivedEventArgs.IsResponseRequested"/> was <see langword="false"/>, or a response was already sent.</exception>
    void Reject();

    /// <summary>Sends a negative acknowledgement carrying <paramref name="payload"/>.</summary>
    /// <param name="payload">
    /// The acknowledgement payload, rented from a pool. Ownership transfers to this responder, which
    /// disposes it once the acknowledgement has been sent.
    /// </param>
    /// <exception cref="InvalidOperationException"><see cref="MsmtReceivedEventArgs.IsResponseRequested"/> was <see langword="false"/>, or a response was already sent.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
    void Reject(IMemoryOwner<byte> payload);

    /// <summary>
    /// Suppresses the automatic acceptance that would otherwise occur once every <see
    /// cref="IMsmtPeer.Received"/> subscriber has run without deciding. After this is called, no message
    /// on the same connection is acknowledged - or read - until <see cref="Accept()"/> or <see
    /// cref="Reject()"/> (or their payload-carrying overloads) is eventually called, whether by the
    /// subscriber that called this before it returns, or by anything else holding this responder
    /// afterward.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="MsmtReceivedEventArgs.IsResponseRequested"/> was <see langword="false"/>, or a response was already sent.</exception>
    void Defer();
}

/// <inheritdoc cref="IMsmtResponder" />
internal sealed class MsmtResponder(bool isResponseRequested) : IMsmtResponder
{
    private readonly TaskCompletionSource decidedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public MsmtResponseKind Response { get; private set; }

    /// <summary>Gets the payload to send with the acknowledgement, once <see cref="Response"/> is no longer <see cref="MsmtResponseKind.None"/>.</summary>
    internal IMemoryOwner<byte> Payload { get; private set; } = EmptyMemoryOwner.Instance;

    /// <summary>Gets a value indicating whether <see cref="Defer"/> has been called, suppressing automatic acceptance.</summary>
    internal bool IsDeferred { get; private set; }

    /// <inheritdoc />
    public void Accept() => Accept(EmptyMemoryOwner.Instance);

    /// <inheritdoc />
    public void Accept(IMemoryOwner<byte> payload) => Decide(MsmtResponseKind.Accept, payload);

    /// <inheritdoc />
    public void Reject() => Reject(EmptyMemoryOwner.Instance);

    /// <inheritdoc />
    public void Reject(IMemoryOwner<byte> payload) => Decide(MsmtResponseKind.Reject, payload);

    /// <inheritdoc />
    public void Defer()
    {
        EnsureCanDecide();
        IsDeferred = true;
    }

    /// <summary>Asynchronously waits until <see cref="Accept()"/> or <see cref="Reject()"/> is called.</summary>
    /// <param name="cancellation">Cancels the wait.</param>
    /// <returns>A task that completes once this responder has been decided.</returns>
    internal Task WaitForDecision(CancellationToken cancellation) => decidedSource.Task.WaitAsync(cancellation);

    private void Decide(MsmtResponseKind response, IMemoryOwner<byte> payload)
    {
        EnsureCanDecide();
        MsmtProtocol.ValidatePayloadLength(payload.Memory.Length, nameof(payload));
        Response = response;
        Payload = payload;
        decidedSource.SetResult();
    }

    private void EnsureCanDecide()
    {
        if (!isResponseRequested)
        {
            throw new InvalidOperationException("The sender did not request a response for this message.");
        }

        if (Response != MsmtResponseKind.None)
        {
            throw new InvalidOperationException("A response has already been sent for this message.");
        }
    }
}

/// <summary>
/// Extension members for <see cref="IMsmtResponder"/>.
/// </summary>
public static class MsmtResponderExtensions
{
    extension(IMsmtResponder responder)
    {
        /// <summary>
        /// Sends a positive acknowledgement carrying <paramref name="payload"/>, wrapping it in a non-pooled
        /// <see cref="IMemoryOwner{T}"/> so callers with an ordinary <see cref="ReadOnlyMemory{T}"/> don't
        /// need to manage one themselves.
        /// </summary>
        /// <param name="payload">The acknowledgement payload. Not copied - the caller must not mutate it until the acknowledgement has been sent.</param>
        /// <exception cref="InvalidOperationException"><see cref="MsmtReceivedEventArgs.IsResponseRequested"/> was <see langword="false"/>, or a response was already sent.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
        public void Accept(ReadOnlyMemory<byte> payload) => responder.Accept(new NonOwningMemoryOwner(payload));

        /// <summary>
        /// Sends a negative acknowledgement carrying <paramref name="payload"/>, wrapping it in a non-pooled
        /// <see cref="IMemoryOwner{T}"/> so callers with an ordinary <see cref="ReadOnlyMemory{T}"/> don't
        /// need to manage one themselves.
        /// </summary>
        /// <param name="payload">The acknowledgement payload. Not copied - the caller must not mutate it until the acknowledgement has been sent.</param>
        /// <exception cref="InvalidOperationException"><see cref="MsmtReceivedEventArgs.IsResponseRequested"/> was <see langword="false"/>, or a response was already sent.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than <see cref="MsmtLimits.MaxPayloadLength"/>.</exception>
        public void Reject(ReadOnlyMemory<byte> payload) => responder.Reject(new NonOwningMemoryOwner(payload));
    }
}
