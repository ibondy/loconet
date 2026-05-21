using System;

namespace LocoNet.Net;

/// <summary>
/// Stream-to-frame reassembler for LocoNet bytes. Feed received bytes via
/// <see cref="AddByte(byte)"/>; when a complete, checksum-valid frame is accumulated it is
/// returned. Invalid frames are discarded and counted in <see cref="Stats"/>.
/// </summary>
/// <remarks>
/// Ported from <c>src/LocoNetMessageBuffer.cpp</c>. Not thread-safe.
/// </remarks>
public sealed class LocoNetMessageBuffer
{
    // Largest LocoNet frame in the union is 16 bytes (peerXferMsg / progTaskMsg).
    private const int BufferSize = 16;

    private readonly byte[] _buffer = new byte[BufferSize];
    private int _index;
    private int _expectedLength;
    private byte _checksum = LnConstants.ChecksumSeed;

    /// <summary>Receive statistics.</summary>
    public LnRxStats Stats { get; } = new();

    /// <summary>Reset the framer to its initial empty state.</summary>
    public void Reset()
    {
        _index = 0;
        _expectedLength = 0;
        _checksum = LnConstants.ChecksumSeed;
    }

    /// <summary>
    /// Feed one byte. Returns a complete <see cref="LnMsg"/> when one has been assembled,
    /// otherwise <see langword="null"/>.
    /// </summary>
    public LnMsg? AddByte(byte newByte)
    {
        if (_index >= BufferSize)
        {
            // Overflow — discard and resync on next opcode byte.
            Reset();
        }

        // High bit set means start of a new LocoNet packet — resync.
        if ((newByte & 0x80) != 0)
        {
            _index = 0;
            _expectedLength = 0;
            _checksum = LnConstants.ChecksumSeed;
        }

        _buffer[_index++] = newByte;

        // The checksum covers every byte up to (but not including) the trailing checksum byte.
        if (_index <= 2 || _index <= _expectedLength)
        {
            _checksum ^= newByte;
        }

        return TryComplete();
    }

    /// <summary>Feed a span of bytes and yield each completed message in order.</summary>
    public void AddBytes(ReadOnlySpan<byte> bytes, Action<LnMsg> onMessage)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        foreach (byte b in bytes)
        {
            var msg = AddByte(b);
            if (msg.HasValue)
            {
                onMessage(msg.Value);
            }
        }
    }

    private LnMsg? TryComplete()
    {
        if (_index < 2)
        {
            return null;
        }

        if (_expectedLength == 0)
        {
            _expectedLength = OpCodeExtensions.PacketSize(_buffer[0], _buffer[1]);
        }

        if (_expectedLength != _index)
        {
            return null;
        }

        if (_checksum == 0)
        {
            Stats.RxPackets++;
            var frame = _buffer.AsSpan(0, _expectedLength).ToArray();
            Reset();
            // Use FromBytes for a final validation pass; failure here would be a bug above.
            return LnMsg.FromBytes(frame);
        }

        Stats.RxErrors++;
        Reset();
        return null;
    }
}
