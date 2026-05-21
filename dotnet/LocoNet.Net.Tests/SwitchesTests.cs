using LocoNet.Net;
using Xunit;

namespace LocoNet.Net.Tests;

public class SwitchesTests
{
    [Theory]
    [InlineData((ushort)1)]
    [InlineData((ushort)2)]
    [InlineData((ushort)128)]
    [InlineData((ushort)1024)]
    [InlineData((ushort)2048)]
    public void MakeSwitchRequest_RoundTrip(ushort address)
    {
        var on  = Switches.MakeSwitchRequest(address, output: true,  thrown: true);
        var off = Switches.MakeSwitchRequest(address, output: false, thrown: false);

        Assert.Equal(OpCode.SwReq, on.OpCode);
        Assert.Equal(4, on.Length);

        var (addrOn, outOn, dirOn)    = Switches.DecodeSwitchRequest(on);
        var (addrOff, outOff, dirOff) = Switches.DecodeSwitchRequest(off);

        Assert.Equal(address, addrOn);
        Assert.Equal(address, addrOff);
        Assert.True(outOn);
        Assert.False(outOff);
        // DIR bit is set when CLOSED (thrown == false); see Digitrax convention.
        Assert.False(dirOn);
        Assert.True(dirOff);
    }

    [Fact]
    public void RequestSwitch_SendsThroughTransport()
    {
        var ln = new FakeLocoNet();
        Switches.RequestSwitch(ln, 42, output: true, thrown: true);
        var sent = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.SwReq, sent.OpCode);
        var (addr, output, _) = Switches.DecodeSwitchRequest(sent);
        Assert.Equal(42, addr);
        Assert.True(output);
    }

    [Fact]
    public void ReportSwitch_SendsSwState()
    {
        var ln = new FakeLocoNet();
        Switches.ReportSwitch(ln, 200);
        var sent = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.SwState, sent.OpCode);
        // Address encoding matches SwReq.
        var (addr, _, _) = Switches.DecodeSwitchRequest(sent);
        Assert.Equal(200, addr);
    }

    [Theory]
    [InlineData((ushort)1, true)]
    [InlineData((ushort)2, false)]
    [InlineData((ushort)3, true)]
    [InlineData((ushort)100, true)]
    [InlineData((ushort)1024, false)]
    public void ReportSensor_RoundTrip(ushort address, bool state)
    {
        var ln = new FakeLocoNet();
        Switches.ReportSensor(ln, address, state);
        var sent = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.InputRep, sent.OpCode);

        var (decAddr, decState) = Switches.DecodeInputReport(sent);
        Assert.Equal(address, decAddr);
        Assert.Equal(state, decState);
    }

    [Fact]
    public void ReportPower_OnAndOff()
    {
        var ln = new FakeLocoNet();
        Switches.ReportPower(ln, true);
        Switches.ReportPower(ln, false);
        Assert.Equal(OpCode.GpOn, ln.Sent[0].OpCode);
        Assert.Equal(OpCode.GpOff, ln.Sent[1].OpCode);
    }

    [Fact]
    public void DecodeSwitchReport_ReturnsAddressAndBits()
    {
        // Build an OPC_SW_REP frame manually with address=5, HI=1, SW=1
        ushort addr = (ushort)(5 - 1);
        byte sw1 = (byte)(addr & 0x7F);
        byte sw2 = (byte)(((addr >> 7) & 0x0F) | LnConstants.SwRepHi | LnConstants.SwRepSw);
        var msg = LnMsg.FromPayload(new byte[] { (byte)OpCode.SwRep, sw1, sw2 });

        var (a, hi, sw) = Switches.DecodeSwitchReport(msg);
        Assert.Equal(5, a);
        Assert.True(hi);
        Assert.True(sw);
    }

    [Theory]
    [InlineData(false)] // output form: SW_REP_INPUTS bit (0x40) clear
    [InlineData(true)]  // input form:  SW_REP_INPUTS bit (0x40) set
    public void DecodeSwitchReport_CarriesInputFormBitInSw2(bool inputForm)
    {
        // SW2 bit 6 (0x40) distinguishes the two forms of OPC_SW_REP per spec; the decoder
        // returns the raw value, so callers can inspect it.
        ushort addr = (ushort)(10 - 1);
        byte sw1 = (byte)(addr & 0x7F);
        byte sw2 = (byte)((addr >> 7) & 0x0F);
        if (inputForm) sw2 |= LnConstants.SwRepInputs;
        var msg = LnMsg.FromPayload(new byte[] { (byte)OpCode.SwRep, sw1, sw2 });

        Assert.Equal(inputForm, (msg[2] & LnConstants.SwRepInputs) != 0);
        var (a, _, _) = Switches.DecodeSwitchReport(msg);
        Assert.Equal(10, a);
    }
}
