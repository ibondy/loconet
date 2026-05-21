using LocoNet.Net;
using Xunit;

namespace LocoNet.Net.Tests;

public class OpCodeExtensionsTests
{
    [Theory]
    [InlineData(0x83, 2)] // OPC_GPON (2-byte)
    [InlineData(0x82, 2)] // OPC_GPOFF
    [InlineData(0xA0, 4)] // OPC_LOCO_SPD (4-byte)
    [InlineData(0xBF, 4)] // OPC_LOCO_ADR
    [InlineData(0xD0, 6)] // OPC_MULTI_SENSE (6-byte)
    public void PacketSize_FixedLength_IgnoresSizeByte(byte opc, int expected)
    {
        Assert.Equal(expected, OpCodeExtensions.PacketSize(opc, 0));
        Assert.Equal(expected, OpCodeExtensions.PacketSize(opc, 0xFF));
    }

    [Theory]
    [InlineData(0xE5, 0x0F, 15)] // peer-xfer (LNCV)
    [InlineData(0xE5, 0x10, 16)] // peer-xfer (LNSV)
    [InlineData(0xE7, 0x0E, 14)] // slot-data
    [InlineData(0xEF, 0x0E, 14)] // write slot data
    public void PacketSize_VariableLength_ReturnsSizeByte(byte opc, byte sz, int expected)
    {
        Assert.Equal(expected, OpCodeExtensions.PacketSize(opc, sz));
    }

    [Fact]
    public void OpCodeMask_ClearsHighBit()
    {
        Assert.Equal(0x65, (byte)OpCode.PeerXfer & OpCodeExtensions.OpCodeMask);
        Assert.Equal(0x6F, (byte)OpCode.WrSlData & OpCodeExtensions.OpCodeMask);
    }
}
