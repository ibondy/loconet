using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocoNet.Net.Tests;

[TestClass]
public class LocoNetMessageBufferTests
{
    [TestMethod]
    public void ParsesSingleCompleteFrame()
    {
        var buffer = new LocoNetMessageBuffer();
        var msg = LnMsg.Make(OpCode.LocoSpd, 0x05, 0x40);

        LnMsg? last = null;
        foreach (var b in msg.Bytes)
        {
            last = buffer.AddByte(b);
        }

        Assert.NotNull(last);
        Assert.Equal(msg, last!.Value);
        Assert.Equal(1ul, buffer.Stats.RxPackets);
        Assert.Equal(0ul, buffer.Stats.RxErrors);
    }

    [TestMethod]
    public void ResyncsOnNewOpcodeMidFrame()
    {
        var buffer = new LocoNetMessageBuffer();
        // Begin a 4-byte frame and then drop a fresh opcode before completing.
        buffer.AddByte((byte)OpCode.LocoSpd);
        buffer.AddByte(0x05);
        // Inject a new opcode (high bit set) — framer should resync.
        var good = LnMsg.Make(OpCode.LocoDirf, 0x02, 0x10);
        LnMsg? last = null;
        foreach (var b in good.Bytes)
        {
            last = buffer.AddByte(b);
        }
        Assert.NotNull(last);
        Assert.Equal(good, last!.Value);
    }

    [TestMethod]
    public void IncrementsErrorCountOnBadChecksum()
    {
        var buffer = new LocoNetMessageBuffer();
        // Valid 4-byte frame layout but flip a low bit of the checksum (keep high bit clear so
        // we exercise the bad-checksum path rather than triggering a resync).
        var bytes = LnMsg.Make(OpCode.LocoSpd, 0x05, 0x40).Bytes.ToArray();
        bytes[^1] ^= 0x01;
        LnMsg? last = null;
        foreach (var b in bytes) last = buffer.AddByte(b);

        Assert.Null(last);
        Assert.Equal(0ul, buffer.Stats.RxPackets);
        Assert.Equal(1ul, buffer.Stats.RxErrors);
    }

    [TestMethod]
    public void ParsesMultipleFramesViaAddBytes()
    {
        var buffer = new LocoNetMessageBuffer();
        var m1 = LnMsg.Make(OpCode.LocoSpd, 0x05, 0x40);
        var m2 = LnMsg.MakeLongAck((byte)OpCode.PeerXfer, 0x7F);

        var stream = new List<byte>();
        stream.AddRange(m1.Bytes.ToArray());
        stream.AddRange(m2.Bytes.ToArray());

        var got = new List<LnMsg>();
        buffer.AddBytes(stream.ToArray(), got.Add);

        Assert.Collection(got,
            x => Assert.Equal(m1, x),
            x => Assert.Equal(m2, x));
        Assert.Equal(2ul, buffer.Stats.RxPackets);
    }

    [TestMethod]
    public void HandlesVariableLengthFrames()
    {
        // OPC_PEER_XFER (0xE5) carries its own length in byte [1]. Use a 15-byte LNCV-style frame.
        var payload = new byte[14];
        payload[0] = (byte)OpCode.PeerXfer;
        payload[1] = 0x0F;
        for (int i = 2; i < payload.Length; i++) payload[i] = (byte)(i & 0x7F);
        var msg = LnMsg.FromPayload(payload);

        var buffer = new LocoNetMessageBuffer();
        LnMsg? last = null;
        foreach (var b in msg.Bytes) last = buffer.AddByte(b);

        Assert.NotNull(last);
        Assert.Equal(msg, last!.Value);
    }
}
