using LocoNet.Net;
using Xunit;

namespace LocoNet.Net.Tests;

public class LocoNetFastClockTests
{
    [Fact]
    public void Poll_SendsRqSlDataForFcSlot()
    {
        var ln = new FakeLocoNet();
        using var fc = new LocoNetFastClock(ln, dcs100CompatibleSpeed: false, correctDcs100Clock: false);
        fc.Poll();

        var sent = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.RqSlData, sent.OpCode);
        Assert.Equal(LnConstants.FastClockSlot, sent[1]);
        Assert.Equal(0, sent[2]);
    }

    [Fact]
    public void Process66ms_FromIdle_SendsRequest()
    {
        var ln = new FakeLocoNet();
        using var fc = new LocoNetFastClock(ln, false, false);
        fc.Process66msActions();

        var sent = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.RqSlData, sent.OpCode);
        Assert.Equal(LnConstants.FastClockSlot, sent[1]);
    }

    [Fact]
    public void OnFcSlotData_RaisesUpdated()
    {
        var ln = new FakeLocoNet();
        using var fc = new LocoNetFastClock(ln, false, false);

        byte rate = 0, days = 0, hours = 0, mins = 0;
        bool sync = false;
        fc.Updated += (r, d, h, m, s) => { rate = r; days = d; hours = h; mins = m; sync = s; };

        // First trigger a Poll() so state == ReqTime.
        fc.Process66msActions(); // Idle → ReqTime + sends poll
        ln.Sent.Clear();

        // Build a 14-byte WrSlData fast-clock frame.
        // Layout: [0]opc [1]size [2]slot=0x7B [3]rate=4 [4]fracL [5]fracH [6]mins60 [7]track [8]hours24 [9]days
        //         [10]clk_cntrl (0x40 = valid) [11]id1 [12]id2 [13]checksum
        Span<byte> payload = stackalloc byte[13];
        payload[0] = (byte)OpCode.WrSlData;
        payload[1] = 0x0E;
        payload[2] = LnConstants.FastClockSlot;
        payload[3] = 4;        // rate
        payload[4] = 0;        // fracL
        payload[5] = 0;        // fracH
        payload[6] = (byte)(127 - 60 + 23); // mins → 23 mins past
        payload[7] = 0;
        payload[8] = (byte)(128 - 24 + 5);  // hours → 5
        payload[9] = 7;        // days
        payload[10] = 0x40;    // clk_cntrl: VALID
        payload[11] = 0;
        payload[12] = 0;
        var msg = LnMsg.FromPayload(payload);

        ln.Receive(msg);

        Assert.Equal(4, rate);
        Assert.Equal(7, days);
        Assert.Equal(5, hours);
        Assert.Equal(23, mins);
        Assert.True(sync);
    }

    [Fact]
    public void RolloverWithCorrectDcs100_SendsWrSlData()
    {
        var ln = new FakeLocoNet();
        using var fc = new LocoNetFastClock(ln, dcs100CompatibleSpeed: false, correctDcs100Clock: true);

        // Get into Ready state by feeding a valid FC packet, with frac counters near rollover.
        fc.Process66msActions(); // Idle → ReqTime
        ln.Sent.Clear();

        // Frac about to roll: fracL=0x7F, fracH=0x7F, rate=1 → first tick increments fracL to 0x80,
        // wrap → fracH increment to 0x80, wrap → minute rollover.
        Span<byte> payload = stackalloc byte[13];
        payload[0] = (byte)OpCode.SlRdData;
        payload[1] = 0x0E;
        payload[2] = LnConstants.FastClockSlot;
        payload[3] = 1;
        payload[4] = 0x7F;
        payload[5] = 0x7F;
        payload[6] = (byte)(127 - 60);
        payload[7] = 0;
        payload[8] = (byte)(128 - 24);
        payload[9] = 0;
        payload[10] = 0x40;
        payload[11] = 0;
        payload[12] = 0;
        ln.Receive(LnMsg.FromPayload(payload));

        ln.Sent.Clear();
        fc.Process66msActions(); // should roll a minute and emit WrSlData

        var wr = ln.Sent.Find(m => m.OpCode == OpCode.WrSlData);
        Assert.NotEqual(default, wr);
        Assert.Equal(14, wr.Length);
        Assert.Equal(LnConstants.FastClockSlot, wr[2]);
    }

    [Fact]
    public void DisabledClockCntrl_TransitionsToDisabled_NoUpdate()
    {
        var ln = new FakeLocoNet();
        using var fc = new LocoNetFastClock(ln, false, false);
        int updates = 0;
        fc.Updated += (_, _, _, _, _) => updates++;
        fc.Process66msActions(); // ReqTime
        ln.Sent.Clear();

        Span<byte> payload = stackalloc byte[13];
        payload[0] = (byte)OpCode.SlRdData;
        payload[1] = 0x0E;
        payload[2] = LnConstants.FastClockSlot;
        payload[10] = 0x00; // clk_cntrl clear → disabled
        ln.Receive(LnMsg.FromPayload(payload));

        Assert.Equal(0, updates);
    }
}
