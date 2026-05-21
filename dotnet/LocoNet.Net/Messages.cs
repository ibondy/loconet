using System;

namespace LocoNet.Net;

/// <summary>
/// Typed accessors for the 14-byte slot-data frame (<c>OPC_SL_RD_DATA</c> / <c>OPC_WR_SL_DATA</c>).
/// </summary>
public readonly ref struct SlotDataMsg
{
    private readonly ReadOnlySpan<byte> _data;

    public SlotDataMsg(LnMsg message)
    {
        if (message.OpCode != OpCode.SlRdData && message.OpCode != OpCode.WrSlData)
        {
            throw new ArgumentException($"Not a slot-data message: {message.OpCode}", nameof(message));
        }
        if (message.Length != 14)
        {
            throw new ArgumentException($"Slot-data frame must be 14 bytes (was {message.Length}).", nameof(message));
        }
        _data = message.Bytes;
    }

    public byte Slot      => _data[2];
    public byte Stat      => _data[3];
    public byte AddrLow   => _data[4];
    public byte Speed     => _data[5];
    public byte DirFunc   => _data[6];
    public byte Track     => _data[7];
    public byte Stat2     => _data[8];
    public byte AddrHigh  => _data[9];
    public byte Sound     => _data[10];
    public byte Id1       => _data[11];
    public byte Id2       => _data[12];

    /// <summary>Combined 14-bit loco address (<see cref="AddrHigh"/> &lt;&lt; 7 | <see cref="AddrLow"/>).</summary>
    public ushort Address => (ushort)((AddrHigh << 7) | AddrLow);

    /// <summary>Combined 14-bit throttle id (<see cref="Id2"/> &lt;&lt; 7 | <see cref="Id1"/>).</summary>
    public ushort ThrottleId => (ushort)((Id2 << 7) | Id1);

    /// <summary>
    /// Build a new slot-data frame from this one, optionally overriding selected fields.
    /// The checksum is recomputed and the opcode defaults to <c>OPC_WR_SL_DATA</c>.
    /// </summary>
    public LnMsg ToWrSlData(byte? stat = null, byte? id1 = null, byte? id2 = null)
    {
        Span<byte> buf = stackalloc byte[13];
        _data[..13].CopyTo(buf);
        buf[0] = (byte)OpCode.WrSlData;
        if (stat is not null) buf[3] = stat.Value;
        if (id1 is not null) buf[11] = id1.Value;
        if (id2 is not null) buf[12] = id2.Value;
        return LnMsg.FromPayload(buf);
    }
}

/// <summary>
/// Typed accessor for 4-byte loco-data frames (<c>OPC_LOCO_SPD</c>, <c>OPC_LOCO_DIRF</c>,
/// <c>OPC_LOCO_SND</c>, <c>OPC_SLOT_STAT1</c>).
/// </summary>
public readonly ref struct LocoDataMsg
{
    private readonly ReadOnlySpan<byte> _data;

    public LocoDataMsg(LnMsg message)
    {
        if (message.Length != 4)
        {
            throw new ArgumentException($"Loco-data frame must be 4 bytes (was {message.Length}).", nameof(message));
        }
        _data = message.Bytes;
    }

    public byte Slot => _data[1];
    public byte Data => _data[2];
}

/// <summary>
/// Typed accessor for the 4-byte long-ACK frame (<c>OPC_LONG_ACK</c>).
/// </summary>
public readonly ref struct LongAckMsg
{
    private readonly ReadOnlySpan<byte> _data;

    public LongAckMsg(LnMsg message)
    {
        if (message.OpCode != OpCode.LongAck || message.Length != 4)
        {
            throw new ArgumentException("Not a long-ACK frame.", nameof(message));
        }
        _data = message.Bytes;
    }

    /// <summary>Opcode of the message being acknowledged, with the high bit cleared.</summary>
    public byte Opcode => _data[1];

    public byte Ack => _data[2];
}
