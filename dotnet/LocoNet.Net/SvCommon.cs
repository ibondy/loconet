using System;

namespace LocoNet.Net;

/// <summary>Result codes for <see cref="LocoNetSystemVariable.ProcessMessage"/>.</summary>
public enum SvStatus : byte
{
    NotConsumed = 0,
    ConsumedOk = 1,
    Error = 2,
    DeferredProcessingNeeded = 3,
}

/// <summary>LNSV commands (low 6 bits of <c>sv_cmd</c>).</summary>
public enum SvCommand : byte
{
    WriteSingle    = 0x01,
    ReadSingle     = 0x02,
    WriteMasked    = 0x03,
    WriteQuad      = 0x05,
    ReadQuad       = 0x06,
    Discover       = 0x07,
    Identify       = 0x08,
    ChangeAddress  = 0x09,
    Reconfigure    = 0x0F,
}

/// <summary>Standard LNSV addresses (offsets in the SV storage region).</summary>
public static class SvAddr
{
    public const ushort EepromSize     = 1;
    public const ushort SwVersion      = 2;
    public const ushort NodeIdL        = 3;
    public const ushort NodeIdH        = 4;
    public const ushort SerialNumberL  = 5;
    public const ushort SerialNumberH  = 6;
    public const ushort UserBase       = 7;
}

/// <summary>
/// Backing store for <see cref="LocoNetSystemVariable"/>. Replace with EEPROM/file-backed
/// storage in your application; the default is in-memory.
/// </summary>
public interface ISvStorage
{
    /// <summary>Maximum valid offset (inclusive).</summary>
    ushort MaxOffset { get; }
    byte Read(ushort offset);
    void Write(ushort offset, byte value);
}

/// <summary>Simple in-memory <see cref="ISvStorage"/> implementation.</summary>
public sealed class InMemorySvStorage : ISvStorage
{
    private readonly byte[] _data;
    public InMemorySvStorage(int size = 256)
    {
        if (size < 8) throw new ArgumentOutOfRangeException(nameof(size), "Storage must hold at least the SV header bytes.");
        _data = new byte[size];
    }
    public ushort MaxOffset => (ushort)(_data.Length - 1);
    public byte Read(ushort offset) => _data[offset];
    public void Write(ushort offset, byte value) => _data[offset] = value;
}
