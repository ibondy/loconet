namespace LocoNet.Net;

/// <summary>
/// Bit-mask and field constants from the LocoNet protocol definitions.
/// </summary>
public static class LnConstants
{
    // DIRF / SND bits
    public const byte DirfDir = 0x20;
    public const byte DirfF0  = 0x10;
    public const byte DirfF4  = 0x08;
    public const byte DirfF3  = 0x04;
    public const byte DirfF2  = 0x02;
    public const byte DirfF1  = 0x01;

    public const byte SndF8 = 0x08;
    public const byte SndF7 = 0x04;
    public const byte SndF6 = 0x02;
    public const byte SndF5 = 0x01;

    // OPC_SW_* fields
    public const byte SwAckClosed = 0x20;
    public const byte SwAckOutput = 0x10;
    public const byte SwReqDir    = 0x20;
    public const byte SwReqOut    = 0x10;
    public const byte SwRepSw     = 0x20;
    public const byte SwRepHi     = 0x10;
    public const byte SwRepClosed = 0x20;
    public const byte SwRepThrown = 0x10;
    public const byte SwRepInputs = 0x40;

    public const byte InputRepCb = 0x40;
    public const byte InputRepSw = 0x20;
    public const byte InputRepHi = 0x10;

    public const byte LocoSpdEStop = 0x01;

    // Slot status 1
    public const byte Stat1SlSpurge = 0x80;
    public const byte Stat1SlConup  = 0x40;
    public const byte Stat1SlBusy   = 0x20;
    public const byte Stat1SlActive = 0x10;
    public const byte Stat1SlCondn  = 0x08;
    public const byte Stat1SlSpdEx  = 0x04;
    public const byte Stat1SlSpd14  = 0x02;
    public const byte Stat1SlSpd28  = 0x01;

    // Loco use determination
    public const byte LocoStatMask = Stat1SlBusy | Stat1SlActive;
    public const byte LocoInUse    = Stat1SlBusy | Stat1SlActive;
    public const byte LocoIdle     = Stat1SlBusy;
    public const byte LocoCommon   = Stat1SlActive;
    public const byte LocoFree     = 0;

    // Track status
    public const byte GtrkProgBusy = 0x08;
    public const byte GtrkMlok1    = 0x04;
    public const byte GtrkIdle     = 0x02;
    public const byte GtrkPower    = 0x01;

    // Well-known slot numbers
    public const byte FastClockSlot   = 0x7B;
    public const byte ProgrammingSlot = 0x7C;

    // Programmer status
    public const byte PStatUserAborted = 0x08;
    public const byte PStatReadFail    = 0x04;
    public const byte PStatWriteFail   = 0x02;
    public const byte PStatNoDecoder   = 0x01;

    // LocoNet checksum seed (used by the byte-stream framer).
    public const byte ChecksumSeed = 0xFF;
}
