using System;

namespace LocoNet.Net;

/// <summary>Connection lifecycle state for <see cref="LocoNetTcpClient"/>.</summary>
public enum ConnectionState
{
    /// <summary>Initial state and final state after disposal.</summary>
    Disconnected,
    /// <summary>An initial connect attempt is in progress.</summary>
    Connecting,
    /// <summary>Socket is open and read/write loops are running.</summary>
    Connected,
    /// <summary>A reconnect attempt is in progress (the link previously was Connected at least once).</summary>
    Reconnecting,
    /// <summary>Reconnect attempts have been exhausted; the client will not retry.</summary>
    Faulted,
}

/// <summary>Severity for <see cref="LocoNetLogger"/> messages.</summary>
public enum LocoNetLogLevel
{
    Trace, Debug, Info, Warning, Error,
}

/// <summary>Optional diagnostic logging callback used by <see cref="LocoNetTcpClient"/>.</summary>
public delegate void LocoNetLogger(LocoNetLogLevel level, string message, Exception? exception);

/// <summary>Event arguments for <see cref="LocoNetTcpClient.StateChanged"/>.</summary>
public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionStateChangedEventArgs(ConnectionState previous, ConnectionState current, Exception? error)
    {
        Previous = previous;
        Current = current;
        Error = error;
    }
    public ConnectionState Previous { get; }
    public ConnectionState Current { get; }
    /// <summary>Set when the transition was caused by a fault (e.g. socket error).</summary>
    public Exception? Error { get; }
}

/// <summary>Event arguments for <see cref="LocoNetTcpClient.Reconnected"/>.</summary>
public sealed class ReconnectedEventArgs : EventArgs
{
    public ReconnectedEventArgs(int reconnectCount, TimeSpan downtime)
    {
        ReconnectCount = reconnectCount;
        Downtime = downtime;
    }
    /// <summary>1-based count of successful reconnects since the client was created.</summary>
    public int ReconnectCount { get; }
    /// <summary>How long the link was down between the previous disconnect and this reconnect.</summary>
    public TimeSpan Downtime { get; }
}

/// <summary>Runtime connection-resilience statistics for <see cref="LocoNetTcpClient"/>.</summary>
public sealed class LnConnectionStats
{
    /// <summary>Total successful connects (including the first).</summary>
    public int Connects { get; internal set; }
    /// <summary>Total successful reconnects (excludes the first connect).</summary>
    public int Reconnects { get; internal set; }
    /// <summary>Failed connect attempts (TCP errors, timeouts).</summary>
    public int FailedAttempts { get; internal set; }
    /// <summary>Number of connection drops detected by the read loop.</summary>
    public int Drops { get; internal set; }
    /// <summary>Number of times the idle-read watchdog forced a reconnect.</summary>
    public int IdleWatchdogTrips { get; internal set; }
    /// <summary>Number of TX messages discarded because the link was not Connected.</summary>
    public ulong TxDiscardedWhileDisconnected { get; internal set; }
    /// <summary>
    /// Number of TX messages dropped because the bounded outbound channel was full.
    /// </summary>
    /// <remarks>
    /// Only counted when <see cref="LocoNetTcpClientOptions.TxFullMode"/> is
    /// <see cref="System.Threading.Channels.BoundedChannelFullMode.Wait"/>. The <c>DropOldest</c>
    /// and <c>DropWrite</c> modes silently drop inside the channel and do not increment this counter.
    /// </remarks>
    public ulong TxDroppedByBackpressure { get; internal set; }
    /// <summary>UTC timestamp of the most recently received byte (any byte, not just a full frame).</summary>
    public DateTime? LastRxUtc { get; internal set; }
    /// <summary>UTC timestamp of the most recently sent frame.</summary>
    public DateTime? LastTxUtc { get; internal set; }
    /// <summary>The most recent error captured by the supervisor (if any).</summary>
    public Exception? LastError { get; internal set; }

    public void Reset()
    {
        Connects = 0;
        Reconnects = 0;
        FailedAttempts = 0;
        Drops = 0;
        IdleWatchdogTrips = 0;
        TxDiscardedWhileDisconnected = 0;
        TxDroppedByBackpressure = 0;
        LastRxUtc = null;
        LastTxUtc = null;
        LastError = null;
    }
}
