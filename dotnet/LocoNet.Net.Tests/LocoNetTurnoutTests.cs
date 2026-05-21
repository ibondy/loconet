using LocoNet.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocoNet.Net.Tests;

[TestClass]
public class LocoNetTurnoutTests
{
    [TestMethod]
    [DataRow((ushort)1, true)]
    [DataRow((ushort)1, false)]
    [DataRow((ushort)2048, true)]
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
