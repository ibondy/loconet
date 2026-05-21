using LocoNet.Net;
using Xunit;

namespace LocoNet.Net.Tests;

public class LocoNetThrottleTests
{
    private static LnMsg BuildSlotData(byte slot, ushort address, byte speed = 0, byte dirf = 0, byte stat = 0, ushort throttleId = 0)
    {
        Span<byte> payload = stackalloc byte[13];
        payload[0] = (byte)OpCode.SlRdData;
        payload[1] = 0x0E;
        payload[2] = slot;
        payload[3] = stat;
        payload[4] = (byte)(address & 0x7F);
        payload[5] = speed;
        payload[6] = dirf;
        payload[7] = 0;
        payload[8] = 0;
        payload[9] = (byte)((address >> 7) & 0x7F);
        payload[10] = 0;
        payload[11] = (byte)(throttleId & 0x7F);
        payload[12] = (byte)(throttleId >> 7);
        return LnMsg.FromPayload(payload);
    }

    [Fact]
    public void SetAddress_FreeState_SendsLocoAdrAndTransitions()
    {
        var ln = new FakeLocoNet();
        var th = new LocoNetThrottle(ln);
        var res = th.SetAddress(1234);
        Assert.Equal(ThrottleError.Ok, res);
        Assert.Equal(ThrottleState.Select, th.State);

        var sent = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.LocoAdr, sent.OpCode);
        Assert.Equal(1234 >> 7, sent[1]);
        Assert.Equal(1234 & 0x7F, sent[2]);
    }

    [Fact]
    public void SetAddress_Busy_FiresError()
    {
        var ln = new FakeLocoNet();
        var th = new LocoNetThrottle(ln);
        th.SetAddress(1);
        ThrottleError? err = null;
        th.Error += (_, e) => err = e;
        var res = th.SetAddress(2);
        Assert.Equal(ThrottleError.Busy, res);
        Assert.Equal(ThrottleError.Busy, err);
    }

    [Fact]
    public void SlotResponse_AfterSelect_TriggersMoveSlots()
    {
        var ln = new FakeLocoNet();
        var th = new LocoNetThrottle(ln);
        th.SetAddress(100);
        ln.Sent.Clear();

        // Command station replies with slot data for slot 5, address 100, free slot (stat=0).
        ln.Receive(BuildSlotData(slot: 5, address: 100, stat: 0));

        Assert.Equal(ThrottleState.SlotMove, th.State);
        Assert.Equal(5, th.Slot);
        var sent = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.MoveSlots, sent.OpCode);
        Assert.Equal(5, sent[1]);
        Assert.Equal(5, sent[2]); // null move src==dst
    }

    [Fact]
    public void SetSpeed_NotSelected_FailsNotSelected()
    {
        var ln = new FakeLocoNet();
        var th = new LocoNetThrottle(ln);
        Assert.Equal(ThrottleError.NotSelected, th.SetSpeed(50));
    }

    [Fact]
    public void GetFunction_F0_UsesBit4()
    {
        var ln = new FakeLocoNet();
        var th = new LocoNetThrottle(ln);
        th.SetAddress(1);
        // First slot-data response: free slot → drives Select → SlotMove (sends null-move).
        ln.Receive(BuildSlotData(3, 1, stat: 0));
        Assert.Equal(ThrottleState.SlotMove, th.State);
        ln.Sent.Clear();
        // Second slot-data response on our owned slot with InUse + functions set.
        ln.Receive(BuildSlotData(3, 1, stat: LnConstants.LocoInUse, dirf: LnConstants.DirfF0 | 0x05));

        Assert.Equal(ThrottleState.InUse, th.State);
        Assert.NotEqual(0, th.GetFunction(0));
        Assert.NotEqual(0, th.GetFunction(1));
        Assert.Equal(0, th.GetFunction(2));
        Assert.NotEqual(0, th.GetFunction(3));
    }

    [Fact]
    public void AcquireAddress_FreeState_SendsNullMove()
    {
        var ln = new FakeLocoNet();
        var th = new LocoNetThrottle(ln);
        Assert.Equal(ThrottleError.Ok, th.AcquireAddress());
        var sent = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.MoveSlots, sent.OpCode);
        Assert.Equal(0, sent[1]);
        Assert.Equal(0, sent[2]);
        Assert.Equal(ThrottleState.Acquire, th.State);
    }

    [Fact]
    public void DispatchAddress_NoSlot_FailsNotSelected()
    {
        var ln = new FakeLocoNet();
        var th = new LocoNetThrottle(ln);
        Assert.Equal(ThrottleError.NotSelected, th.DispatchAddress());
    }

    [Fact]
    public void ReleaseAddress_Free_NoSend()
    {
        var ln = new FakeLocoNet();
        var th = new LocoNetThrottle(ln);
        th.ReleaseAddress();
        Assert.Empty(ln.Sent);
        Assert.Equal(ThrottleState.Free, th.State);
    }

    [Fact]
    public void LongAck_OnLocoAdr_FiresNoSlotsError()
    {
        var ln = new FakeLocoNet();
        var th = new LocoNetThrottle(ln);
        th.SetAddress(99); // → Select state
        ln.Sent.Clear();
        ThrottleError? err = null;
        th.Error += (_, e) => err = e;

        ln.Receive(LnMsg.MakeLongAck((byte)OpCode.LocoAdr, 0));

        Assert.Equal(ThrottleError.NoSlots, err);
        Assert.Equal(ThrottleState.Free, th.State);
    }

    [Fact]
    public void LongAck_OnMoveSlots_FiresNoLocoError()
    {
        var ln = new FakeLocoNet();
        var th = new LocoNetThrottle(ln);
        th.AcquireAddress(); // → Acquire state
        ln.Sent.Clear();
        ThrottleError? err = null;
        th.Error += (_, e) => err = e;

        ln.Receive(LnMsg.MakeLongAck((byte)OpCode.MoveSlots, 0));

        Assert.Equal(ThrottleError.NoLoco, err);
    }

    [Theory]
    [InlineData((byte)0, (byte)1)] // API "stop" → wire 0x01 (EMERG STOP per spec swap)
    [InlineData((byte)1, (byte)0)] // API "emerg stop" → wire 0x00
    [InlineData((byte)50, (byte)50)]
    [InlineData((byte)127, (byte)127)]
    public void SetSpeed_AppliesEStopSwapOnTheWire(byte apiSpeed, byte expectedWire)
    {
        var ln = new FakeLocoNet();
        var th = new LocoNetThrottle(ln);
        // Drive throttle into InUse for slot 3, loco 1. Seed the wire-speed to a value that
        // differs from every test row so SetSpeed always emits a packet.
        const byte seedWireSpeed = 99;
        th.SetAddress(1);
        ln.Receive(BuildSlotData(3, 1, stat: 0));            // null-move sent
        ln.Receive(BuildSlotData(3, 1, stat: LnConstants.LocoInUse, speed: seedWireSpeed));
        Assert.Equal(ThrottleState.InUse, th.State);
        ln.Sent.Clear();

        Assert.Equal(ThrottleError.Ok, th.SetSpeed(apiSpeed));
        var sent = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.LocoSpd, sent.OpCode);
        Assert.Equal(3, sent[1]);
        Assert.Equal(expectedWire, sent[2]);
    }

    [Fact]
    public void SetDirection_PreservesFunctionBits()
    {
        var ln = new FakeLocoNet();
        var th = new LocoNetThrottle(ln);
        th.SetAddress(1);
        ln.Receive(BuildSlotData(7, 1, stat: 0));
        // Slot data with F0+F2+F4 lit before InUse.
        byte initialDirf = (byte)(LnConstants.DirfF0 | LnConstants.DirfF2 | LnConstants.DirfF4);
        ln.Receive(BuildSlotData(7, 1, stat: LnConstants.LocoInUse, dirf: initialDirf));
        Assert.Equal(ThrottleState.InUse, th.State);
        ln.Sent.Clear();

        Assert.Equal(ThrottleError.Ok, th.SetDirection(1)); // reverse → set DIR bit
        var sent = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.LocoDirf, sent.OpCode);
        Assert.Equal(7, sent[1]);
        // Function bits unchanged, DIR bit now set.
        Assert.Equal((byte)(initialDirf | LnConstants.DirfDir), sent[2]);
    }

    [Fact]
    public void DispatchAddress_FromInUse_SendsMoveToSlotZero()
    {
        var ln = new FakeLocoNet();
        var th = new LocoNetThrottle(ln);
        th.SetAddress(1);
        ln.Receive(BuildSlotData(11, 1, stat: 0));
        ln.Receive(BuildSlotData(11, 1, stat: LnConstants.LocoInUse));
        Assert.Equal(ThrottleState.InUse, th.State);
        ln.Sent.Clear();

        Assert.Equal(ThrottleError.Ok, th.DispatchAddress());
        var sent = Assert.Single(ln.Sent);
        Assert.Equal(OpCode.MoveSlots, sent.OpCode);
        Assert.Equal(11, sent[1]);
        Assert.Equal(0, sent[2]); // dispatch PUT → dest slot 0
        Assert.Equal(ThrottleState.Free, th.State);
    }
}
