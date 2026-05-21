using System;

namespace LocoNet.Net;

/// <summary>
/// LocoNet fast-clock slave (slot 0x7B). Polls the command station's fast-clock slot,
/// keeps a local copy in sync, and ticks forward between updates.
/// </summary>
/// <remarks>
/// Managed port of the original LocoNet2 fast-clock helper. Drive <see cref="Process66msActions"/> from a
/// 65 ms timer; the algorithm assumes a 65 ms tick rate.
/// </remarks>
public sealed class LocoNetFastClock : IDisposable
{
    private const ushort FcFracMinBase = 0x3FFF;
    private const byte FcFracResetHigh = 0x78;
    private const byte FcFracResetLow  = 0x6D;

    // Field offsets within a 14-byte fast-clock slot frame (same layout as slot data).
    private const int IxSlot       = 2;
    private const int IxClkRate    = 3;
    private const int IxFracMinsL  = 4;
    private const int IxFracMinsH  = 5;
    private const int IxMins60     = 6;
    private const int IxTrackStat  = 7;
    private const int IxHours24    = 8;
    private const int IxDays       = 9;
    private const int IxClkCntrl   = 10;
    private const int IxId1        = 11;
    private const int IxId2        = 12;

    private enum FcState : byte { Idle, ReqTime, Ready, Disabled }

    private readonly ILocoNet _locoNet;
    private readonly bool _dcs100CompatibleSpeed;
    private readonly bool _correctDcs100Clock;
    private readonly byte[] _data = new byte[14]; // local copy of the FC slot frame (without checksum)

    private FcState _state = FcState.Idle;

    /// <summary>
    /// Raised on every fast-clock update. Args: <c>(rate, days, hours, minutes, syncFromMaster)</c>.
    /// </summary>
    public event Action<byte, byte, byte, byte, bool>? Updated;

    /// <summary>
    /// Raised on every fractional-minute tick. The value is a 14-bit counter that counts
    /// down from <c>0x3FFF</c> within the current minute.
    /// </summary>
    public event Action<ushort>? FractionalMinuteUpdated;

    public LocoNetFastClock(ILocoNet locoNet, bool dcs100CompatibleSpeed, bool correctDcs100Clock)
    {
        _locoNet = locoNet ?? throw new ArgumentNullException(nameof(locoNet));
        _dcs100CompatibleSpeed = dcs100CompatibleSpeed;
        _correctDcs100Clock = correctDcs100Clock;
        _locoNet.MessageReceived += OnMessageReceived;
    }

    /// <summary>Force an immediate slot read request.</summary>
    public void Poll() => _locoNet.Send(OpCode.RqSlData, LnConstants.FastClockSlot, 0);

    /// <summary>Drive every ~65 ms.</summary>
    public void Process66msActions()
    {
        if (_state == FcState.Ready)
        {
            _data[IxFracMinsL] += _data[IxClkRate];
            if ((_data[IxFracMinsL] & 0x80) != 0)
            {
                _data[IxFracMinsL] &= 0x7F;
                _data[IxFracMinsH]++;

                if ((_data[IxFracMinsH] & 0x80) != 0)
                {
                    _data[IxFracMinsL] = FcFracResetLow;
                    _data[IxFracMinsH] = (byte)(FcFracResetHigh + (_dcs100CompatibleSpeed ? 1 : 0));

                    _data[IxMins60]++;
                    if (_data[IxMins60] >= 0x7F)
                    {
                        _data[IxMins60] = 127 - 60;
                        _data[IxHours24]++;
                        if ((_data[IxHours24] & 0x80) != 0)
                        {
                            _data[IxHours24] = 128 - 24;
                            _data[IxDays]++;
                        }
                    }

                    if (_correctDcs100Clock)
                    {
                        // Build a 13-byte payload (FromPayload appends the checksum byte,
                        // yielding the canonical 14-byte WrSlData frame).
                        _data[0] = (byte)OpCode.WrSlData;
                        _data[1] = 0x0E;
                        _data[IxSlot] = LnConstants.FastClockSlot;
                        _locoNet.Send(LnMsg.FromPayload(_data.AsSpan(0, 13)));
                    }
                    else
                    {
                        RaiseUpdate(syncFromMaster: false);
                    }
                }
            }

            FractionalMinuteUpdated?.Invoke((ushort)(FcFracMinBase - ((_data[IxFracMinsH] << 7) + _data[IxFracMinsL])));
        }

        if (_state == FcState.Idle)
        {
            _locoNet.Send(OpCode.RqSlData, LnConstants.FastClockSlot, 0);
            _state = FcState.ReqTime;
        }
    }

    private void OnMessageReceived(object? sender, LnMessageEventArgs e)
    {
        var msg = e.Message;
        if ((msg.OpCode != OpCode.SlRdData && msg.OpCode != OpCode.WrSlData) || msg.Length != 14)
        {
            return;
        }
        if (msg[IxSlot] != LnConstants.FastClockSlot)
        {
            return;
        }

        if ((msg[IxClkCntrl] & 0x40) == 0)
        {
            _state = FcState.Disabled;
            return;
        }

        if (_state < FcState.ReqTime)
        {
            return;
        }

        msg.Bytes.CopyTo(_data);
        RaiseUpdate(syncFromMaster: true);
        FractionalMinuteUpdated?.Invoke((ushort)(FcFracMinBase - ((_data[IxFracMinsH] << 7) + _data[IxFracMinsL])));
        _state = FcState.Ready;
    }

    private void RaiseUpdate(bool syncFromMaster)
    {
        byte hours = _data[IxHours24] >= (128 - 24)
            ? (byte)(_data[IxHours24] - (128 - 24))
            : (byte)(_data[IxHours24] % 24);
        byte minutes = (byte)(_data[IxMins60] - (127 - 60));
        Updated?.Invoke(_data[IxClkRate], _data[IxDays], hours, minutes, syncFromMaster);
    }

    public void Dispose() => _locoNet.MessageReceived -= OnMessageReceived;
}
