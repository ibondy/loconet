using LocoNet.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocoNet.Net.Tests;

[TestClass]
public class MessagesTests
{
    [TestMethod]
    public void SlotDataMsg_AddressCombination()
    {
        Span<byte> payload = stackalloc byte[13];
        payload[0] = (byte)OpCode.SlRdData;
        payload[1] = 0x0E;
        payload[2] = 5;            // slot
        payload[4] = 0x05;         // addr low
        payload[9] = 0x0A;         // addr high (7-bit)
        payload[11] = 0x21;        // id1
        payload[12] = 0x07;        // id2
        var msg = LnMsg.FromPayload(payload);
        var sd = new SlotDataMsg(msg);

        Assert.Equal((0x0A << 7) | 0x05, sd.Address);
        Assert.Equal((0x07 << 7) | 0x21, sd.ThrottleId);
        Assert.Equal(5, sd.Slot);
    }

    [TestMethod]
    public void SlotDataMsg_ToWrSlData_RebuildsWithOverridesAndChecksum()
    {
        Span<byte> payload = stackalloc byte[13];
        payload[0] = (byte)OpCode.SlRdData;
        payload[1] = 0x0E;
        payload[2] = 9;
        payload[3] = 0x33;
        payload[4] = 0x12;
        payload[9] = 0x01;
        var orig = LnMsg.FromPayload(payload);
        var sd = new SlotDataMsg(orig);

        var wr = sd.ToWrSlData(stat: 0x77, id1: 0x44, id2: 0x55);
        Assert.Equal(OpCode.WrSlData, wr.OpCode);
        Assert.Equal(14, wr.Length);
        Assert.Equal(0x77, wr[3]);
        Assert.Equal(0x44, wr[11]);
        Assert.Equal(0x55, wr[12]);
        // Address fields preserved
        Assert.Equal(0x12, wr[4]);
        Assert.Equal(0x01, wr[9]);
    }

    [TestMethod]
    public void LocoDataMsg_RejectsWrongLength()
    {
        // GpOn (0x83) is a 2-byte message; LocoDataMsg expects 14 bytes.
        Span<byte> payload = stackalloc byte[1];
        payload[0] = (byte)OpCode.GpOn;
        var two = LnMsg.FromPayload(payload);
        Assert.Throws<ArgumentException>(() => new LocoDataMsg(two));
    }

    [TestMethod]
    public void LongAckMsg_ExposesOpcodeAndAck()
    {
        var lack = LnMsg.MakeLongAck((byte)OpCode.LocoAdr, 0x55);
        var m = new LongAckMsg(lack);
        Assert.Equal((byte)OpCode.LocoAdr & 0x7F, m.Opcode);
        Assert.Equal(0x55, m.Ack);
    }

    [TestMethod]
    public void SlotDataMsg_RejectsWrongOpcode()
    {
        // OPC_GPON is a 2-byte message — wrong opcode and wrong length for slot data.
        var bad = LnMsg.FromPayload(stackalloc byte[] { (byte)OpCode.GpOn });
        Assert.Throws<ArgumentException>(() => new SlotDataMsg(bad));
    }

    [TestMethod]
    public void LongAckMsg_RejectsWrongOpcode()
    {
        var bad = LnMsg.Make(OpCode.LocoSpd, 0x00, 0x00);
        Assert.Throws<ArgumentException>(() => new LongAckMsg(bad));
    }
}
