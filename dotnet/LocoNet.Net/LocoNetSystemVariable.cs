using System;

namespace LocoNet.Net;

/// <summary>
/// LocoNet System Variable (LNSV) server. Implements the LNSV1/LNSV2 protocol as a
/// responder: handles <c>WRITE_SINGLE</c>, <c>READ_SINGLE</c>, <c>WRITE_MASKED</c>,
/// <c>WRITE_QUAD</c>, <c>READ_QUAD</c>, <c>DISCOVER</c>, <c>IDENTIFY</c>,
/// <c>CHANGE_ADDRESS</c>, and <c>RECONFIGURE</c>.
/// </summary>
/// <remarks>Port of <c>src/LocoNetSystemVariable.cpp</c>.</remarks>
public sealed class LocoNetSystemVariable : IDisposable
{
    private readonly ILocoNet _locoNet;
    private readonly ISvStorage _storage;
    private readonly byte _mfgId;
    private readonly byte _devId;
    private readonly ushort _productId;

    private bool _deferredProcessingRequired;
    private byte _deferredSrcAddr;

    /// <summary>Raised after a value has been written to storage. Args: (offset, oldValue, newValue).</summary>
    public event Action<ushort, byte, byte>? SvChanged;

    /// <summary>Raised on receipt of an LNSV <c>RECONFIGURE</c> command after the reply has been sent.</summary>
    public event Action? Reconfigure;

    /// <summary>Manufacturer ID used in IDENTIFY responses (e.g. <c>13</c> = MfgIdDiy).</summary>
    public const byte MfgIdDiy = 13;

    public LocoNetSystemVariable(
        ILocoNet locoNet,
        ISvStorage storage,
        byte mfgId,
        byte devId,
        ushort productId,
        byte swVersion)
    {
        _locoNet = locoNet ?? throw new ArgumentNullException(nameof(locoNet));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _mfgId = mfgId;
        _devId = devId;
        _productId = productId;
        // Persist SW version so IDENTIFY can return it from storage if desired.
        _storage.Write(SvAddr.SwVersion, swVersion);
        _locoNet.MessageReceived += OnMessageReceived;
    }

    /// <summary>Read a single byte from SV storage.</summary>
    public byte ReadStorage(ushort offset) => _storage.Read(offset);

    /// <summary>Write a single byte to SV storage (raises <see cref="SvChanged"/> on change).</summary>
    public void WriteStorage(ushort offset, byte value)
    {
        byte old = _storage.Read(offset);
        if (old == value) return;
        _storage.Write(offset, value);
        SvChanged?.Invoke(offset, old, value);
    }

    /// <summary>Compose the 16-bit node id from the two header bytes.</summary>
    public ushort ReadNodeId() => (ushort)((_storage.Read(SvAddr.NodeIdH) << 8) | _storage.Read(SvAddr.NodeIdL));

    /// <summary>Store a new 16-bit node id and return the value read back from storage.</summary>
    public ushort WriteNodeId(ushort newNodeId)
    {
        WriteStorage(SvAddr.NodeIdH, (byte)(newNodeId >> 8));
        WriteStorage(SvAddr.NodeIdL, (byte)(newNodeId & 0xFF));
        return ReadNodeId();
    }

    /// <summary>Flush any deferred reply (e.g. DISCOVER) that could not be sent earlier.</summary>
    public SvStatus DoDeferredProcessing()
    {
        if (!_deferredProcessingRequired) return SvStatus.ConsumedOk;

        Span<byte> frame = stackalloc byte[15];
        BuildSvFrame(frame, src: _deferredSrcAddr, svCmd: (byte)((byte)SvCommand.Discover | 0x40));
        Span<byte> data = stackalloc byte[8];
        FillIdentifyPayload(data);
        SvPeerData.Encode(frame, data);

        _locoNet.Send(LnMsg.FromPayload(frame));
        _deferredProcessingRequired = false;
        return SvStatus.ConsumedOk;
    }

    private void OnMessageReceived(object? sender, LnMessageEventArgs e)
    {
        ProcessMessage(e.Message);
    }

    /// <summary>Process a single incoming LocoNet message; returns whether it was consumed.</summary>
    public SvStatus ProcessMessage(LnMsg msg)
    {
        // mesg_size 0x10, OPC_PEER_XFER, sv_type 0x02, reply bit must be 0, both PXCT prefixes start with 0x10.
        if (msg.Length != 16 || msg.OpCode != OpCode.PeerXfer) return SvStatus.NotConsumed;
        if (msg[1] != 0x10) return SvStatus.NotConsumed;
        if (msg[4] != 0x02) return SvStatus.NotConsumed;
        if ((msg[3] & 0x40) != 0) return SvStatus.NotConsumed;
        if ((msg[5] & 0xF0) != 0x10 || (msg[10] & 0xF0) != 0x10) return SvStatus.NotConsumed;

        Span<byte> frame = stackalloc byte[15];
        msg.Bytes[..15].CopyTo(frame);

        Span<byte> data = stackalloc byte[8];
        SvPeerData.Decode(frame, data);

        ushort destinationId = (ushort)(data[0] | (data[1] << 8));
        ushort svAddress     = (ushort)(data[2] | (data[3] << 8));
        ushort productId     = (ushort)(data[4] | (data[5] << 8));
        ushort serialNumber  = (ushort)(data[6] | (data[7] << 8));

        SvCommand cmd = (SvCommand)frame[3];
        byte src = frame[2];

        if (cmd != SvCommand.Discover && cmd != SvCommand.ChangeAddress && destinationId != ReadNodeId())
        {
            return SvStatus.NotConsumed;
        }

        switch (cmd)
        {
            case SvCommand.WriteSingle:
                if (!CheckAddressRange(svAddress, 1)) return SvStatus.Error;
                WriteStorage(svAddress, data[4]);
                data[4] = ReadStorage(svAddress);
                break;

            case SvCommand.ReadSingle:
                if (!CheckAddressRange(svAddress, 1)) return SvStatus.Error;
                data[4] = ReadStorage(svAddress);
                break;

            case SvCommand.WriteMasked:
                if (!CheckAddressRange(svAddress, 1)) return SvStatus.Error;
                {
                    byte oldMasked = (byte)(ReadStorage(svAddress) & (~data[5] & 0xFF));
                    byte newMasked = (byte)(data[4] & data[5]);
                    WriteStorage(svAddress, (byte)(oldMasked | newMasked));
                    data[4] = ReadStorage(svAddress);
                }
                break;

            case SvCommand.WriteQuad:
                if (!CheckAddressRange(svAddress, 4)) return SvStatus.Error;
                WriteStorage((ushort)(svAddress + 0), data[4]);
                WriteStorage((ushort)(svAddress + 1), data[5]);
                WriteStorage((ushort)(svAddress + 2), data[6]);
                WriteStorage((ushort)(svAddress + 3), data[7]);
                data[4] = ReadStorage((ushort)(svAddress + 0));
                data[5] = ReadStorage((ushort)(svAddress + 1));
                data[6] = ReadStorage((ushort)(svAddress + 2));
                data[7] = ReadStorage((ushort)(svAddress + 3));
                break;

            case SvCommand.ReadQuad:
                if (!CheckAddressRange(svAddress, 4)) return SvStatus.Error;
                data[4] = ReadStorage((ushort)(svAddress + 0));
                data[5] = ReadStorage((ushort)(svAddress + 1));
                data[6] = ReadStorage((ushort)(svAddress + 2));
                data[7] = ReadStorage((ushort)(svAddress + 3));
                break;

            case SvCommand.Discover:
                _deferredSrcAddr = src;
                _deferredProcessingRequired = true;
                return SvStatus.DeferredProcessingNeeded;

            case SvCommand.Identify:
                FillIdentifyPayload(data);
                break;

            case SvCommand.ChangeAddress:
                // Validate the addressing fields.
                byte addrMfg = (byte)(svAddress & 0xFF);
                byte addrDev = (byte)(svAddress >> 8);
                if (_mfgId != addrMfg || _devId != addrDev) return SvStatus.NotConsumed;
                if (_productId != productId) return SvStatus.NotConsumed;
                if (ReadStorage(SvAddr.SerialNumberL) != (byte)(serialNumber & 0xFF)) return SvStatus.NotConsumed;
                if (ReadStorage(SvAddr.SerialNumberH) != (byte)(serialNumber >> 8)) return SvStatus.NotConsumed;

                if (WriteNodeId(destinationId) != destinationId)
                {
                    _locoNet.Send(OpCode.LongAck, (byte)((byte)OpCode.PeerXfer & 0x7F), 44);
                    return SvStatus.ConsumedOk;
                }
                break;

            case SvCommand.Reconfigure:
                // Reply first, then fire the event below.
                break;

            default:
                _locoNet.Send(OpCode.LongAck, (byte)((byte)OpCode.PeerXfer & 0x7F), 43);
                return SvStatus.Error;
        }

        // Send the reply: mirror the incoming frame with the reply bit set on sv_cmd.
        SvPeerData.Encode(frame, data);
        frame[3] |= 0x40;
        _locoNet.Send(LnMsg.FromPayload(frame));

        if (cmd == SvCommand.Reconfigure)
        {
            Reconfigure?.Invoke();
        }
        return SvStatus.ConsumedOk;
    }

    private bool CheckAddressRange(ushort startAddress, byte count)
    {
        for (int i = 0; i < count; i++)
        {
            ushort offset = (ushort)(startAddress + i);
            if (offset < SvAddr.EepromSize || offset > _storage.MaxOffset)
            {
                _locoNet.Send(OpCode.LongAck, (byte)((byte)OpCode.PeerXfer & 0x7F), 42);
                return false;
            }
        }
        return true;
    }

    private void FillIdentifyPayload(Span<byte> data)
    {
        ushort nodeId = ReadNodeId();
        data[0] = (byte)(nodeId & 0xFF);
        data[1] = (byte)(nodeId >> 8);
        data[2] = _mfgId;
        data[3] = _devId;
        data[4] = (byte)(_productId & 0xFF);
        data[5] = (byte)(_productId >> 8);
        data[6] = ReadStorage(SvAddr.SerialNumberL);
        data[7] = ReadStorage(SvAddr.SerialNumberH);
    }

    private void BuildSvFrame(Span<byte> frame, byte src, byte svCmd)
    {
        frame[0] = (byte)OpCode.PeerXfer;
        frame[1] = 0x10;
        frame[2] = src;
        frame[3] = svCmd;
        frame[4] = 0x02; // sv_type
        frame[5] = 0x10; // svx1
        frame[10] = 0x10; // svx2
    }

    public void Dispose() => _locoNet.MessageReceived -= OnMessageReceived;
}
