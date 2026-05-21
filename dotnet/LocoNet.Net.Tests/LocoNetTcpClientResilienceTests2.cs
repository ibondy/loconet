using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocoNet.Net.Tests;

[TestClass]
public class LocoNetTcpClientResilienceTests2
{
    private static (TcpListener listener, int port) StartListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    // 1. Reconnected event MUST NOT fire on the first connect.
    [TestMethod]
    public async Task FirstConnect_DoesNotRaiseReconnected()
    {
        var (listener, port) = StartListener();
        try
        {
            int reconnectFires = 0;
            await using var client = new LocoNetTcpClient("127.0.0.1", port, new LocoNetTcpClientOptions
            {
                MaxReconnectAttempts = 0,
            });
            client.Reconnected += (_, _) => Interlocked.Increment(ref reconnectFires);

            var accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync();
            using var server = await accept;

            // Give any erroneous handler a chance to run.
            await Task.Delay(100);
            Assert.Equal(0, reconnectFires);
        }
        finally { listener.Stop(); }
    }

    // 2. Pending TX is dropped across a reconnect — the new server does not see stale frames.
    [TestMethod]
    public async Task PendingTxIsDroppedAcrossReconnect()
    {
        var (listener, port) = StartListener();
        try
        {
            var reconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var client = new LocoNetTcpClient("127.0.0.1", port, new LocoNetTcpClientOptions
            {
                InitialReconnectDelay = TimeSpan.FromMilliseconds(50),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(100),
                ReconnectJitter = 0,
                MaxReconnectAttempts = 5,
                TxMessagesPerSecond = 1, // slow writer so frames queue up
            });
            client.Reconnected += (_, _) => reconnectedTcs.TrySetResult();

            var acceptFirst = listener.AcceptTcpClientAsync();
            await client.ConnectAsync();
            var server1 = await acceptFirst;

            // Enqueue several frames, then drop the link before the writer can drain them.
            for (int i = 0; i < 10; i++)
            {
                client.Send(LnMsg.Make(OpCode.LocoSpd, (byte)i, (byte)i));
            }
            server1.Close();

            // Second server (after reconnect) should NOT see the queued frames.
            using var server2 = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await reconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var stream2 = server2.GetStream();
            stream2.ReadTimeout = 400;
            var buf = new byte[16];
            int read = 0;
            try { read = stream2.Read(buf, 0, buf.Length); }
            catch (IOException) { /* timeout = nothing arrived, good */ }

            Assert.Equal(0, read);
        }
        finally { listener.Stop(); }
    }

    // 3. Idle-read watchdog trips and triggers a reconnect.
    [TestMethod]
    public async Task IdleReadWatchdog_TripsAndReconnects()
    {
        var (listener, port) = StartListener();
        try
        {
            var reconnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var client = new LocoNetTcpClient("127.0.0.1", port, new LocoNetTcpClientOptions
            {
                IdleReadWatchdog = TimeSpan.FromMilliseconds(500),
                InitialReconnectDelay = TimeSpan.FromMilliseconds(50),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(100),
                ReconnectJitter = 0,
                MaxReconnectAttempts = 3,
            });
            client.Reconnected += (_, _) => reconnectTcs.TrySetResult();

            var accept1 = listener.AcceptTcpClientAsync();
            await client.ConnectAsync();
            using var server1 = await accept1;
            // Server stays silent — watchdog should trip ~500ms later.

            using var server2 = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await reconnectTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(client.ConnectionStats.IdleWatchdogTrips >= 1);
        }
        finally { listener.Stop(); }
    }

    // 4. TX queue overflow increments TxDroppedByBackpressure.
    [TestMethod]
    public async Task TxBackpressure_DropsExcessAndIncrementsCounter()
    {
        var (listener, port) = StartListener();
        try
        {
            await using var client = new LocoNetTcpClient("127.0.0.1", port, new LocoNetTcpClientOptions
            {
                MaxReconnectAttempts = 0,
                TxQueueCapacity = 4,
                // Wait mode is the only FullMode where TryWrite returns false on overflow,
                // letting Send() observe the failure and increment TxDroppedByBackpressure.
                // (DropOldest/DropWrite silently absorb the write inside the channel.)
                TxFullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
                TxMessagesPerSecond = 1, // ensure writer drains slower than we enqueue
            });

            var accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync();
            using var server = await accept;
            // Don't read on server side — writer will block after a few frames.

            for (int i = 0; i < 200; i++)
            {
                client.Send(LnMsg.Make(OpCode.LocoSpd, 1, 1));
            }

            // Drain may take some ticks; give the channel a moment to register drops.
            await Task.Delay(100);
            Assert.True(client.ConnectionStats.TxDroppedByBackpressure > 0,
                $"expected drops > 0, got {client.ConnectionStats.TxDroppedByBackpressure}");
        }
        finally { listener.Stop(); }
    }

    // 5. StateChanged transition sequence across a peer-close + reconnect.
    [TestMethod]
    public async Task StateChanged_FollowsExpectedSequence()
    {
        var (listener, port) = StartListener();
        try
        {
            var transitions = new List<ConnectionState>();
            var reconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var client = new LocoNetTcpClient("127.0.0.1", port, new LocoNetTcpClientOptions
            {
                InitialReconnectDelay = TimeSpan.FromMilliseconds(50),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(100),
                ReconnectJitter = 0,
                MaxReconnectAttempts = 5,
            });
            client.StateChanged += (_, e) => { lock (transitions) transitions.Add(e.Current); };
            client.Reconnected += (_, _) => reconnectedTcs.TrySetResult();

            var accept1 = listener.AcceptTcpClientAsync();
            await client.ConnectAsync();
            using (var server1 = await accept1)
            {
                server1.Close();
            }

            using var server2 = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await reconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50); // let final transition flush

            List<ConnectionState> snapshot;
            lock (transitions) snapshot = new List<ConnectionState>(transitions);

            // Required ordered subset.
            int i = 0;
            void Expect(ConnectionState s)
            {
                while (i < snapshot.Count && snapshot[i] != s) i++;
                Assert.True(i < snapshot.Count, $"missing {s} in [{string.Join(",", snapshot)}]");
                i++;
            }
            Expect(ConnectionState.Connecting);
            Expect(ConnectionState.Connected);
            Expect(ConnectionState.Reconnecting);
            Expect(ConnectionState.Connected);
        }
        finally { listener.Stop(); }
    }

    // 6. DisposeAsync cancels the supervisor promptly.
    [TestMethod]
    public async Task DisposeAsync_CancelsSupervisorPromptly()
    {
        var client = new LocoNetTcpClient("127.0.0.1", 1, new LocoNetTcpClientOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            InitialReconnectDelay = TimeSpan.FromSeconds(30),
            MaxReconnectDelay = TimeSpan.FromMinutes(5),
            MaxReconnectAttempts = -1, // unlimited
        });
        await client.ConnectAsync(); // first attempt fails and is swallowed; supervisor enters long backoff

        var sw = Stopwatch.StartNew();
        await client.DisposeAsync();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"DisposeAsync took {sw.Elapsed} — expected < 3s");
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }

    // 7. Double ConnectAsync throws.
    [TestMethod]
    public async Task DoubleConnect_Throws()
    {
        var (listener, port) = StartListener();
        try
        {
            await using var client = new LocoNetTcpClient("127.0.0.1", port, new LocoNetTcpClientOptions
            {
                MaxReconnectAttempts = 0,
            });
            var accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync();
            using var server = await accept;

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync());
        }
        finally { listener.Stop(); }
    }

    // 8. A throwing MessageReceived handler does not kill the supervisor — subsequent frames still arrive.
    [TestMethod]
    public async Task ThrowingMessageHandler_DoesNotKillSupervisor()
    {
        var (listener, port) = StartListener();
        try
        {
            int received = 0;
            var secondTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await using var client = new LocoNetTcpClient("127.0.0.1", port, new LocoNetTcpClientOptions
            {
                MaxReconnectAttempts = 0,
            });
            client.MessageReceived += (_, _) =>
            {
                int n = Interlocked.Increment(ref received);
                if (n == 1) throw new InvalidOperationException("boom");
                if (n == 2) secondTcs.TrySetResult();
            };

            var accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync();
            using var server = await accept;
            var s = server.GetStream();

            // Two well-formed LocoSpd frames (4 bytes each).
            var frame1 = LnMsg.Make(OpCode.LocoSpd, 1, 1).Bytes.ToArray();
            var frame2 = LnMsg.Make(OpCode.LocoSpd, 2, 2).Bytes.ToArray();
            await s.WriteAsync(frame1);
            await s.FlushAsync();
            await s.WriteAsync(frame2);
            await s.FlushAsync();

            await secondTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(ConnectionState.Connected, client.State);
            Assert.True(received >= 2);
        }
        finally { listener.Stop(); }
    }
}
