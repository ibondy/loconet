using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocoNet.Net.Tests;

[TestClass]
public class LncvMessageTests
{
    [TestMethod]
    public void MakeResponse_ProducesParseableFrame()
    {
        var msg = LncvMessage.MakeResponse(src: 0x42, deviceClass: 0x1234, cv: 0x00FF, value: 0xBEEF, flags: 0x80);

        Assert.True(LncvMessage.IsLncv(msg));
        Assert.Equal(OpCode.PeerXfer, msg.OpCode);
        Assert.Equal(15, msg.Length);
        Assert.Equal(LncvMessage.SrcModule, LncvMessage.Source(msg));
        Assert.Equal(0x42, LncvMessage.DestLow(msg));
        Assert.Equal(0x00, LncvMessage.DestHigh(msg));
        Assert.Equal(LncvMessage.ReqIdCfgRead, LncvMessage.ReqId(msg));
        Assert.Equal(0x1234, LncvMessage.DeviceClass(msg));
        Assert.Equal(0x00FF, LncvMessage.LncvNumber(msg));
        Assert.Equal(0xBEEF, LncvMessage.LncvValue(msg));
        Assert.Equal(0x80, LncvMessage.Flags(msg));
    }

    [TestMethod]
    public void EncodeData_RestoresHighBitsViaPxct()
    {
        byte[] data = { 0x80, 0x01, 0xFF, 0x7F, 0x00, 0x88, 0x91 };
        byte[] buffer = new byte[8];
        LncvMessage.EncodeData(data, buffer);

        // Payload bytes have their high bits cleared; PXCT carries them.
        byte pxct = buffer[0];
        byte mask = 0x01;
        for (int i = 0; i < 7; i++)
        {
            byte stored = buffer[1 + i];
            Assert.True((stored & 0x80) == 0);
            byte expectedBit = (byte)((data[i] & 0x80) != 0 ? mask : 0);
            Assert.Equal(expectedBit, pxct & mask);
            mask <<= 1;
        }
    }

    [TestMethod]
    public void IsLncv_RejectsWrongOpcode()
    {
        var notLncv = LnMsg.Make(OpCode.LocoSpd, 0x01, 0x02);
        Assert.False(LncvMessage.IsLncv(notLncv));
    }
}
