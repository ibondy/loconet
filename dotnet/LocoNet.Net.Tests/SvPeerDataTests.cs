using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocoNet.Net.Tests;

[TestClass]
public class SvPeerDataTests
{
    [TestMethod]
    public void EncodeDecode_RoundTrips_AllHighBits()
    {
        // Data with every high bit set so PXCT must carry all 8 bits across both groups.
        byte[] data = { 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88 };
        var frame = new byte[15];
        // Caller is expected to set the slot/cmd/type/svx headers; for the encode/decode
        // round-trip we only care about the data area.
        SvPeerData.Encode(frame, data);

        // Verify low bits are masked to 7 bits in the d-slots.
        for (int i = 6; i <= 9; i++) Assert.True((frame[i] & 0x80) == 0);
        for (int i = 11; i <= 14; i++) Assert.True((frame[i] & 0x80) == 0);

        // svx1 must have all 4 low bits set, ditto svx2.
        Assert.Equal(0x0F, frame[5] & 0x0F);
        Assert.Equal(0x0F, frame[10] & 0x0F);

        var decoded = new byte[8];
        SvPeerData.Decode(frame, decoded);
        Assert.Equal(data, decoded);
    }

    [TestMethod]
    public void EncodeDecode_RoundTrips_NoHighBits()
    {
        byte[] data = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        var frame = new byte[15];
        SvPeerData.Encode(frame, data);

        Assert.Equal(0, frame[5] & 0x0F);
        Assert.Equal(0, frame[10] & 0x0F);

        var decoded = new byte[8];
        SvPeerData.Decode(frame, decoded);
        Assert.Equal(data, decoded);
    }

    [TestMethod]
    public void EncodeDecode_RoundTrips_MixedHighBits()
    {
        byte[] data = { 0x01, 0x82, 0x03, 0x84, 0x85, 0x06, 0x87, 0x08 };
        var frame = new byte[15];
        SvPeerData.Encode(frame, data);
        var decoded = new byte[8];
        SvPeerData.Decode(frame, decoded);
        Assert.Equal(data, decoded);
    }
}
