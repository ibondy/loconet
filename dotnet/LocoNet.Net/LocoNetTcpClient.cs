using System;
using System.Buffers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace LocoNet.Net;

/// <summary>
/// Event arguments for a received LocoNet message.
/// </summary>
public sealed class LnMessageEventArgs(LnMsg message) : EventArgs
{
    public LnMsg Message { get; } = message;
}

/// <summary>
/// A resilient LocoNet-over-TCP client. Connects to a LocoNet-over-TCP server (for example a
/// LocoBufferUSB exposed via <c>ser2net</c>, an ESP32/Pico running the LocoNet2 firmware in
/// bridge mode, or JMRI's <c>LocoNetOverTcp</c> server) and exchanges raw LocoNet frames.
/// </summary>
/// <remarks>
/// <para>
/// Features (configured via <see cref="LocoNetTcpClientOptions"/>):
/// auto-reconnect with exponential backoff + jitter, per-attempt connect timeout, OS-level
/// TCP keepalives (Windows/Linux), application-level idle-read watchdog, bounded outbound
/// queue with configurable backpressure policy, optional TX rate cap, plus
/// <see cref="StateChanged"/> / <see cref="Reconnected"/> events and <see cref="ConnectionStats"/>
/// for diagnostics.
/// </para>
/// <para>
/// Pending TX frames are discarded when the link transitions out of
/// <see cref="ConnectionState.Connected"/>; this is intentional — replaying stale throttle
/// commands across a reconnect can be dangerous on a real layout. Callers should re-acquire
/// throttles and re-poll slot data after the <see cref="Reconnected"/> event.
/// </para>
/// </remarks>
public sealed class LocoNetTcpClient : ILocoNet, IAsyncDisposable, IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly LocoNetTcpClientOptions _options;
    private readonly LocoNetMessageBuffer _rxBuffer = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly object _stateGate = new();

    private Channel<LnMsg> _txChannel;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _supervisor;
    private ConnectionState _state = ConnectionState.Disconnected;
    private long _lastRxTicks; // DateTime.UtcNow.Ticks; 0 = not yet
    private DateTime? _disconnectedSinceUtc;
    private bool _started;
    private bool _disposed;

    public LocoNetTcpClient(string host, int port)
        : this(host, port, options: null) { }

    public LocoNetTcpClient(string host, int port, LocoNetTcpClientOptions? options)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _port = port;
        _options = options ?? new LocoNetTcpClientOptions();
        _txChannel = CreateTxChannel();
    }

    /// <summary>Raised when a complete, checksum-valid LocoNet message has been received.</summary>
    public event EventHandler<LnMessageEventArgs>? MessageReceived;

    /// <summary>Raised when the underlying socket disconnects (cleanly or due to an error).</summary>
    /// <remarks>For full lifecycle visibility subscribe to <see cref="StateChanged"/> instead.</remarks>
    public event EventHandler<Exception?>? Disconnected;

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>Raised when an auto-reconnect successfully restores the link.</summary>
    public event EventHandler<ReconnectedEventArgs>? Reconnected;

    /// <summary>Receive statistics.</summary>
    public LnRxStats RxStats => _rxBuffer.Stats;

    /// <summary>Transmit statistics.</summary>
    public LnTxStats TxStats { get; } = new();

    /// <summary>Connection-resilience statistics.</summary>
    public LnConnectionStats ConnectionStats { get; } = new();

    /// <summary>Current lifecycle state.</summary>
    public ConnectionState State
    {
        get { lock (_stateGate) return _state; }
    }

    /// <summary>True iff <see cref="State"/> is <see cref="ConnectionState.Connected"/>.</summary>
    public bool IsConnected => State == ConnectionState.Connected;

    /// <summary>
    /// Open the TCP connection. If reconnects are enabled (<see cref="LocoNetTcpClientOptions.MaxReconnectAttempts"/>
    /// != 0), the supervisor task takes over and will reconnect on failure.
    /// </summary>
    /// <exception cref="InvalidOperationException">Already started or disposed.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateGate)
        {
            if (_started)
            {
                throw new InvalidOperationException("Already started.");
            }
            _started = true;
        }

        // First connect runs inline so callers see a SocketException/TimeoutException
        // synchronously if the peer is unreachable AND reconnects are disabled.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        bool inlineConnected = false;
        try
        {
            await ConnectOnceAsync(linked.Token).ConfigureAwait(false);
            inlineConnected = true;
        }
        catch when (_options.MaxReconnectAttempts != 0)
        {
            // Supervisor will retry. Swallow the inline failure.
            ConnectionStats.FailedAttempts++;
        }
        catch
        {
            ConnectionStats.FailedAttempts++;
            TransitionState(ConnectionState.Faulted, ConnectionStats.LastError);
            throw;
        }

        _supervisor = Task.Run(() => SupervisorLoopAsync(inlineConnected));
    }

    /// <summary>Backwards-compatible alias for <see cref="ConnectAsync"/>.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) => ConnectAsync(cancellationToken);

    /// <summary>Enqueue a message to be sent. Returns immediately.</summary>
    public ValueTask SendAsync(LnMsg message, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            if (_options.DisconnectBehavior == TxBehaviorWhenDisconnected.Throw)
            {
                throw new InvalidOperationException("Not connected.");
            }
            ConnectionStats.TxDiscardedWhileDisconnected++;
            return ValueTask.CompletedTask;
        }
        return _txChannel.Writer.WriteAsync(message, cancellationToken);
    }

    /// <inheritdoc />
    public void Send(LnMsg message)
    {
        if (!IsConnected)
        {
            if (_options.DisconnectBehavior == TxBehaviorWhenDisconnected.Throw)
            {
                throw new InvalidOperationException("Not connected.");
            }
            ConnectionStats.TxDiscardedWhileDisconnected++;
            return;
        }
        if (!_txChannel.Writer.TryWrite(message))
        {
            ConnectionStats.TxDroppedByBackpressure++;
        }
    }

    // ----- Supervisor / connect / IO loops -----

    private async Task SupervisorLoopAsync(bool alreadyConnected)
    {
        int attempt = 0;
        var ct = _shutdownCts.Token;

        if (alreadyConnected)
        {
            await RunSessionAsync(ct).ConfigureAwait(false);
        }

        while (!ct.IsCancellationRequested)
        {
            if (_options.MaxReconnectAttempts >= 0 && attempt >= _options.MaxReconnectAttempts)
            {
                TransitionState(ConnectionState.Faulted, ConnectionStats.LastError);
                return;
            }

            var delay = ComputeBackoff(attempt);
            Log(LocoNetLogLevel.Info, $"Reconnect attempt {attempt + 1} in {delay.TotalMilliseconds:F0} ms", null);

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            try
            {
                DateTime disconnectStartUtc = _disconnectedSinceUtc ?? DateTime.UtcNow;
                await ConnectOnceAsync(ct).ConfigureAwait(false);
                attempt = 0;
                ConnectionStats.Reconnects++;
                var downtime = DateTime.UtcNow - disconnectStartUtc;
                _disconnectedSinceUtc = null;
                SafeRaiseReconnected(new ReconnectedEventArgs(ConnectionStats.Reconnects, downtime));
                await RunSessionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                ConnectionStats.FailedAttempts++;
                ConnectionStats.LastError = ex;
                Log(LocoNetLogLevel.Warning, $"Reconnect attempt {attempt + 1} failed: {ex.Message}", ex);
                attempt++;
            }
        }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        TransitionState(_state == ConnectionState.Disconnected ? ConnectionState.Connecting : ConnectionState.Reconnecting, null);

        // Fresh channel each session — previously-queued (now-stale) frames are dropped.
        _txChannel = CreateTxChannel();

        var client = new TcpClient { NoDelay = true };
        try
        {
            if (_options.EnableTcpKeepAlives)
            {
                TryEnableKeepAlives(client.Client);
            }

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(_options.ConnectTimeout);
            try
            {
                await client.ConnectAsync(_host, _port, attemptCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"TCP connect to {_host}:{_port} did not complete within {_options.ConnectTimeout}.");
            }

            _client = client;
            _stream = client.GetStream();
            Volatile.Write(ref _lastRxTicks, DateTime.UtcNow.Ticks);
            ConnectionStats.Connects++;
            _disconnectedSinceUtc = null;
            TransitionState(ConnectionState.Connected, null);
            Log(LocoNetLogLevel.Info, $"Connected to {_host}:{_port}", null);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task RunSessionAsync(CancellationToken supervisorCt)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(supervisorCt);
        var ct = sessionCts.Token;

        var read = Task.Run(() => ReadLoopAsync(ct), ct);
        var write = Task.Run(() => WriteLoopAsync(ct), ct);
        var watchdog = _options.IdleReadWatchdog is { } idleThreshold
            ? Task.Run(() => IdleWatchdogAsync(idleThreshold, sessionCts, ct), ct)
            : Task.CompletedTask;

        Exception? failure = null;
        try { await Task.WhenAny(read, write).ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }

        sessionCts.Cancel();

        try { await Task.WhenAll(read, write, watchdog).ConfigureAwait(false); }
        catch (Exception ex) { failure ??= ex; }

        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
        _txChannel.Writer.TryComplete();

        if (failure is OperationCanceledException) failure = null;
        if (failure is not null) ConnectionStats.LastError = failure;
        _disconnectedSinceUtc = DateTime.UtcNow;
        ConnectionStats.Drops++;
        SafeRaiseDisconnected(failure);
        TransitionState(ConnectionState.Reconnecting, failure);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var stream = _stream!;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }

                if (read <= 0) return; // peer closed

                Volatile.Write(ref _lastRxTicks, DateTime.UtcNow.Ticks);
                ConnectionStats.LastRxUtc = DateTime.UtcNow;

                for (int i = 0; i < read; i++)
                {
                    var msg = _rxBuffer.AddByte(buffer[i]);
                    if (msg.HasValue)
                    {
                        SafeRaiseMessage(msg.Value);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        var stream = _stream!;
        long minIntervalTicks = _options.TxMessagesPerSecond is int rate && rate > 0
            ? TimeSpan.TicksPerSecond / rate
            : 0;
        long lastSendTicks = 0;
        var reader = _txChannel.Reader;
        try
        {
            await foreach (var msg in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (minIntervalTicks > 0)
                {
                    long elapsed = DateTime.UtcNow.Ticks - lastSendTicks;
                    long wait = minIntervalTicks - elapsed;
                    if (wait > 0)
                    {
                        try { await Task.Delay(TimeSpan.FromTicks(wait), ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }
                }

                try
                {
                    await stream.WriteAsync(msg.Bytes.ToArray(), ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                    TxStats.TxPackets++;
                    ConnectionStats.LastTxUtc = DateTime.UtcNow;
                    lastSendTicks = DateTime.UtcNow.Ticks;
                }
                catch (OperationCanceledException) { return; }
                catch
                {
                    TxStats.TxErrors++;
                    throw;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task IdleWatchdogAsync(TimeSpan threshold, CancellationTokenSource sessionCts, CancellationToken ct)
    {
        var pollInterval = TimeSpan.FromMilliseconds(Math.Max(250, threshold.TotalMilliseconds / 4));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
                long last = Volatile.Read(ref _lastRxTicks);
                if (last == 0) continue;
                var age = DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc);
                if (age > threshold)
                {
                    ConnectionStats.IdleWatchdogTrips++;
                    Log(LocoNetLogLevel.Warning, $"Idle watchdog tripped (no RX for {age.TotalSeconds:F1} s)", null);
                    try { _stream?.Dispose(); } catch { }
                    sessionCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ----- Helpers -----

    private Channel<LnMsg> CreateTxChannel()
        => Channel.CreateBounded<LnMsg>(new BoundedChannelOptions(Math.Max(1, _options.TxQueueCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = _options.TxFullMode,
        });

    private TimeSpan ComputeBackoff(int attempt)
    {
        double baseMs = _options.InitialReconnectDelay.TotalMilliseconds * Math.Pow(2, attempt);
        double cappedMs = Math.Min(baseMs, _options.MaxReconnectDelay.TotalMilliseconds);
        double jitter = _options.ReconnectJitter <= 0
            ? 0
            : (Random.Shared.NextDouble() * 2 - 1) * _options.ReconnectJitter * cappedMs;
        double ms = Math.Max(0, cappedMs + jitter);
        return TimeSpan.FromMilliseconds(ms);
    }

    private void TryEnableKeepAlives(Socket socket)
    {
        try { socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); }
        catch (Exception ex) { Log(LocoNetLogLevel.Debug, "KeepAlive option not supported", ex); return; }

        TrySetTcpOption(socket, SocketOptionName.TcpKeepAliveTime, (int)_options.TcpKeepAliveTime.TotalSeconds);
        TrySetTcpOption(socket, SocketOptionName.TcpKeepAliveInterval, (int)_options.TcpKeepAliveInterval.TotalSeconds);
        TrySetTcpOption(socket, SocketOptionName.TcpKeepAliveRetryCount, _options.TcpKeepAliveRetryCount);
    }

    private void TrySetTcpOption(Socket socket, SocketOptionName name, int value)
    {
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, name, value); }
        catch (Exception ex) { Log(LocoNetLogLevel.Debug, $"TCP option {name} not supported", ex); }
    }

    private void TransitionState(ConnectionState next, Exception? error)
    {
        ConnectionState prev;
        lock (_stateGate)
        {
            if (_state == next) return;
            prev = _state;
            _state = next;
        }
        Log(LocoNetLogLevel.Debug, $"State {prev} -> {next}", error);
        try { StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(prev, next, error)); }
        catch (Exception ex) { Log(LocoNetLogLevel.Warning, "StateChanged handler threw", ex); }
    }

    private void SafeRaiseMessage(LnMsg msg)
    {
        try { MessageReceived?.Invoke(this, new LnMessageEventArgs(msg)); }
        catch (Exception ex) { Log(LocoNetLogLevel.Warning, "MessageReceived handler threw", ex); }
    }

    private void SafeRaiseDisconnected(Exception? failure)
    {
        try { Disconnected?.Invoke(this, failure); }
        catch (Exception ex) { Log(LocoNetLogLevel.Warning, "Disconnected handler threw", ex); }
    }

    private void SafeRaiseReconnected(ReconnectedEventArgs e)
    {
        try { Reconnected?.Invoke(this, e); }
        catch (Exception ex) { Log(LocoNetLogLevel.Warning, "Reconnected handler threw", ex); }
    }

    private void Log(LocoNetLogLevel level, string message, Exception? exception)
    {
        var logger = _options.Logger;
        if (logger is null) return;
        try { logger(level, message, exception); }
        catch { /* never let a logger fault tear down the supervisor */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _shutdownCts.Cancel(); } catch { }
        _txChannel.Writer.TryComplete();
        if (_supervisor is not null)
        {
            try { await _supervisor.ConfigureAwait(false); }
            catch { /* shutdown */ }
        }
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _shutdownCts.Dispose();
        TransitionState(ConnectionState.Disconnected, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _shutdownCts.Cancel(); } catch { }
        _txChannel.Writer.TryComplete();
        // Best-effort sync wait with timeout to avoid the GetAwaiter().GetResult() deadlock.
        if (_supervisor is not null)
        {
            try { _supervisor.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _shutdownCts.Dispose();
        TransitionState(ConnectionState.Disconnected, null);
    }
}
