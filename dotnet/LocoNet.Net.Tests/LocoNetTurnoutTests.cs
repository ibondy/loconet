using LocoNet.Net;
using Xunit;

namespace LocoNet.Net.Tests;

public class LocoNetTurnoutTests
{
    [Theory]
    [InlineData((ushort)1, true)]
    [InlineData((ushort)1, false)]
    [InlineData((ushort)2048, true)]
    public void SetState_SendsSwReqWithOutputAndAddress(ushort address, bool thrown)
    {
        var ln = new FakeLocoNet();
        var tn = new LocoNetTurnout(ln) { Address = address };

        tn.SetState(thrown);

        var sent = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.SwReq, sent.OpCode);
        var (addr, output, _) = Switches.DecodeSwitchRequest(sent);
        Assert.Equal(address, addr);
        Assert.True(output, "Turnout SetState always energizes the coil (output=true).");
        Assert.Equal(thrown, tn.Thrown);
    }
}
