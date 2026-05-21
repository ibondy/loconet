using System;

namespace LocoNet.Net;

/// <summary>
/// Higher-level event dispatcher: subscribes to an <see cref="ILocoNet"/> and re-emits
/// strongly-typed events for the most common message kinds (switch / sensor / power).
/// </summary>
/// <remarks>Managed port of the original LocoNet2 dispatcher event surface.</remarks>
public sealed class LocoNetDispatcher : IDisposable
{
    private readonly ILocoNet _locoNet;

    public LocoNetDispatcher(ILocoNet locoNet)
    {
        _locoNet = locoNet ?? throw new ArgumentNullException(nameof(locoNet));
        _locoNet.MessageReceived += OnMessageReceived;
    }

    /// <summary>Raised for an <c>OPC_INPUT_REP</c> sensor change. (address, state).</summary>
    public event Action<ushort, bool>? SensorChanged;

    /// <summary>Raised for an <c>OPC_SW_REQ</c> switch command. (address, output, direction).</summary>
    public event Action<ushort, bool, bool>? SwitchRequested;

    /// <summary>Raised for an <c>OPC_SW_REP</c> switch/sensor report. (address, hi, sw-input).</summary>
    public event Action<ushort, bool, bool>? SwitchReported;

    /// <summary>Raised for an <c>OPC_SW_STATE</c> message. (address, output, direction).</summary>
    public event Action<ushort, bool, bool>? SwitchStateReported;

    /// <summary>Raised when global power changes (<c>OPC_GPON</c>/<c>OPC_GPOFF</c>).</summary>
    public event Action<bool>? PowerChanged;

    /// <summary>
    /// Raised for an <c>OPC_MULTI_SENSE</c> "device info" message. Args:
    /// <c>(boardId, sectionIndex 1..4, dcs1State, dcs2State)</c>. Fires four times per packet
    /// (one for each section).
    /// </summary>
    public event Action<byte, byte, bool, bool>? MultiSenseDeviceInfo;

    /// <summary>
    /// Raised for an <c>OPC_MULTI_SENSE</c> transponder absent/present message. Args:
    /// <c>(boardAddress, zoneId 'A'..'H', locoAddress, present)</c>.
    /// </summary>
    public event Action<ushort, char, ushort, bool>? MultiSenseTransponder;

    private void OnMessageReceived(object? sender, LnMessageEventArgs e)
    {
        var msg = e.Message;
        switch (msg.OpCode)
        {
            case OpCode.InputRep:
                if (msg.Length == 4)
                {
                    var (addr, hi) = Switches.DecodeInputReport(msg);
                    SensorChanged?.Invoke(addr, hi);
                }
                break;

            case OpCode.SwReq:
                if (msg.Length == 4)
                {
                    var (addr, output, dir) = Switches.DecodeSwitchRequest(msg);
                    SwitchRequested?.Invoke(addr, output, dir);
                }
                break;

            case OpCode.SwRep:
                if (msg.Length == 4)
                {
                    var (addr, hi, sw) = Switches.DecodeSwitchReport(msg);
                    SwitchReported?.Invoke(addr, hi, sw);
                }
                break;

            case OpCode.SwState:
                if (msg.Length == 4)
                {
                    var (addr, output, dir) = Switches.DecodeSwitchRequest(msg);
                    SwitchStateReported?.Invoke(addr, output, dir);
                }
                break;

            case OpCode.GpOn:
                PowerChanged?.Invoke(true);
                break;

            case OpCode.GpOff:
                PowerChanged?.Invoke(false);
                break;

            case OpCode.MultiSense:
                if (msg.Length == 6)
                {
                    DispatchMultiSense(msg);
                }
                break;
        }
    }

    private void DispatchMultiSense(LnMsg msg)
    {
        const byte MsgMask    = 0x60;
        const byte MsgAbsent  = 0x00;
        const byte MsgPresent = 0x20;
        const byte MsgDevInfo = 0x60;

        byte type = msg[1];
        byte b2   = msg[2];
        byte b3   = msg[3];
        byte b4   = msg[4];

        switch (type & MsgMask)
        {
            case MsgDevInfo:
                if (MultiSenseDeviceInfo is null) return;
                if ((b3 & 0xF0) != 0x30 && (b3 & 0xF0) != 0x10) return;
                {
                    byte boardId = (byte)(b2 + 1 + ((type & 0x01) != 0 ? 128 : 0));
                    byte mask = 1;
                    for (byte i = 0; i < 4; i++, mask <<= 1)
                    {
                        MultiSenseDeviceInfo.Invoke(boardId, (byte)(i + 1),
                            (b3 & mask) != 0, (b4 & mask) != 0);
                    }
                }
                break;

            case MsgAbsent:
            case MsgPresent:
                if (MultiSenseTransponder is null) return;
                {
                    // type byte = zone+type; b2 = zone+presence
                    byte zone = b2;
                    ushort boardAddress = (ushort)(zone + ((type & 0x1F) << 7) + 1);
                    char zoneId = ZoneIdFor((byte)(zone & 0x0F));
                    ushort locoAddress = (b4 + b3) != 0x7D ? (ushort)(b3 << 7) : (ushort)0;
                    bool present = (zone & MsgPresent) != 0;
                    MultiSenseTransponder.Invoke(boardAddress, zoneId, locoAddress, present);
                }
                break;
        }
    }

    private static char ZoneIdFor(byte zone) => zone switch
    {
        0x00 => 'A',
        0x02 => 'B',
        0x04 => 'C',
        0x06 => 'D',
        0x08 => 'E',
        0x0A => 'F',
        0x0C => 'G',
        0x0E => 'H',
        _    => (char)zone,
    };

    public void Dispose()
    {
        _locoNet.MessageReceived -= OnMessageReceived;
    }
}
