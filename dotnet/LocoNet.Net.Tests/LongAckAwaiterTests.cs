using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocoNet.Net.Tests;

[TestClass]
public class LongAckAwaiterTests
{
    [TestMethod]
    public async Task ResolvesOnMatchingLongAck()
    {
        var ln = new FakeLocoNet();
        using var awaiter = new LongAckAwaiter(ln);

        var request = LnMsg.Make(OpCode.SwReq, 0x10, 0x20);
        var task = awaiter.SendAndAwaitAckAsync(request, TimeSpan.FromSeconds(2));

        // Sent first.
        Assert.Single(ln.Sent);
        Assert.Equal(request, ln.Sent[0]);

        ln.Receive(LnMsg.MakeLongAck((byte)OpCode.SwReq, 0x7F));
        var code = await task;
        Assert.Equal(0x7F, code);
    }

    [TestMethod]
    public async Task DeliversInFifoOrderForSameOpcode()
    {
        var ln = new FakeLocoNet();
        using var awaiter = new LongAckAwaiter(ln);

        var t1 = awaiter.SendAndAwaitAckAsync(LnMsg.Make(OpCode.SwReq, 0x01, 0x00), TimeSpan.FromSeconds(2));
        var t2 = awaiter.SendAndAwaitAckAsync(LnMsg.Make(OpCode.SwReq, 0x02, 0x00), TimeSpan.FromSeconds(2));

        ln.Receive(LnMsg.MakeLongAck((byte)OpCode.SwReq, 0x11));
        ln.Receive(LnMsg.MakeLongAck((byte)OpCode.SwReq, 0x22));

        Assert.Equal(0x11, await t1);
        Assert.Equal(0x22, await t2);
    }

    [TestMethod]
    public async Task IgnoresAckForDifferentOpcode()
    {
        var ln = new FakeLocoNet();
        using var awaiter = new LongAckAwaiter(ln);

        var task = awaiter.SendAndAwaitAckAsync(LnMsg.Make(OpCode.SwReq, 0x01, 0x00), TimeSpan.FromMilliseconds(200));

        ln.Receive(LnMsg.MakeLongAck((byte)OpCode.PeerXfer, 0x7F));

        await Assert.ThrowsAsync<TimeoutException>(() => task);
    }

    [TestMethod]
    public async Task TimesOutWhenNoAckArrives()
    {
        var ln = new FakeLocoNet();
        using var awaiter = new LongAckAwaiter(ln);

        var task = awaiter.SendAndAwaitAckAsync(LnMsg.Make(OpCode.SwReq, 0x01, 0x00), TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<TimeoutException>(() => task);
    }

    [TestMethod]
    public async Task RespectsExternalCancellation()
    {
        var ln = new FakeLocoNet();
        using var awaiter = new LongAckAwaiter(ln);
        using var cts = new CancellationTokenSource();

        var task = awaiter.SendAndAwaitAckAsync(LnMsg.Make(OpCode.SwReq, 0x01, 0x00), Timeout.InfiniteTimeSpan, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [TestMethod]
    public async Task DisposeFailsPendingWaiters()
    {
        var ln = new FakeLocoNet();
        var awaiter = new LongAckAwaiter(ln);

        var task = awaiter.SendAndAwaitAckAsync(LnMsg.Make(OpCode.SwReq, 0x01, 0x00), Timeout.InfiniteTimeSpan);
        awaiter.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => task);
    }
}
