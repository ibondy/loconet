namespace LocoNet.Net;

/// <summary>
/// LocoNet opcodes. The high bit of the opcode byte is always set; the next two bits
/// (mask <c>0x60</c>) encode the message length group.
/// </summary>
public enum OpCode : byte
{
    // 2-byte messages (opcode + checksum)
    Busy            = 0x81,
    GpOff           = 0x82,
    GpOn            = 0x83,
    Idle            = 0x85,

    // 4-byte messages
    LocoSpd         = 0xA0,
    LocoDirf        = 0xA1,
    LocoSnd         = 0xA2,
    SwReq           = 0xB0,
    SwRep           = 0xB1,
    InputRep        = 0xB2,
    Unknown         = 0xB3,
    LongAck         = 0xB4,
    SlotStat1       = 0xB5,
    ConsistFunc     = 0xB6,
    UnlinkSlots     = 0xB8,
    LinkSlots       = 0xB9,
    MoveSlots       = 0xBA,
    RqSlData        = 0xBB,
    SwState         = 0xBC,
    SwAck           = 0xBD,
    LocoAdr         = 0xBF,

    // 6-byte messages
    MultiSense      = 0xD0,

    // Variable-length messages
    Se              = 0xE4,
    // OPC_ANALOGIO (0xE5) shares the opcode byte with OPC_PEER_XFER; both are
    // 0xE5 in ln_opc.h and disambiguated by the body of the message.
    PeerXfer        = 0xE5,
    SlRdData        = 0xE7,
    ImmPacket       = 0xED,
    ImmPacket2      = 0xEE,
    WrSlData        = 0xEF,
}

/// <summary>
/// Helpers for working with the opcode byte.
/// </summary>
public static class OpCodeExtensions
{
    /// <summary>Mask used to clear the high bit of an opcode for ACK matching.</summary>
    public const byte OpCodeMask = 0x7F;

    /// <summary>
    /// Compute the total LocoNet packet size (including opcode and checksum) from the
    /// first one or two bytes of a message.
    /// </summary>
    /// <param name="command">The opcode byte (first byte of the packet).</param>
    /// <param name="sizeByte">For variable-length messages (opcodes in the <c>0xE0</c> range),
    /// the second byte which carries the message size.</param>
    public static int PacketSize(byte command, byte sizeByte)
        => (command & 0x60) == 0x60
            ? sizeByte
            : ((command & 0x60) >> 4) + 2;
}
