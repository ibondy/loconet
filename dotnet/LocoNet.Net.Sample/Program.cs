using LocoNet.Net;

// Simple LocoNet-over-TCP listener with auto-reconnect, idle watchdog, and TX rate cap.
//   dotnet run --project dotnet/LocoNet.Net.Sample -- <host> <port>

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 ? int.Parse(args[1]) : 1234;

var options = new LocoNetTcpClientOptions
{
    // ---- Tier 1: auto-reconnect ----
    ConnectTimeout = TimeSpan.FromSeconds(5),
    InitialReconnectDelay = TimeSpan.FromMilliseconds(500),
    MaxReconnectDelay = TimeSpan.FromSeconds(15),
    ReconnectJitter = 0.2,
    MaxReconnectAttempts = -1, // unlimited

    // ---- Tier 2: link health ----
    EnableTcpKeepAlives = true,
    TcpKeepAliveTime = TimeSpan.FromSeconds(15),
    TcpKeepAliveInterval = TimeSpan.FromSeconds(5),
    TcpKeepAliveRetryCount = 3,
    IdleReadWatchdog = TimeSpan.FromSeconds(30),
    TxQueueCapacity = 256,
    DisconnectBehavior = TxBehaviorWhenDisconnected.Discard,

    // ---- Tier 3: diagnostics ----
    TxMessagesPerSecond = 50, // be polite to slow hardware
    Logger = (level, message, ex) =>
    {
        if (level >= LocoNetLogLevel.Info)
        {
            Console.WriteLine($"[{level}] {message}{(ex is null ? "" : $" :: {ex.Message}")}");
        }
    },
};

await using var client = new LocoNetTcpClient(host, port, options);

client.MessageReceived += (_, e) =>
{
    var msg = e.Message;
    Console.WriteLine($"RX  {msg.OpCode,-12} ({msg.Length,2} B)  {msg}");
};

client.StateChanged += (_, e) =>
{
    Console.WriteLine($"State: {e.Previous} -> {e.Current}{(e.Error is null ? "" : $" ({e.Error.Message})")}");
};

client.Reconnected += (_, e) =>
{
    Console.WriteLine($"Reconnected (count={e.ReconnectCount}, downtime={e.Downtime.TotalSeconds:F1}s)");
    // After a reconnect the slot state on the command station is unknown to us:
    // a real throttle app should re-poll slots / re-acquire any locos it owns here.
};

Console.WriteLine($"Connecting to {host}:{port}...");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await client.ConnectAsync(cts.Token);
Console.WriteLine("Press Ctrl+C to exit.");

// Example: request global power on.
await client.SendAsync(LnMsg.FromPayload(stackalloc byte[] { (byte)OpCode.GpOn }), cts.Token);

// Periodic stats dump while waiting for Ctrl+C.
var statsTask = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), cts.Token); }
        catch (OperationCanceledException) { break; }

        var c = client.ConnectionStats;
        Console.WriteLine(
            $"stats: state={client.State} connects={c.Connects} reconnects={c.Reconnects} " +
            $"failed={c.FailedAttempts} drops={c.Drops} idleTrips={c.IdleWatchdogTrips} " +
            $"txDiscarded={c.TxDiscardedWhileDisconnected} txDropped={c.TxDroppedByBackpressure} " +
            $"rx={client.RxStats.RxPackets} tx={client.TxStats.TxPackets}");
    }
});

try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { }

try { await statsTask; } catch { }
