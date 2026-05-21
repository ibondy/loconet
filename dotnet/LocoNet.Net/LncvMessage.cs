using System;

namespace LocoNet.Net;

/// <summary>
/// Typed accessor / builder for the Uhlenbrock 15-byte LNCV peer-transfer message used by
/// <see cref="LocoNetCVAccess"/> and <see cref="LocoNetCV"/>.
/// </summary>
/// <remarks>
/// Frame layout:
/// <code>
/// [0]  command       OPC_PEER_XFER (0xE5) or OPC_IMM_PACKET (0xED)
/// [1]  mesg_size     0x0F (15)
/// [2]  SRC
/// [3]  DSTL          destination low byte
/// [4]  DSTH          destination high byte
/// [5]  ReqId
/// [6]  PXCT1         MSBs of the following 7 data bytes
/// [7..13] D[0..6]    7 data bytes (top bit zero, stored separately in PXCT1)
/// [14] checksum
/// </code>
/// </remarks>
public static class LncvMessage
{
    public const byte SrcModule        = 0x05;
    public const byte ReqIdCfgRead     = 31;
    public const byte ReqIdCfgWrite    = 32;
    public const byte ReqIdCfgRequest  = 33;
    public const byte FlagProgOn       = 0x80;
    public const byte FlagProgOff      = 0x40;

    private const int IxCmd  = 0;
    private const int IxSize = 1;
    private const int IxSrc  = 2;
    private const int IxDstL = 3;
    private const int IxDstH = 4;
    private const int IxReqId = 5;
    private const int IxPxct = 6;
    private const int IxData = 7;

    public static bool IsLncv(LnMsg msg)
        => msg.Length == 15 && (msg.OpCode == OpCode.PeerXfer || msg.OpCode == OpCode.ImmPacket) && msg[IxSize] == 0x0F;

    public static byte Source(LnMsg msg) => msg[IxSrc];
    public static byte DestLow(LnMsg msg) => msg[IxDstL];
    public static byte DestHigh(LnMsg msg) => msg[IxDstH];
    public static byte ReqId(LnMsg msg) => msg[IxReqId];

    /// <summary>Reconstruct the 7 data bytes, restoring the MSB from PXCT1.</summary>
    public static void ExtractData(LnMsg msg, Span<byte> outData)
    {
        if (outData.Length < 7) throw new ArgumentException("outData must be at least 7 bytes.", nameof(outData));
        byte pxct = msg[IxPxct];
        byte mask = 0x01;
        for (int i = 0; i < 7; i++)
        {
            byte b = msg[IxData + i];
            if ((pxct & mask) != 0) b |= 0x80;
            outData[i] = b;
            mask <<= 1;
        }
    }

    public static ushort DeviceClass(LnMsg msg)
    {
        Span<byte> d = stackalloc byte[7];
        ExtractData(msg, d);
        return (ushort)(d[0] | (d[1] << 8));
    }

    public static ushort LncvNumber(LnMsg msg)
    {
        Span<byte> d = stackalloc byte[7];
        ExtractData(msg, d);
        return (ushort)(d[2] | (d[3] << 8));
    }

    public static ushort LncvValue(LnMsg msg)
    {
        Span<byte> d = stackalloc byte[7];
        ExtractData(msg, d);
        return (ushort)(d[4] | (d[5] << 8));
    }

    public static byte Flags(LnMsg msg)
    {
        Span<byte> d = stackalloc byte[7];
        ExtractData(msg, d);
        return d[6];
    }

    /// <summary>Build an LNCV response message (CFG read reply).</summary>
    public static LnMsg MakeResponse(byte src, ushort deviceClass, ushort cv, ushort value, byte flags)
    {
        Span<byte> data = stackalloc byte[7];
        data[0] = (byte)(deviceClass & 0xFF);
        data[1] = (byte)(deviceClass >> 8);
        data[2] = (byte)(cv & 0xFF);
        data[3] = (byte)(cv >> 8);
        data[4] = (byte)(value & 0xFF);
        data[5] = (byte)(value >> 8);
        data[6] = flags;

        Span<byte> frame = stackalloc byte[14];
        frame[IxCmd]   = (byte)OpCode.PeerXfer;
        frame[IxSize]  = 0x0F;
        frame[IxSrc]   = SrcModule;
        frame[IxDstL]  = src;
        frame[IxDstH]  = 0;
        frame[IxReqId] = ReqIdCfgRead;
        EncodeData(data, frame[IxPxct..]);
        return LnMsg.FromPayload(frame);
    }

    /// <summary>Pack 7 data bytes plus their MSBs into PXCT1+D[0..6] (8 bytes total).</summary>
    public static void EncodeData(ReadOnlySpan<byte> data, Span<byte> pxctAndPayload)
    {
        if (data.Length < 7) throw new ArgumentException("data must be 7 bytes.", nameof(data));
        if (pxctAndPayload.Length < 8) throw new ArgumentException("destination must be 8 bytes.", nameof(pxctAndPayload));

        byte pxct = 0;
        byte mask = 0x01;
        for (int i = 0; i < 7; i++)
        {
            byte b = data[i];
            if ((b & 0x80) != 0)
            {
                pxct |= mask;
            }
            pxctAndPayload[1 + i] = (byte)(b & 0x7F);
            mask <<= 1;
        }
        pxctAndPayload[0] = pxct;
    }
}
