using LocoNet.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocoNet.Net.Tests;

[TestClass]
public class LocoNetCVAccessTests
{
    [TestMethod]
    public void CfgRead_OkResult_SendsResponse()
    {
        var ln = new FakeLocoNet();
        using var cv = new LocoNetCVAccess(ln)
        {
            CvRead = (dev, num) => (LncvResult.Ok, (ushort)(num * 2))
        };

        // CFG_READ request: source=0x11, deviceClass=5, lncvNumber=10
        ln.Receive(BuildLncvRequest(src: 0x11, deviceClass: 5, lncv: 10, value: 0,
                                    reqId: LncvMessage.ReqIdCfgRead, opc: OpCode.PeerXfer));

        var resp = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.PeerXfer, resp.OpCode);
        Assert.Equal(15, resp.Length);
        Assert.Equal(LncvMessage.ReqIdCfgRead, LncvMessage.ReqId(resp));
        Assert.Equal(20, LncvMessage.LncvValue(resp));
    }

    [TestMethod]
    public void CfgRead_PositiveErrorCode_SendsLongAck()
    {
        var ln = new FakeLocoNet();
        using var cv = new LocoNetCVAccess(ln)
        {
            CvRead = (_, _) => (LncvResult.Unsupported, (ushort)0)
        };
        ln.Receive(BuildLncvRequest(0, 0, 0, 0, LncvMessage.ReqIdCfgRead, OpCode.PeerXfer));

        var resp = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.LongAck, resp.OpCode);
        Assert.Equal((byte)OpCode.PeerXfer & 0x7F, resp[1]);
        Assert.Equal((byte)LncvResult.Unsupported, resp[2]);
    }

    [TestMethod]
    public void CfgRead_NoReplyResult_SendsNothing()
    {
        var ln = new FakeLocoNet();
        using var cv = new LocoNetCVAccess(ln)
        {
            CvRead = (_, _) => (LncvResult.NoReply, (ushort)0)
        };
        ln.Receive(BuildLncvRequest(0, 0, 0, 0, LncvMessage.ReqIdCfgRead, OpCode.PeerXfer));
        Assert.Empty(ln.Sent);
    }

    [TestMethod]
    public void CfgWrite_OkResult_SendsLongAck()
    {
        var ln = new FakeLocoNet();
        ushort capturedValue = 0;
        using var cv = new LocoNetCVAccess(ln)
        {
            CvWrite = (_, _, v) => { capturedValue = v; return LncvResult.Ok; }
        };
        ln.Receive(BuildLncvRequest(0, 99, 7, 0xBEEF, LncvMessage.ReqIdCfgWrite, OpCode.PeerXfer));

        var resp = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.LongAck, resp.OpCode);
        Assert.Equal(0, resp[2]);
        Assert.Equal(0xBEEF, capturedValue);
    }

    internal static LnMsg BuildLncvRequest(byte src, ushort deviceClass, ushort lncv, ushort value, byte reqId, OpCode opc)
    {
        Span<byte> data = stackalloc byte[7];
        data[0] = (byte)(deviceClass & 0xFF);
        data[1] = (byte)(deviceClass >> 8);
        data[2] = (byte)(lncv & 0xFF);
        data[3] = (byte)(lncv >> 8);
        data[4] = (byte)(value & 0xFF);
        data[5] = (byte)(value >> 8);
        data[6] = 0;

        Span<byte> frame = stackalloc byte[14];
        frame[0] = (byte)opc;
        frame[1] = 0x0F;
        frame[2] = src;
        frame[3] = 0;
        frame[4] = 0;
        frame[5] = reqId;
        LncvMessage.EncodeData(data, frame[6..]);
        return LnMsg.FromPayload(frame);
    }
}
