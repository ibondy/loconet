using System;
using System.Threading.Channels;

namespace LocoNet.Net;

/// <summary>
/// Configuration for <see cref="LocoNetTcpClient"/>'s resilience features.
/// </summary>
public sealed record LocoNetTcpClientOptions
{
    /// <summary>Maximum time to wait for an individual TCP connect attempt before aborting it.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Initial delay before the first reconnect attempt.</summary>
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound for the reconnect delay (exponential backoff is capped here).</summary>
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Fractional jitter applied to each backoff delay (0..1). 0.2 = ±20%.</summary>
    public double ReconnectJitter { get; init; } = 0.2;

    /// <summary>Maximum reconnect attempts after a disconnect. <c>-1</c> means unlimited.</summary>
    public int MaxReconnectAttempts { get; init; } = -1;

    /// <summary>
    /// If set, the client forces a reconnect when no bytes have arrived from the peer for this
    /// duration. Useful to catch half-open connections that TCP keepalives miss. <c>null</c> = disabled.
    /// </summary>
    public TimeSpan? IdleReadWatchdog { get; init; }

    /// <summary>Maximum number of outbound frames buffered while the link is up.</summary>
    public int TxQueueCapacity { get; init; } = 1024;

    /// <summary>
    /// What happens to <see cref="LocoNetTcpClient.Send"/> when the TX queue is full.
    /// </summary>
    /// <remarks>
    /// Only <see cref="BoundedChannelFullMode.Wait"/> causes <see cref="LocoNetTcpClient.Send"/>
    /// to observe the overflow and increment
    /// <see cref="LnConnectionStats.TxDroppedByBackpressure"/>. The <see cref="BoundedChannelFullMode.DropOldest"/>
    /// and <see cref="BoundedChannelFullMode.DropWrite"/> modes silently absorb the write inside
    /// the underlying <see cref="System.Threading.Channels.Channel{T}"/>, so drops still occur but
    /// are not visible in the stats counter.
    /// </remarks>
    public BoundedChannelFullMode TxFullMode { get; init; } = BoundedChannelFullMode.DropOldest;

    /// <summary>What happens to <see cref="LocoNetTcpClient.Send"/> when the link is not Connected.</summary>
    public TxBehaviorWhenDisconnected DisconnectBehavior { get; init; } = TxBehaviorWhenDisconnected.Discard;

    /// <summary>Enable OS-level TCP keepalives on the underlying socket.</summary>
    public bool EnableTcpKeepAlives { get; init; } = true;

    /// <summary>Idle time before the first TCP keepalive probe is sent.</summary>
    public TimeSpan TcpKeepAliveTime { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Interval between successive TCP keepalive probes.</summary>
    public TimeSpan TcpKeepAliveInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Number of unacknowledged keepalive probes before the connection is dropped.</summary>
    public int TcpKeepAliveRetryCount { get; init; } = 3;

    /// <summary>
    /// Maximum outbound LocoNet messages per second. <c>null</c> = unlimited. LocoNet hardware
    /// caps out around ~500 msg/s at 16.66 kbaud; a value of 200 is a safe upper bound.
    /// </summary>
    public int? TxMessagesPerSecond { get; init; }

    /// <summary>Optional logging hook. Called from background threads; do not block.</summary>
    public LocoNetLogger? Logger { get; init; }
}

/// <summary>Behavior of <see cref="LocoNetTcpClient.Send"/> while the link is not in the Connected state.</summary>
public enum TxBehaviorWhenDisconnected
{
    /// <summary>Silently discard the message. Safe default; avoids replaying stale commands on reconnect.</summary>
    Discard,

    /// <summary>Throw an <see cref="InvalidOperationException"/>.</summary>
    Throw,
}
