using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocoNet.Net.Tests;

[TestClass]
public class LnMsgTests
{
    [TestMethod]
    public void FromPayload_AppendsCorrectChecksum()
    {
        // OPC_GPON = 0x83, no payload bytes => 2 bytes total (opcode + checksum).
        var msg = LnMsg.FromPayload(stackalloc byte[] { (byte)OpCode.GpOn });
        Assert.Equal(2, msg.Length);
        Assert.Equal(OpCode.GpOn, msg.OpCode);

        // Checksum seed XOR opcode = 0xFF ^ 0x83 = 0x7C.
        Assert.Equal(0x7C, msg[1]);
    }

    [TestMethod]
    public void FromBytes_RoundTrips_FromPayload()
    {
        var built = LnMsg.Make(OpCode.LocoSpd, 0x12, 0x34);
        var parsed = LnMsg.FromBytes(built.Bytes);
        Assert.Equal(built, parsed);
    }

    [TestMethod]
    public void FromBytes_RejectsBadChecksum()
    {
        var buf = new byte[] { (byte)OpCode.GpOn, 0x00 }; // wrong checksum
        Assert.Throws<ArgumentException>(() => LnMsg.FromBytes(buf));
    }

    [TestMethod]
    public void FromBytes_RejectsNonOpcodeFirstByte()
    {
        var buf = new byte[] { 0x12, 0x34 };
        Assert.Throws<ArgumentException>(() => LnMsg.FromBytes(buf));
    }

    [TestMethod]
    public void MakeLongAck_StripsHighBitOnReplyOpcode()
    {
        var ack = LnMsg.MakeLongAck(replyToOpc: (byte)OpCode.PeerXfer, ack: 0x7F);
        Assert.Equal(OpCode.LongAck, ack.OpCode);
        Assert.Equal((byte)OpCode.PeerXfer & 0x7F, ack[1]);
        Assert.Equal(0x7F, ack[2]);
    }

    [TestMethod]
    public void ComputeChecksum_MatchesManualXor()
    {
        byte[] payload = { 0xB4, 0x6D, 0x01 };
        byte expected = 0xFF;
        foreach (var b in payload) expected ^= b;
        Assert.Equal(expected, LnMsg.ComputeChecksum(payload));
    }

    [TestMethod]
    public void FromBytes_RejectsVariableOpcodeWithBufferShorterThanDeclaredSize()
    {
        // OPC_PEER_XFER (0xE5) declares its own length in byte [1]. Frame says 16 bytes but we
        // only supply 8 → must throw.
        var buf = new byte[8];
        buf[0] = (byte)OpCode.PeerXfer;
        buf[1] = 0x10; // declares 16 bytes
        // Pad with a plausible-looking checksum so we exercise the length check, not the cs check.
        buf[^1] = LnMsg.ComputeChecksum(buf.AsSpan(0, buf.Length - 1));
        Assert.Throws<ArgumentException>(() => LnMsg.FromBytes(buf));
    }

    [TestMethod]
    public void FromPayload_RejectsPayloadLengthInconsistentWithOpcode()
    {
        // OPC_GPON is a 2-byte (fixed) message. Pass a 4-byte-style payload → mismatch.
        var bad = new byte[] { (byte)OpCode.GpOn, 0x11, 0x22 };
        Assert.Throws<ArgumentException>(() => LnMsg.FromPayload(bad));
    }
}
