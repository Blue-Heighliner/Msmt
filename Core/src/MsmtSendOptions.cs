namespace BlueHeighliner.Msmt;

/// <summary>
/// Options for a single message queued for delivery via <see cref="IMsmtPeer.Send"/> or <see cref="IMsmtPeer.Request"/>.
/// </summary>
public sealed record MsmtSendOptions
{
    /// <summary>
    /// Gets an optional object reference to associate with this payload. When non-<see langword="null"/>,
    /// this connection's owning <see cref="IMsmtPeer"/> raises <see cref="IMsmtPeer.PackageChanged"/> as this
    /// send's <see cref="IMsmtPackage"/> progresses, identifying it by this same reference, and <see
    /// cref="IMsmtPeer.GetPackage"/> can look it up by that reference until it completes. Must not equal the
    /// tag of another send still in flight (queued or not yet completed) on the same connection, or <see
    /// cref="IMsmtPeer.GetPackage"/> for that shared tag becomes ambiguous between the two.
    /// </summary>
    public object? Tag { get; init; }

    /// <summary>
    /// Gets this payload's priority relative to other queued payloads. Payloads with a higher priority are
    /// transmitted before those with a lower one; payloads with equal priority are transmitted
    /// first-in-first-out. Defaults to <c>0</c>.
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// Gets the DSCP (Differentiated Services Code Point) to mark this payload's packets with for
    /// network-level quality of service, or <see langword="null"/> to leave the connection's socket
    /// unmarked. Applied to the underlying TCP socket immediately before this payload is written, so it
    /// takes effect even on a connection cached and reused across sends (<see
    /// cref="MsmtOperationMode.Session"/> or <see cref="MsmtOperationMode.MessageWithRekeying"/>) with a
    /// different value from one send to the next. Marking is best-effort: per the ICD, whether - and how -
    /// the underlying platform and network actually honor it is outside this library's control, and a
    /// platform that rejects the marking does not fail the send.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is outside DSCP's valid 6-bit range of 0 to 63.</exception>
    public int? Dscp
    {
        get;
        init
        {
            if (value is < 0 or > 63)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "DSCP must be between 0 and 63.");
            }

            field = value;
        }
    }
}
