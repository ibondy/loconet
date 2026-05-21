using System;

namespace LocoNet.Net;

/// <summary>
/// Helpers for building and parsing the switch / sensor / power LocoNet messages
/// (<c>OPC_SW_REQ</c>, <c>OPC_SW_REP</c>, <c>OPC_SW_STATE</c>, <c>OPC_INPUT_REP</c>,
/// <c>OPC_GPON</c>, <c>OPC_GPOFF</c>).
/// </summary>
/// <remarks>Ported from the standalone helpers in <c>src/LocoNet2.cpp</c>.</remarks>
public static class Switches
{
    /// <summary>
    /// Build an <c>OPC_SW_REQ</c> message.
    /// </summary>
    /// <param name="address">Turnout address (1-based, 1..2048).</param>
    /// <param name="output">True to energize the coil, false to release.</param>
    /// <param name="thrown">True for thrown, false for closed.</param>
    public static LnMsg MakeSwitchRequest(ushort address, bool output, bool thrown)
    {
        ushort addr = (ushort)(address - 1);
        byte addrH = (byte)((addr >> 7) & 0x0F);
        byte addrL = (byte)(addr & 0x7F);

        if (output)        addrH |= LnConstants.SwReqOut;
        if (!thrown)       addrH |= LnConstants.SwReqDir;

        return LnMsg.Make(OpCode.SwReq, addrL, addrH);
    }

    /// <summary>Send a switch-request (<c>OPC_SW_REQ</c>) message.</summary>
    public static void RequestSwitch(ILocoNet ln, ushort address, bool output, bool thrown)
        => ln.Send(MakeSwitchRequest(address, output, thrown));

    /// <summary>Send a switch-state query (<c>OPC_SW_STATE</c>) message.</summary>
    public static void ReportSwitch(ILocoNet ln, ushort address)
    {
        ushort addr = (ushort)(address - 1);
        ln.Send(OpCode.SwState, (byte)(addr & 0x7F), (byte)((addr >> 7) & 0x0F));
    }

    /// <summary>Send a sensor input report (<c>OPC_INPUT_REP</c>).</summary>
    /// <param name="ln">LocoNet transport.</param>
    /// <param name="address">Sensor address (1-based).</param>
    /// <param name="state">True for HI, false for LO.</param>
    public static void ReportSensor(ILocoNet ln, ushort address, bool state)
    {
        ushort addr = (ushort)(address - 1);
        byte addrH = (byte)(((addr >> 8) & 0x0F) | LnConstants.InputRepCb);
        byte addrL = (byte)((addr >> 1) & 0x7F);
        if ((addr & 1) != 0)  addrH |= LnConstants.InputRepSw;
        if (state)            addrH |= LnConstants.InputRepHi;
        ln.Send(OpCode.InputRep, addrL, addrH);
    }

    /// <summary>Send a global power on/off message.</summary>
    public static void ReportPower(ILocoNet ln, bool on)
        => ln.Send(LnMsg.FromPayload(stackalloc byte[] { on ? (byte)OpCode.GpOn : (byte)OpCode.GpOff }));

    /// <summary>
    /// Decode a switch <c>OPC_SW_REQ</c>, <c>OPC_SW_REP</c> (when reporting outputs),
    /// or <c>OPC_SW_STATE</c> message. Returns the 1-based switch address and the two
    /// control bits.
    /// </summary>
    public static (ushort Address, bool Output, bool Direction) DecodeSwitchRequest(LnMsg msg)
    {
        if (msg.Length != 4)
        {
            throw new ArgumentException("Expected a 4-byte switch message.", nameof(msg));
        }
        byte sw1 = msg[1];
        byte sw2 = msg[2];
        ushort address = (ushort)((sw1 | ((sw2 & 0x0F) << 7)) + 1);
        return (address, (sw2 & LnConstants.SwReqOut) != 0, (sw2 & LnConstants.SwReqDir) != 0);
    }

    /// <summary>
    /// Decode an <c>OPC_SW_REP</c> (sensor input form) message into a sensor address +
    /// state pair. Uses the <c>SW</c> bit to disambiguate switch input vs. aux input.
    /// </summary>
    public static (ushort Address, bool High, bool SwitchInput) DecodeSwitchReport(LnMsg msg)
    {
        if (msg.Length != 4)
        {
            throw new ArgumentException("Expected a 4-byte switch-report message.", nameof(msg));
        }
        byte sw1 = msg[1];
        byte sw2 = msg[2];
        ushort address = (ushort)((sw1 | ((sw2 & 0x0F) << 7)) + 1);
        return (address, (sw2 & LnConstants.SwRepHi) != 0, (sw2 & LnConstants.SwRepSw) != 0);
    }

    /// <summary>Decode an <c>OPC_INPUT_REP</c> sensor report. Returns 1-based address.</summary>
    public static (ushort Address, bool High) DecodeInputReport(LnMsg msg)
    {
        if (msg.OpCode != OpCode.InputRep || msg.Length != 4)
        {
            throw new ArgumentException("Expected an OPC_INPUT_REP message.", nameof(msg));
        }
        byte in1 = msg[1];
        byte in2 = msg[2];
        ushort address = (ushort)(in1 | ((in2 & 0x0F) << 7));
        address <<= 1;
        address += (in2 & LnConstants.InputRepSw) != 0 ? (ushort)2 : (ushort)1;
        return (address, (in2 & LnConstants.InputRepHi) != 0);
    }
}
