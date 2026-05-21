using LocoNet.Net;
using Xunit;

namespace LocoNet.Net.Tests;

public class LocoNetCVTests
{
    [Fact]
    public void Discovery_OkResult_SendsResponse()
    {
        var ln = new FakeLocoNet();
        using var cv = new LocoNetCV(ln)
        {
            OnDiscovery = () => (LncvResult.Ok, (ushort)0x1234, (ushort)0x4567)
        };

        // Discovery request: deviceClass=0xFFFF, lncv=0, value=0xFFFF, ImmPacket opcode, ReqIdCfgRequest
        var req = LocoNetCVAccessTests.BuildLncvRequest(
            src: 0x22, deviceClass: 0xFFFF, lncv: 0x0000, value: 0xFFFF,
            reqId: LncvMessage.ReqIdCfgRequest, opc: OpCode.ImmPacket);
        ln.Receive(req);

        var resp = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.PeerXfer, resp.OpCode);
        Assert.Equal(0x1234, LncvMessage.DeviceClass(resp));
        Assert.Equal(0x4567, LncvMessage.LncvValue(resp));
    }

    [Fact]
    public void Discovery_NoCallback_SendsNothing()
    {
        var ln = new FakeLocoNet();
        using var cv = new LocoNetCV(ln);
        ln.Receive(LocoNetCVAccessTests.BuildLncvRequest(
            0, 0xFFFF, 0, 0xFFFF, LncvMessage.ReqIdCfgRequest, OpCode.ImmPacket));
        Assert.Empty(ln.Sent);
    }

    [Fact]
    public void ProgrammingStart_OkResult_SendsResponse()
    {
        var ln = new FakeLocoNet();
        using var cv = new LocoNetCV(ln)
        {
            OnProgrammingStart = () => (LncvResult.Ok, (ushort)0xAA, (ushort)0xBB)
        };

        // CFG_REQUEST with flag PROG_ON in d[6]: build via raw payload to set flag byte.
        var req = BuildLncvRequestWithFlag(src: 0x33, deviceClass: 0xAA, lncv: 0, value: 0xBB,
                                            flag: LncvMessage.FlagProgOn);
        ln.Receive(req);

        var resp = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.PeerXfer, resp.OpCode);
        Assert.Equal(LncvMessage.FlagProgOn, LncvMessage.Flags(resp));
    }

    [Fact]
    public void ProgrammingStop_InvokesCallback_NoSend()
    {
        var ln = new FakeLocoNet();
        ushort? gotDev = null;
        ushort? gotVal = null;
        using var cv = new LocoNetCV(ln)
        {
            OnProgrammingStop = (dev, v) => { gotDev = dev; gotVal = v; }
        };

        var req = BuildLncvRequestWithFlag(src: 0x44, deviceClass: 0x99, lncv: 0, value: 0x11,
                                            flag: LncvMessage.FlagProgOff);
        ln.Receive(req);

        Assert.Equal((ushort)0x99, gotDev);
        Assert.Equal((ushort)0x11, gotVal);
        Assert.Empty(ln.Sent);
    }

    private static LnMsg BuildLncvRequestWithFlag(byte src, ushort deviceClass, ushort lncv, ushort value, byte flag)
    {
        Span<byte> data = stackalloc byte[7];
        data[0] = (byte)(deviceClass & 0xFF);
        data[1] = (byte)(deviceClass >> 8);
        data[2] = (byte)(lncv & 0xFF);
        data[3] = (byte)(lncv >> 8);
        data[4] = (byte)(value & 0xFF);
        data[5] = (byte)(value >> 8);
        data[6] = flag;

        Span<byte> frame = stackalloc byte[14];
        frame[0] = (byte)OpCode.ImmPacket;
        frame[1] = 0x0F;
        frame[2] = src;
        frame[3] = 0;
        frame[4] = 0;
        frame[5] = LncvMessage.ReqIdCfgRequest;
        LncvMessage.EncodeData(data, frame[6..]);
        return LnMsg.FromPayload(frame);
    }
}
