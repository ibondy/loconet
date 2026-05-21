using LocoNet.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocoNet.Net.Tests;

[TestClass]
public class OpCodeExtensionsTests
{
    [TestMethod]
    [DataRow(0x83, 2)] // OPC_GPON (2-byte)
    [DataRow(0x82, 2)] // OPC_GPOFF
    [DataRow(0xA0, 4)] // OPC_LOCO_SPD (4-byte)
    [DataRow(0xBF, 4)] // OPC_LOCO_ADR
    [DataRow(0xD0, 6)] // OPC_MULTI_SENSE (6-byte)
    public void PacketSize_FixedLength_IgnoresSizeByte(int opc, int expected)
    {
        Assert.Equal(expected, OpCodeExtensions.PacketSize((byte)opc, 0));
        Assert.Equal(expected, OpCodeExtensions.PacketSize((byte)opc, 0xFF));
    }

    [TestMethod]
    [DataRow(0xE5, 0x0F, 15)] // peer-xfer (LNCV)
    [DataRow(0xE5, 0x10, 16)] // peer-xfer (LNSV)
    [DataRow(0xE7, 0x0E, 14)] // slot-data
    [DataRow(0xEF, 0x0E, 14)] // write slot data
    public void PacketSize_VariableLength_ReturnsSizeByte(int opc, int sz, int expected)
    {
        Assert.Equal(expected, OpCodeExtensions.PacketSize((byte)opc, (byte)sz));
    }

    [TestMethod]
    public void OpCodeMask_ClearsHighBit()
    {
        Assert.Equal(0x65, (byte)OpCode.PeerXfer & OpCodeExtensions.OpCodeMask);
        Assert.Equal(0x6F, (byte)OpCode.WrSlData & OpCodeExtensions.OpCodeMask);
    }
}
