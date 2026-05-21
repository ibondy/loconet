using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocoNet.Net.Tests;

[TestClass]
public class LocoNetTcpClientResilienceTests
{
    private static (TcpListener listener, int port) StartListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    [TestMethod]
    public async Task ConnectAsync_ReachesConnectedState_AndRaisesStateChanged()
    {
        var (listener, port) = StartListener();
        try
        {
            var transitions = new System.Collections.Generic.List<(ConnectionState from, ConnectionState to)>();
            await using var client = new LocoNetTcpClient("127.0.0.1", port, new LocoNetTcpClientOptions
            {
                MaxReconnectAttempts = 0,
            });
            client.StateChanged += (_, e) => { lock (transitions) transitions.Add((e.Previous, e.Current)); };

            var acceptTask = listener.AcceptTcpClientAsync();
            await client.ConnectAsync();
            using var server = await acceptTask;

            Assert.Equal(ConnectionState.Connected, client.State);
            Assert.True(client.IsConnected);
            Assert.Equal(1, client.ConnectionStats.Connects);

            // Disconnected → Connecting → Connected at minimum.
            lock (transitions)
            {
                Assert.Contains(transitions, t => t.to == ConnectionState.Connecting);
                Assert.Contains(transitions, t => t.to == ConnectionState.Connected);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task ConnectAsync_UnreachablePeerWithNoReconnect_Throws()
    {
        // Port 1 is virtually always closed on Windows; use a short ConnectTimeout to keep the
        // test fast.
        await using var client = new LocoNetTcpClient("127.0.0.1", 1, new LocoNetTcpClientOptions
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
            MaxReconnectAttempts = 0,
        });
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync());
        Assert.Equal(ConnectionState.Faulted, client.State);
        Assert.Equal(1, client.ConnectionStats.FailedAttempts);
    }

    [TestMethod]
    public async Task Send_WhenDisconnected_WithDiscardBehavior_IncrementsCounter()
    {
        await using var client = new LocoNetTcpClient("127.0.0.1", 1, new LocoNetTcpClientOptions
        {
            DisconnectBehavior = TxBehaviorWhenDisconnected.Discard,
            MaxReconnectAttempts = 0,
        });
        client.Send(LnMsg.Make(OpCode.LocoSpd, 1, 1));
        client.Send(LnMsg.Make(OpCode.LocoSpd, 2, 2));
        Assert.Equal(2ul, client.ConnectionStats.TxDiscardedWhileDisconnected);
    }

    [TestMethod]
    public async Task Send_WhenDisconnected_WithThrowBehavior_Throws()
    {
        await using var client = new LocoNetTcpClient("127.0.0.1", 1, new LocoNetTcpClientOptions
        {
            DisconnectBehavior = TxBehaviorWhenDisconnected.Throw,
            MaxReconnectAttempts = 0,
        });
        Assert.Throws<InvalidOperationException>(() => client.Send(LnMsg.Make(OpCode.LocoSpd, 1, 1)));
    }

    [TestMethod]
    public async Task PeerClose_TriggersReconnect_AndRaisesReconnectedEvent()
    {
        var (listener, port) = StartListener();
        try
        {
            var reconnects = new TaskCompletionSource<ReconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var client = new LocoNetTcpClient("127.0.0.1", port, new LocoNetTcpClientOptions
            {
                InitialReconnectDelay = TimeSpan.FromMilliseconds(50),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(100),
                ReconnectJitter = 0,
                MaxReconnectAttempts = 5,
            });
            client.Reconnected += (_, e) => reconnects.TrySetResult(e);

            var firstAccept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync();
            using (var server1 = await firstAccept)
            {
                // Close server side to force a disconnect.
                server1.Close();
            }

            // Listener accepts the reconnect attempt.
            using var server2 = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var args = await reconnects.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, args.ReconnectCount);
            Assert.True(client.ConnectionStats.Reconnects >= 1);
            Assert.True(client.ConnectionStats.Drops >= 1);
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task UnreachablePeer_WithBoundedRetries_TransitionsToFaulted()
    {
        await using var client = new LocoNetTcpClient("127.0.0.1", 1, new LocoNetTcpClientOptions
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            InitialReconnectDelay = TimeSpan.FromMilliseconds(20),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(50),
            ReconnectJitter = 0,
            MaxReconnectAttempts = 2,
        });
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += (_, e) =>
        {
            if (e.Current == ConnectionState.Faulted) faulted.TrySetResult();
        };

        // Inline attempt should swallow because MaxReconnectAttempts != 0.
        await client.ConnectAsync();

        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ConnectionState.Faulted, client.State);
        Assert.True(client.ConnectionStats.FailedAttempts >= 2);
    }

    [TestMethod]
    public async Task SentMessage_AppearsOnTheWire()
    {
        var (listener, port) = StartListener();
        try
        {
            await using var client = new LocoNetTcpClient("127.0.0.1", port, new LocoNetTcpClientOptions
            {
                MaxReconnectAttempts = 0,
            });
            var acceptTask = listener.AcceptTcpClientAsync();
            await client.ConnectAsync();
            using var server = await acceptTask;
            var serverStream = server.GetStream();

            client.Send(LnMsg.Make(OpCode.LocoSpd, 0x12, 0x34));

            var buf = new byte[4];
            int total = 0;
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (total < buf.Length)
            {
                int n = await serverStream.ReadAsync(buf.AsMemory(total), readCts.Token);
                if (n == 0) break;
                total += n;
            }
            Assert.Equal(4, total);
            var parsed = LnMsg.FromBytes(buf);
            Assert.Equal(OpCode.LocoSpd, parsed.OpCode);
            Assert.Equal(0x12, parsed[1]);
            Assert.Equal(0x34, parsed[2]);
        }
        finally
        {
            listener.Stop();
        }
    }
}
