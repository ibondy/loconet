using System;

namespace LocoNet.Net;

/// <summary>
/// LNSV (LocoNet System Variable) message helper. The on-the-wire layout is a 16-byte
/// peer-transfer frame (<c>OPC_PEER_XFER</c>, <c>mesg_size = 0x10</c>, <c>sv_type = 0x02</c>).
/// 8 payload bytes are packed into two 4-byte groups, each preceded by a PXCT byte
/// holding the high bit of each of the next 4 payload bytes.
/// </summary>
/// <remarks>
/// Frame layout:
/// <code>
/// [0]  command       OPC_PEER_XFER (0xE5)
/// [1]  mesg_size     0x10 (16)
/// [2]  src           8-bit source
/// [3]  sv_cmd        SV command (bit 6 set in replies)
/// [4]  sv_type       0x02
/// [5]  svx1          high bit of [6..9]
/// [6]  d1
/// [7]  d2
/// [8]  d3
/// [9]  d4
/// [10] svx2          high bit of [11..14]
/// [11] d5
/// [12] d6
/// [13] d7
/// [14] d8
/// [15] checksum
/// </code>
/// </remarks>
internal static class SvPeerData
{
    /// <summary>Decode the 8 plain data bytes from an LNSV peer-transfer frame.</summary>
    public static void Decode(ReadOnlySpan<byte> frame, Span<byte> outData)
    {
        if (outData.Length < 8) throw new ArgumentException("outData must be 8 bytes.", nameof(outData));

        byte bitMask = 0x01;
        int inIdx = 6;        // d1
        int bitsIdx = 5;      // svx1
        for (int i = 0; i < 8; i++)
        {
            byte b = frame[inIdx];
            if ((frame[bitsIdx] & bitMask) != 0)
            {
                b |= 0x80;
            }
            outData[i] = b;

            if (i == 3)
            {
                bitMask = 0x01;
                inIdx = 11;       // d5
                bitsIdx = 10;     // svx2
            }
            else
            {
                bitMask <<= 1;
                inIdx++;
            }
        }
    }

    /// <summary>Encode 8 plain data bytes into an LNSV peer-transfer frame (in place).</summary>
    /// <remarks>
    /// Preserves the high nibble of <c>svx1</c> (frame[5]) and <c>svx2</c> (frame[10]) — only
    /// the low 4 bits (the PXCT MSB bits) are cleared and re-populated. The wire-protocol
    /// requires the high nibble to remain <c>0x10</c>; callers should ensure those nibbles
    /// are set before calling Encode.
    /// </remarks>
    public static void Encode(Span<byte> frame, ReadOnlySpan<byte> inData)
    {
        if (inData.Length < 8) throw new ArgumentException("inData must be 8 bytes.", nameof(inData));

        // Clear only the low nibble (PXCT bits); preserve the high nibble (protocol prefix 0x10).
        frame[5]  = (byte)(frame[5]  & 0xF0);
        frame[10] = (byte)(frame[10] & 0xF0);
        byte bitMask = 0x01;
        int outIdx = 6;
        int bitsIdx = 5;
        for (int i = 0; i < 8; i++)
        {
            byte b = inData[i];
            frame[outIdx] = (byte)(b & 0x7F);
            if ((b & 0x80) != 0)
            {
                frame[bitsIdx] |= bitMask;
            }

            if (i == 3)
            {
                bitMask = 0x01;
                outIdx = 11;
                bitsIdx = 10;
            }
            else
            {
                bitMask <<= 1;
                outIdx++;
            }
        }
    }
}
