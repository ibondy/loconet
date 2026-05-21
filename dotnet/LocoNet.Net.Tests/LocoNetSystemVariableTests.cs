using LocoNet.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocoNet.Net.Tests;

[TestClass]
public class LocoNetSystemVariableTests
{
    private const byte MfgId = 13;
    private const byte DevId = 2;
    private const ushort ProductId = 0x4321;
    private const byte SwVersion = 7;

    private static (FakeLocoNet ln, InMemorySvStorage st, LocoNetSystemVariable sv) MakeServer(ushort nodeId = 0x0001)
    {
        var ln = new FakeLocoNet();
        var st = new InMemorySvStorage(256);
        var sv = new LocoNetSystemVariable(ln, st, MfgId, DevId, ProductId, SwVersion);
        sv.WriteNodeId(nodeId);
        ln.Sent.Clear();
        return (ln, st, sv);
    }

    private static LnMsg BuildSv(byte src, SvCommand cmd, ushort destinationId, ushort svAddress, ushort productId, ushort serial, byte d4 = 0, byte d5 = 0, byte d6 = 0, byte d7 = 0)
    {
        Span<byte> data = stackalloc byte[8];
        data[0] = (byte)(destinationId & 0xFF);
        data[1] = (byte)(destinationId >> 8);
        data[2] = (byte)(svAddress & 0xFF);
        data[3] = (byte)(svAddress >> 8);
        // For ChangeAddress, the protocol uses data[4..5] = productId and data[6..7] = serial.
        // For Read/Write Single, callers override these via d4..d7. Default productId/serial = 0
        // matches the previous behaviour and is overwritten when d4..d7 are non-zero.
        data[4] = productId != 0 ? (byte)(productId & 0xFF) : d4;
        data[5] = productId != 0 ? (byte)(productId >> 8)   : d5;
        data[6] = serial    != 0 ? (byte)(serial    & 0xFF) : d6;
        data[7] = serial    != 0 ? (byte)(serial    >> 8)   : d7;

        Span<byte> frame = stackalloc byte[15];
        frame[0] = (byte)OpCode.PeerXfer;
        frame[1] = 0x10;
        frame[2] = src;
        frame[3] = (byte)cmd;
        frame[4] = 0x02;
        frame[5] = 0x10;
        frame[10] = 0x10;

        // Override the second product/serial fields if provided differently from data.
        // Repack via SvPeerData equivalent: since SvPeerData is internal we manually re-encode here.
        // Easier: rely on the same logic — encode via a helper that mirrors SvPeerData.
        EncodePeerData(frame, data);
        return LnMsg.FromPayload(frame);

        static void EncodePeerData(Span<byte> f, ReadOnlySpan<byte> d)
        {
            f[5] = (byte)(f[5] & 0xF0); f[10] = (byte)(f[10] & 0xF0);
            byte mask = 0x01;
            int outIdx = 6, bitsIdx = 5;
            for (int i = 0; i < 8; i++)
            {
                byte b = d[i];
                f[outIdx] = (byte)(b & 0x7F);
                if ((b & 0x80) != 0) f[bitsIdx] |= mask;
                if (i == 3) { mask = 0x01; outIdx = 11; bitsIdx = 10; }
                else { mask <<= 1; outIdx++; }
            }
        }
    }

    [TestMethod]
    public void ReadSingle_ReturnsStoredByte()
    {
        var (ln, st, sv) = MakeServer();
        st.Write(SvAddr.UserBase + 0, 0xAB);

        ln.Receive(BuildSv(src: 0x10, cmd: SvCommand.ReadSingle,
            destinationId: sv.ReadNodeId(),
            svAddress: SvAddr.UserBase, productId: 0, serial: 0));

        var resp = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.PeerXfer, resp.OpCode);
        Assert.Equal(16, resp.Length);
        // Reply bit set in sv_cmd
        Assert.Equal(0x40 | (byte)SvCommand.ReadSingle, resp[3]);
        // d[4] in the SV payload is the value: it lives in frame index... decode it.
        DecodePayload(resp, out var data);
        Assert.Equal(0xAB, data[4]);
    }

    [TestMethod]
    public void WriteSingle_StoresValueAndRepliesWithReadback()
    {
        var (ln, st, sv) = MakeServer();
        ln.Receive(BuildSv(0x10, SvCommand.WriteSingle, sv.ReadNodeId(), SvAddr.UserBase, 0, 0, d4: 0x55));

        Assert.Equal(0x55, st.Read(SvAddr.UserBase));
        var resp = Assert.Single(ln.Sent);
        DecodePayload(resp, out var data);
        Assert.Equal(0x55, data[4]);
    }

    [TestMethod]
    public void WriteMasked_ChangesOnlyBitsInMask()
    {
        var (ln, st, sv) = MakeServer();
        st.Write(SvAddr.UserBase, 0b1010_1010);
        // d4 = new bits, d5 = mask
        ln.Receive(BuildSv(0x10, SvCommand.WriteMasked, sv.ReadNodeId(), SvAddr.UserBase, 0, 0,
            d4: 0b0000_1111, d5: 0b0000_1111));

        Assert.Equal(0b1010_1111, st.Read(SvAddr.UserBase));
    }

    [TestMethod]
    public void WriteQuad_StoresFourBytes()
    {
        var (ln, st, sv) = MakeServer();
        ln.Receive(BuildSv(0x10, SvCommand.WriteQuad, sv.ReadNodeId(), SvAddr.UserBase, 0, 0,
            d4: 1, d5: 2, d6: 3, d7: 4));

        Assert.Equal(1, st.Read(SvAddr.UserBase + 0));
        Assert.Equal(2, st.Read(SvAddr.UserBase + 1));
        Assert.Equal(3, st.Read(SvAddr.UserBase + 2));
        Assert.Equal(4, st.Read(SvAddr.UserBase + 3));
    }

    [TestMethod]
    public void ReadSingle_OutOfRange_SendsLongAck42()
    {
        var (ln, _, sv) = MakeServer();
        ln.Receive(BuildSv(0x10, SvCommand.ReadSingle, sv.ReadNodeId(),
            svAddress: 0x0000, productId: 0, serial: 0)); // < EepromSize → invalid

        var resp = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.LongAck, resp.OpCode);
        Assert.Equal(42, resp[2]);
    }

    [TestMethod]
    public void Discover_RequiresDeferredProcessing()
    {
        var (ln, _, sv) = MakeServer();
        var status = sv.ProcessMessage(BuildSv(0x77, SvCommand.Discover, 0, 0, 0, 0));
        Assert.Equal(SvStatus.DeferredProcessingNeeded, status);
        Assert.Empty(ln.Sent);

        sv.DoDeferredProcessing();
        var resp = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.PeerXfer, resp.OpCode);
        Assert.Equal(0x40 | (byte)SvCommand.Discover, resp[3]);
    }

    [TestMethod]
    public void Identify_FillsMfgDevProductSerial()
    {
        var (ln, st, sv) = MakeServer(nodeId: 0xCAFE);
        st.Write(SvAddr.SerialNumberL, 0x21);
        st.Write(SvAddr.SerialNumberH, 0x43);

        ln.Receive(BuildSv(0x10, SvCommand.Identify, sv.ReadNodeId(), 0, 0, 0));

        var resp = Assert.Single(ln.Sent);
        DecodePayload(resp, out var data);
        Assert.Equal(0xFE, data[0]); // node id low
        Assert.Equal(0xCA, data[1]); // node id high
        Assert.Equal(MfgId, data[2]);
        Assert.Equal(DevId, data[3]);
        Assert.Equal(ProductId & 0xFF, data[4]);
        Assert.Equal(ProductId >> 8, data[5]);
        Assert.Equal(0x21, data[6]);
        Assert.Equal(0x43, data[7]);
    }

    [TestMethod]
    public void ChangeAddress_MatchingMfgDevSerial_UpdatesNodeId()
    {
        var (ln, st, sv) = MakeServer(nodeId: 0x0001);
        st.Write(SvAddr.SerialNumberL, 0x01);
        st.Write(SvAddr.SerialNumberH, 0x02);
        // svAddress packs mfgId in low byte and devId in high byte
        ushort addr = (ushort)(MfgId | (DevId << 8));
        ushort serial = 0x0201; // matches stored
        ln.Receive(BuildSv(0x10, SvCommand.ChangeAddress, 0xBEEF, addr, ProductId, serial));

        Assert.Equal(0xBEEF, sv.ReadNodeId());
        Assert.Single(ln.Sent); // a reply
    }

    [TestMethod]
    public void ChangeAddress_WrongMfg_NotConsumed()
    {
        var (ln, _, sv) = MakeServer();
        ushort addr = (ushort)(0xAA | (DevId << 8));
        var status = sv.ProcessMessage(BuildSv(0, SvCommand.ChangeAddress, 0xBEEF, addr, ProductId, 0));
        Assert.Equal(SvStatus.NotConsumed, status);
        Assert.Empty(ln.Sent);
    }

    [TestMethod]
    public void Reconfigure_RaisesEventAfterReply()
    {
        var (ln, _, sv) = MakeServer();
        int calls = 0;
        sv.Reconfigure += () => calls++;
        ln.Receive(BuildSv(0x10, SvCommand.Reconfigure, sv.ReadNodeId(), 0, 0, 0));
        Assert.Single(ln.Sent);
        Assert.Equal(1, calls);
    }

    [TestMethod]
    public void WrongDestinationId_NotConsumed()
    {
        var (ln, _, sv) = MakeServer(nodeId: 0x1111);
        ln.Receive(BuildSv(0x10, SvCommand.ReadSingle, destinationId: 0x9999, svAddress: SvAddr.UserBase, productId: 0, serial: 0));
        Assert.Empty(ln.Sent);
    }

    [TestMethod]
    public void WriteStorage_RaisesSvChanged()
    {
        var (_, _, sv) = MakeServer();
        ushort gotOffset = 0;
        byte gotOld = 0, gotNew = 0;
        sv.SvChanged += (o, oldV, newV) => { gotOffset = o; gotOld = oldV; gotNew = newV; };
        sv.WriteStorage(SvAddr.UserBase + 4, 0x77);
        Assert.Equal(SvAddr.UserBase + 4, gotOffset);
        Assert.Equal(0, gotOld);
        Assert.Equal(0x77, gotNew);
    }

    private static void DecodePayload(LnMsg msg, out byte[] data)
    {
        data = new byte[8];
        byte mask = 0x01;
        int inIdx = 6, bitsIdx = 5;
        for (int i = 0; i < 8; i++)
        {
            byte b = msg[inIdx];
            if ((msg[bitsIdx] & mask) != 0) b |= 0x80;
            data[i] = b;
            if (i == 3) { mask = 0x01; inIdx = 11; bitsIdx = 10; }
            else { mask <<= 1; inIdx++; }
        }
    }
}
