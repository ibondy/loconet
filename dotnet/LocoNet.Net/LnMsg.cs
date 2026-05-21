using System;

namespace LocoNet.Net;

/// <summary>
/// An immutable LocoNet message: opcode + payload + checksum.
/// </summary>
/// <remarks>
/// The underlying byte array always contains a complete, length-correct frame whose final
/// byte is the XOR checksum. Construct via <see cref="FromBytes(ReadOnlySpan{byte})"/> for
/// validated parsing, or via the <c>Make*</c> factories for outbound messages.
/// </remarks>
public readonly struct LnMsg : IEquatable<LnMsg>
{
    private readonly byte[] _data;

    private LnMsg(byte[] data) => _data = data;

    /// <summary>Raw bytes of the message including opcode and checksum.</summary>
    public ReadOnlySpan<byte> Bytes => _data;

    /// <summary>Total length in bytes (opcode + payload + checksum).</summary>
    public int Length => _data.Length;

    /// <summary>The opcode (first byte).</summary>
    public OpCode OpCode => (OpCode)_data[0];

    /// <summary>Indexed access to message bytes.</summary>
    public byte this[int index] => _data[index];

    /// <summary>
    /// Build an <see cref="LnMsg"/> from a complete frame. The checksum is verified.
    /// </summary>
    /// <exception cref="ArgumentException">If the buffer is too short, has an inconsistent
    /// length, or has an invalid checksum.</exception>
    public static LnMsg FromBytes(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
        {
            throw new ArgumentException("LocoNet frame must be at least 2 bytes.", nameof(frame));
        }
        if ((frame[0] & 0x80) == 0)
        {
            throw new ArgumentException("First byte is not a valid opcode (high bit not set).", nameof(frame));
        }

        int expected = OpCodeExtensions.PacketSize(frame[0], frame.Length > 1 ? frame[1] : (byte)0);
        if (expected != frame.Length)
        {
            throw new ArgumentException(
                $"Frame length {frame.Length} does not match expected length {expected} for opcode 0x{frame[0]:X2}.",
                nameof(frame));
        }

        byte checksum = LnConstants.ChecksumSeed;
        for (int i = 0; i < frame.Length; i++)
        {
            checksum ^= frame[i];
        }
        if (checksum != 0)
        {
            throw new ArgumentException("LocoNet checksum mismatch.", nameof(frame));
        }

        return new LnMsg(frame.ToArray());
    }

    /// <summary>
    /// Build an <see cref="LnMsg"/> from a payload that does not yet include a checksum.
    /// The checksum byte is computed and appended.
    /// </summary>
    public static LnMsg FromPayload(ReadOnlySpan<byte> payloadWithoutChecksum)
    {
        if (payloadWithoutChecksum.Length < 1)
        {
            throw new ArgumentException("Payload must contain at least an opcode byte.", nameof(payloadWithoutChecksum));
        }

        var buffer = new byte[payloadWithoutChecksum.Length + 1];
        payloadWithoutChecksum.CopyTo(buffer);
        buffer[^1] = ComputeChecksum(buffer.AsSpan(0, payloadWithoutChecksum.Length));

        int expected = OpCodeExtensions.PacketSize(buffer[0], buffer.Length > 1 ? buffer[1] : (byte)0);
        if (expected != buffer.Length)
        {
            throw new ArgumentException(
                $"Payload length {buffer.Length} does not match expected length {expected} for opcode 0x{buffer[0]:X2}.",
                nameof(payloadWithoutChecksum));
        }

        return new LnMsg(buffer);
    }

    /// <summary>Convenience factory for the common 4-byte (opcode + d1 + d2 + checksum) form.</summary>
    public static LnMsg Make(OpCode opCode, byte d1, byte d2)
        => FromPayload(stackalloc byte[] { (byte)opCode, d1, d2 });

    /// <summary>Build a long-ACK reply for the given opcode and ack code.</summary>
    public static LnMsg MakeLongAck(byte replyToOpc, byte ack)
        => Make(OpCode.LongAck, (byte)(replyToOpc & OpCodeExtensions.OpCodeMask), ack);

    /// <summary>Compute the XOR checksum over an opcode + payload (without the trailing checksum byte).</summary>
    public static byte ComputeChecksum(ReadOnlySpan<byte> payloadWithoutChecksum)
    {
        byte cs = LnConstants.ChecksumSeed;
        foreach (byte b in payloadWithoutChecksum)
        {
            cs ^= b;
        }
        return cs;
    }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder(_data.Length * 3);
        for (int i = 0; i < _data.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            sb.Append(_data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    public bool Equals(LnMsg other) => _data.AsSpan().SequenceEqual(other._data);
    public override bool Equals(object? obj) => obj is LnMsg m && Equals(m);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_data);
        return hash.ToHashCode();
    }

    public static bool operator ==(LnMsg left, LnMsg right) => left.Equals(right);
    public static bool operator !=(LnMsg left, LnMsg right) => !left.Equals(right);
}
