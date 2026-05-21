using LocoNet.Net;
using Xunit;

namespace LocoNet.Net.Tests;

public class LocoNetDispatcherTests
{
    [Fact]
    public void SensorChanged_FiresOnInputRep()
    {
        var ln = new FakeLocoNet();
        using var d = new LocoNetDispatcher(ln);
        ushort? gotAddr = null;
        bool? gotHi = null;
        d.SensorChanged += (a, h) => { gotAddr = a; gotHi = h; };

        // Build an OPC_INPUT_REP for sensor 17, HIGH.
        var ln2 = new FakeLocoNet();
        Switches.ReportSensor(ln2, 17, true);
        ln.Receive(ln2.Sent[0]);

        Assert.Equal((ushort)17, gotAddr);
        Assert.True(gotHi);
    }

    [Fact]
    public void SwitchRequested_FiresOnSwReq()
    {
        var ln = new FakeLocoNet();
        using var d = new LocoNetDispatcher(ln);
        ushort? gotAddr = null;
        bool? gotOut = null, gotDir = null;
        d.SwitchRequested += (a, o, dr) => { gotAddr = a; gotOut = o; gotDir = dr; };

        ln.Receive(Switches.MakeSwitchRequest(99, output: true, thrown: true));
        Assert.Equal((ushort)99, gotAddr);
        Assert.True(gotOut);
        Assert.False(gotDir); // thrown=true → DIR bit clear
    }

    [Fact]
    public void SwitchStateReported_FiresOnSwState()
    {
        var ln = new FakeLocoNet();
        using var d = new LocoNetDispatcher(ln);
        ushort? gotAddr = null;
        d.SwitchStateReported += (a, _, _) => gotAddr = a;

        var ln2 = new FakeLocoNet();
        Switches.ReportSwitch(ln2, 7);
        ln.Receive(ln2.Sent[0]);
        Assert.Equal((ushort)7, gotAddr);
    }

    [Fact]
    public void PowerChanged_FiresOnGpOnAndGpOff()
    {
        var ln = new FakeLocoNet();
        using var d = new LocoNetDispatcher(ln);
        var values = new System.Collections.Generic.List<bool>();
        d.PowerChanged += b => values.Add(b);

        var ln2 = new FakeLocoNet();
        Switches.ReportPower(ln2, true);
        Switches.ReportPower(ln2, false);
        foreach (var m in ln2.Sent) ln.Receive(m);

        Assert.Equal(new[] { true, false }, values);
    }

    [Fact]
    public void MultiSenseTransponder_FiresOnPresent()
    {
        var ln = new FakeLocoNet();
        using var d = new LocoNetDispatcher(ln);
        ushort gotBoard = 0;
        char gotZone = '?';
        ushort gotLoco = 0;
        bool gotPresent = false;
        int calls = 0;
        d.MultiSenseTransponder += (b, z, l, p) =>
        {
            calls++;
            gotBoard = b; gotZone = z; gotLoco = l; gotPresent = p;
        };

        // type byte: PRESENT (0x20) | board-type=0x01 → 0x21
        // zone byte: zone=0x00 (zoneA) | PRESENT mask=0x20 → 0x20
        // adr1=0x40 (loco 0x40<<7 = 8192), adr2=0x00 (sum != 0x7D)
        byte cmd = (byte)OpCode.MultiSense;
        byte type = 0x21;
        byte zone = 0x20; // zoneA + PRESENT bit
        byte adr1 = 0x40;
        byte adr2 = 0x00;
        var msg = LnMsg.FromPayload(new byte[] { cmd, type, zone, adr1, adr2 });
        ln.Receive(msg);

        Assert.Equal(1, calls);
        // boardAddress = zone (0x20) + ((type 0x21 & 0x1F) << 7) + 1 = 32 + (1<<7) + 1 = 161
        Assert.Equal(161, gotBoard);
        Assert.Equal('A', gotZone);
        Assert.Equal(0x40 << 7, gotLoco);
        Assert.True(gotPresent);
    }

    [Fact]
    public void MultiSenseDeviceInfo_FiresFourTimes()
    {
        var ln = new FakeLocoNet();
        using var d = new LocoNetDispatcher(ln);
        int calls = 0;
        d.MultiSenseDeviceInfo += (_, _, _, _) => calls++;

        // type byte 0x60 = DEVICE_INFO
        // arg3 must have top nibble 0x30 or 0x10
        var msg = LnMsg.FromPayload(new byte[] {
            (byte)OpCode.MultiSense, 0x60, 0x05, 0x35, 0x0A
        });
        ln.Receive(msg);
        Assert.Equal(4, calls);
    }

    [Fact]
    public void Busy81_IsSilentlyIgnored()
    {
        // Spec: <81><7E> is a NOP "time burner" emitted by the master. Dispatcher must not
        // fire any event for it.
        var ln = new FakeLocoNet();
        using var d = new LocoNetDispatcher(ln);
        int sensor = 0, sw = 0, power = 0, ms = 0;
        d.SensorChanged += (_, _) => sensor++;
        d.SwitchRequested += (_, _, _) => sw++;
        d.PowerChanged += _ => power++;
        d.MultiSenseTransponder += (_, _, _, _) => ms++;

        var nop = LnMsg.FromPayload(stackalloc byte[] { (byte)OpCode.Busy });
        Assert.Equal(2, nop.Length);
        ln.Receive(nop);

        Assert.Equal(0, sensor + sw + power + ms);
    }

    [Fact]
    public void MultiSenseTransponder_AbsentFiresWithPresentFalse()
    {
        var ln = new FakeLocoNet();
        using var d = new LocoNetDispatcher(ln);
        bool? present = null;
        int calls = 0;
        d.MultiSenseTransponder += (_, _, _, p) => { present = p; calls++; };

        // type=0x01 (ABSENT — PRESENT bit 0x20 clear), zone=0x00 (PRESENT mask clear),
        // adr1=0x40, adr2=0x00.
        var msg = LnMsg.FromPayload(new byte[] {
            (byte)OpCode.MultiSense, 0x01, 0x00, 0x40, 0x00
        });
        ln.Receive(msg);

        Assert.Equal(1, calls);
        Assert.False(present);
    }
}
