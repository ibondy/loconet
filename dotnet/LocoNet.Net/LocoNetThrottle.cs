using System;

namespace LocoNet.Net;

/// <summary>
/// Manages the LocoNet command-station slot for a single locomotive: acquire / select /
/// release plus speed, direction and function control.
/// </summary>
/// <remarks>
/// Port of <c>src/LocoNetThrottle.cpp</c>. Drive the periodic <see cref="Process100msActions"/>
/// from a 100 ms timer in your application to keep the slot refreshed and to flush deferred
/// speed updates.
/// </remarks>
public sealed class LocoNetThrottle
{
    private const int SlotRefreshTicks = 600; // 600 * 100ms = 60s

    private readonly ILocoNet _locoNet;

    private ThrottleState _state = ThrottleState.Free;
    private ushort _ticksSinceLastAction;
    private ushort _throttleId;
    private byte _slot = 0xFF;
    private ushort _address;
    private byte _speed;
    private byte _deferredSpeed;
    private byte _status1;
    private byte _dirFunc0to4;
    private byte _func5to8;
    private byte _userData;
    private ThrottleOptions _options;
    private ThrottleSpeedSteps _speedSteps;

    public LocoNetThrottle(ILocoNet locoNet)
    {
        _locoNet = locoNet ?? throw new ArgumentNullException(nameof(locoNet));
        _locoNet.MessageReceived += OnMessageReceived;
    }

    /// <summary>Configure throttle identity and options.</summary>
    public void Init(byte userData, ThrottleOptions options, ushort throttleId)
    {
        _userData = userData;
        _options = options;
        _throttleId = throttleId;
    }

    // ----- Events -----

    /// <summary>Raised when the controlled loco address changes. Args: (newAddr, oldAddr).</summary>
    public event Action<LocoNetThrottle, ushort, ushort>? AddressChanged;

    /// <summary>Raised when the speed value changes (0 = e-stop, 1 = stop, 2-127 = normal).</summary>
    public event Action<LocoNetThrottle, byte>? SpeedChanged;

    /// <summary>Raised when direction changes (non-zero = reverse bit set).</summary>
    public event Action<LocoNetThrottle, byte>? DirectionChanged;

    /// <summary>Raised when an individual function bit changes. Args: (function 0-8, on/off).</summary>
    public event Action<LocoNetThrottle, byte, bool>? FunctionChanged;

    /// <summary>Raised when slot status 1 changes.</summary>
    public event Action<LocoNetThrottle, byte>? SlotStateChanged;

    /// <summary>Raised on every state transition. Args: (newState, prevState).</summary>
    public event Action<LocoNetThrottle, ThrottleState, ThrottleState>? StateChanged;

    /// <summary>Raised when the speed-step mode changes.</summary>
    public event Action<LocoNetThrottle, ThrottleSpeedSteps>? SpeedStepsChanged;

    /// <summary>Raised when a throttle operation fails.</summary>
    public event Action<LocoNetThrottle, ThrottleError>? Error;

    // ----- Public state -----

    public ushort Address => _address;
    public byte Slot => _slot;
    public byte Speed => SwapSpeedZeroAndEmStop(_speed);
    public byte Direction => (byte)(_dirFunc0to4 & LnConstants.DirfDir);
    public ThrottleState State => _state;
    public ThrottleSpeedSteps SpeedSteps
    {
        get => _speedSteps;
        set => UpdateSpeedSteps(value, forceNotify: false);
    }

    public byte GetFunction(byte function)
    {
        if (function <= 4)
        {
            byte mask = (byte)(1 << (function != 0 ? function - 1 : 4));
            return (byte)(_dirFunc0to4 & mask);
        }
        byte mask5 = (byte)(1 << (function - 5));
        return (byte)(_func5to8 & mask5);
    }

    // ----- Commands -----

    public ThrottleError SetAddress(ushort address)
    {
        if (_state == ThrottleState.Free)
        {
            UpdateAddress(address, forceNotify: true);
            UpdateState(ThrottleState.Select, forceNotify: true);
            _locoNet.Send(OpCode.LocoAdr, (byte)(address >> 7), (byte)(address & 0x7F));
            return ThrottleError.Ok;
        }
        return Fail(ThrottleError.Busy);
    }

    public ThrottleError StealAddress(ushort address)
    {
        if (_state <= ThrottleState.Release)
        {
            UpdateAddress(address, forceNotify: true);
            UpdateState(ThrottleState.SlotSteal, forceNotify: true);
            _locoNet.Send(OpCode.LocoAdr, (byte)(address >> 7), (byte)(address & 0x7F));
            return ThrottleError.Ok;
        }
        return Fail(ThrottleError.Busy);
    }

    public ThrottleError ResumeAddress(ushort address, byte lastSlot)
    {
        if (_state == ThrottleState.Free)
        {
            _slot = lastSlot;
            UpdateAddress(address, forceNotify: true);
            UpdateState(ThrottleState.SlotResume, forceNotify: true);
            _locoNet.Send(OpCode.RqSlData, lastSlot, 0);
            return ThrottleError.Ok;
        }
        return Fail(ThrottleError.Busy);
    }

    public ThrottleError FreeAddress()
    {
        if (_state == ThrottleState.InUse)
        {
            _locoNet.Send(OpCode.SlotStat1, _slot, (byte)(_status1 & ~(LnConstants.Stat1SlBusy | LnConstants.Stat1SlActive)));
            _slot = 0xFF;
            UpdateState(ThrottleState.Free, forceNotify: true);
            return ThrottleError.Ok;
        }
        return Fail(ThrottleError.NotSelected);
    }

    public ThrottleError FreeAddressForce(ushort address)
    {
        if (_state <= ThrottleState.Release)
        {
            UpdateAddress(address, forceNotify: true);
            UpdateState(ThrottleState.SlotForceFree, forceNotify: true);
            _locoNet.Send(OpCode.LocoAdr, (byte)(address >> 7), (byte)(address & 0x7F));
            return ThrottleError.Ok;
        }
        return Fail(ThrottleError.Busy);
    }

    public ThrottleError DispatchAddress()
    {
        if (_state == ThrottleState.InUse)
        {
            UpdateState(ThrottleState.Free, forceNotify: true);
            _locoNet.Send(OpCode.MoveSlots, _slot, 0);
            return ThrottleError.Ok;
        }
        return Fail(ThrottleError.NotSelected);
    }

    public ThrottleError DispatchAddress(ushort address)
    {
        if (_state <= ThrottleState.Release)
        {
            UpdateAddress(address, forceNotify: true);
            UpdateState(ThrottleState.Dispatch, forceNotify: true);
            _locoNet.Send(OpCode.LocoAdr, (byte)(address >> 7), (byte)(address & 0x7F));
            return ThrottleError.Ok;
        }
        return Fail(ThrottleError.Busy);
    }

    public ThrottleError AcquireAddress()
    {
        if (_state == ThrottleState.Free)
        {
            UpdateState(ThrottleState.Acquire, forceNotify: true);
            _locoNet.Send(OpCode.MoveSlots, 0, 0);
            return ThrottleError.Ok;
        }
        return Fail(ThrottleError.Busy);
    }

    public void ReleaseAddress()
    {
        if (_state == ThrottleState.InUse)
        {
            _locoNet.Send(OpCode.SlotStat1, _slot, (byte)(_status1 & ~LnConstants.Stat1SlBusy));
        }
        _slot = 0xFF;
        UpdateState(ThrottleState.Free, forceNotify: true);
    }

    public ThrottleError IdleAddress()
    {
        if (_state == ThrottleState.InUse)
        {
            _locoNet.Send(OpCode.SlotStat1, _slot, (byte)(_status1 & ~LnConstants.Stat1SlActive));
            _slot = 0xFF;
            UpdateState(ThrottleState.Idle, forceNotify: true);
            return ThrottleError.Ok;
        }
        return Fail(ThrottleError.NotSelected);
    }

    public ThrottleError SetSpeed(byte speed)
    {
        if (_state != ThrottleState.InUse)
        {
            return Fail(ThrottleError.NotSelected);
        }

        speed = SwapSpeedZeroAndEmStop(speed);
        if (_speed != speed)
        {
            if ((_options & ThrottleOptions.DeferredSpeed) != 0 && (speed > 1 || _ticksSinceLastAction == 0))
            {
                _deferredSpeed = speed;
            }
            else
            {
                _locoNet.Send(OpCode.LocoSpd, _slot, speed);
                _ticksSinceLastAction = 0;
                _deferredSpeed = 0;
            }
        }
        return ThrottleError.Ok;
    }

    public ThrottleError SetDirection(byte direction)
    {
        if (_state != ThrottleState.InUse)
        {
            return Fail(ThrottleError.NotSelected);
        }

        byte newDirFunc = direction != 0
            ? (byte)(_dirFunc0to4 | LnConstants.DirfDir)
            : (byte)(_dirFunc0to4 & ~LnConstants.DirfDir);
        _locoNet.Send(OpCode.LocoDirf, _slot, newDirFunc);
        _ticksSinceLastAction = 0;
        return ThrottleError.Ok;
    }

    public ThrottleError SetFunction(byte function, byte value)
    {
        if (_state != ThrottleState.InUse)
        {
            return Fail(ThrottleError.NotSelected);
        }

        OpCode opCode;
        byte data;
        byte mask;
        if (function <= 4)
        {
            opCode = OpCode.LocoDirf;
            data = _dirFunc0to4;
            mask = (byte)(1 << (function != 0 ? function - 1 : 4));
        }
        else
        {
            opCode = OpCode.LocoSnd;
            data = _func5to8;
            mask = (byte)(1 << (function - 5));
        }

        data = value != 0 ? (byte)(data | mask) : (byte)(data & ~mask);
        _locoNet.Send(opCode, _slot, data);
        _ticksSinceLastAction = 0;
        return ThrottleError.Ok;
    }

    public ThrottleError SetDirFunc0To4Direct(byte value)
    {
        if (_state != ThrottleState.InUse) return Fail(ThrottleError.NotSelected);
        _locoNet.Send(OpCode.LocoDirf, _slot, (byte)(value & 0x7F));
        return ThrottleError.Ok;
    }

    public ThrottleError SetFunc5To8Direct(byte value)
    {
        if (_state != ThrottleState.InUse) return Fail(ThrottleError.NotSelected);
        _locoNet.Send(OpCode.LocoSnd, _slot, (byte)(value & 0x7F));
        return ThrottleError.Ok;
    }

    /// <summary>Drive every 100 ms to refresh the slot and flush deferred speed updates.</summary>
    public void Process100msActions()
    {
        if (_state != ThrottleState.InUse)
        {
            return;
        }

        _ticksSinceLastAction++;
        if (_deferredSpeed != 0 || _ticksSinceLastAction > SlotRefreshTicks)
        {
            byte speed = _deferredSpeed != 0 ? _deferredSpeed : _speed;
            _locoNet.Send(OpCode.LocoSpd, _slot, speed);
            _deferredSpeed = 0;
            _ticksSinceLastAction = 0;
        }
    }

    // ----- Inbound message handling -----

    private void OnMessageReceived(object? sender, LnMessageEventArgs e)
    {
        var msg = e.Message;
        switch (msg.OpCode)
        {
            case OpCode.SlRdData:
                HandleSlotData(msg);
                break;
            case OpCode.LocoSpd:
            case OpCode.LocoDirf:
            case OpCode.LocoSnd:
            case OpCode.SlotStat1:
                HandleLocoData(msg);
                break;
            case OpCode.LongAck:
                HandleLongAck(msg);
                break;
        }
    }

    private void HandleSlotData(LnMsg msg)
    {
        if (msg.Length != 14)
        {
            return;
        }
        var sd = new SlotDataMsg(msg);
        ushort slotAddress = sd.Address;

        if (_slot == sd.Slot)
        {
            // Slot we already own (or are negotiating for)
            if (_address == slotAddress)
            {
                if (_state == ThrottleState.SlotResume && _throttleId != sd.ThrottleId)
                {
                    UpdateState(ThrottleState.Free, forceNotify: true);
                    Error?.Invoke(this, ThrottleError.NoLoco);
                }
                else
                {
                    UpdateState(ThrottleState.InUse, forceNotify: true);
                    UpdateAddress(slotAddress, forceNotify: true);
                    UpdateSpeed(sd.Speed, forceNotify: true);
                    UpdateDirectionAndFunctions(sd.DirFunc, forceNotify: true);
                    UpdateFunctions5To8(sd.Sound, forceNotify: true);
                    UpdateSpeedSteps(_speedSteps, forceNotify: true);
                    UpdateState(ThrottleState.InUse, forceNotify: true);

                    // Write our throttle id back to the slot.
                    byte newStat = (byte)((sd.Stat & 0xF8) | (byte)_speedSteps);
                    var tx = sd.ToWrSlData(
                        stat: newStat,
                        id1: (byte)(_throttleId & 0x7F),
                        id2: (byte)(_throttleId >> 7));
                    _locoNet.Send(tx);
                }
            }
            else if (_state == ThrottleState.SlotMove)
            {
                // Another throttle did a NULL MOVE with the same slot — retry.
                UpdateState(ThrottleState.Select, forceNotify: true);
                _locoNet.Send(OpCode.LocoAdr, (byte)(_address >> 7), (byte)(_address & 0x7F));
            }
        }
        else if (_address == slotAddress)
        {
            // Slot data for our address but a different slot — likely the response to a select/dispatch/steal.
            if (_state == ThrottleState.Select || _state == ThrottleState.Dispatch)
            {
                if ((sd.Stat & LnConstants.Stat1SlConup) == 0 &&
                    (sd.Stat & LnConstants.LocoInUse) != LnConstants.LocoInUse)
                {
                    byte data2;
                    if (_state == ThrottleState.Select)
                    {
                        UpdateState(ThrottleState.SlotMove, forceNotify: true);
                        _slot = sd.Slot;
                        data2 = sd.Slot;
                    }
                    else
                    {
                        UpdateState(ThrottleState.Free, forceNotify: true);
                        data2 = 0;
                    }
                    _locoNet.Send(OpCode.MoveSlots, sd.Slot, data2);
                }
                else
                {
                    Error?.Invoke(this, ThrottleError.SlotInUse);
                    UpdateState(ThrottleState.Free, forceNotify: true);
                }
            }
            else if (_state == ThrottleState.SlotSteal)
            {
                if ((sd.Stat & LnConstants.Stat1SlConup) == 0 &&
                    (sd.Stat & LnConstants.LocoInUse) == LnConstants.LocoInUse)
                {
                    _slot = sd.Slot;
                    UpdateState(ThrottleState.InUse, forceNotify: true);
                    UpdateAddress(slotAddress, forceNotify: true);
                    UpdateSpeed(sd.Speed, forceNotify: true);
                    UpdateDirectionAndFunctions(sd.DirFunc, forceNotify: true);
                    UpdateFunctions5To8(sd.Sound, forceNotify: true);
                    UpdateStatus1(sd.Stat, forceNotify: true);
                    UpdateState(ThrottleState.InUse, forceNotify: true);
                }
                else
                {
                    Error?.Invoke(this, ThrottleError.NoLoco);
                    UpdateState(ThrottleState.Free, forceNotify: true);
                }
            }
            else if (_state == ThrottleState.SlotForceFree)
            {
                _locoNet.Send(OpCode.SlotStat1, sd.Slot, (byte)(_status1 & ~(LnConstants.Stat1SlBusy | LnConstants.Stat1SlActive)));
                _slot = 0xFF;
                UpdateState(ThrottleState.Free, forceNotify: true);
            }
        }

        if (_state == ThrottleState.Acquire)
        {
            _slot = sd.Slot;
            UpdateState(ThrottleState.InUse, forceNotify: true);
            UpdateAddress(slotAddress, forceNotify: true);
            UpdateSpeed(sd.Speed, forceNotify: true);
            UpdateDirectionAndFunctions(sd.DirFunc, forceNotify: true);
            UpdateStatus1(sd.Stat, forceNotify: true);
        }
    }

    private void HandleLocoData(LnMsg msg)
    {
        if (msg.Length != 4)
        {
            return;
        }
        var ld = new LocoDataMsg(msg);
        if (_slot != ld.Slot)
        {
            return;
        }

        switch (msg.OpCode)
        {
            case OpCode.LocoSpd:    UpdateSpeed(ld.Data, forceNotify: false); break;
            case OpCode.LocoDirf:   UpdateDirectionAndFunctions(ld.Data, forceNotify: false); break;
            case OpCode.LocoSnd:    UpdateFunctions5To8(ld.Data, forceNotify: false); break;
            case OpCode.SlotStat1:  UpdateStatus1(ld.Data, forceNotify: false); break;
        }
    }

    private void HandleLongAck(LnMsg msg)
    {
        if (msg.Length != 4)
        {
            return;
        }
        var ack = new LongAckMsg(msg);
        if (_state < ThrottleState.Acquire || _state > ThrottleState.SlotMove)
        {
            return;
        }

        if (ack.Opcode == ((byte)OpCode.MoveSlots & OpCodeExtensions.OpCodeMask))
        {
            Error?.Invoke(this, ThrottleError.NoLoco);
        }
        else if (ack.Opcode == ((byte)OpCode.LocoAdr & OpCodeExtensions.OpCodeMask))
        {
            Error?.Invoke(this, ThrottleError.NoSlots);
        }
        UpdateState(ThrottleState.Free, forceNotify: true);
    }

    // ----- State helpers -----

    private void UpdateAddress(ushort address, bool forceNotify)
    {
        if (!forceNotify && _address == address) return;
        ushort old = _address;
        _address = address;
        AddressChanged?.Invoke(this, _address, old);
    }

    private void UpdateSpeed(byte speed, bool forceNotify)
    {
        if (!forceNotify && _speed == speed) return;
        _speed = speed;
        SpeedChanged?.Invoke(this, SwapSpeedZeroAndEmStop(speed));
    }

    private void UpdateState(ThrottleState state, bool forceNotify)
    {
        if (!forceNotify && _state == state) return;
        var prev = _state;
        _state = state;
        StateChanged?.Invoke(this, state, prev);
    }

    private void UpdateStatus1(byte status, bool forceNotify)
    {
        if (!forceNotify && _status1 == status) return;
        _status1 = status;
        SlotStateChanged?.Invoke(this, status);
        UpdateState((status & LnConstants.LocoInUse) == LnConstants.LocoInUse ? ThrottleState.InUse : ThrottleState.Free, forceNotify);
    }

    private void UpdateDirectionAndFunctions(byte dirFunc, bool forceNotify)
    {
        if (!forceNotify && _dirFunc0to4 == dirFunc) return;
        byte diffs = (byte)(_dirFunc0to4 ^ dirFunc);
        _dirFunc0to4 = dirFunc;

        if (FunctionChanged is not null)
        {
            for (byte fn = 1, mask = 1; fn <= 4; fn++, mask <<= 1)
            {
                if (forceNotify || (diffs & mask) != 0)
                {
                    FunctionChanged.Invoke(this, fn, (dirFunc & mask) != 0);
                }
            }
            if (forceNotify || (diffs & LnConstants.DirfF0) != 0)
            {
                FunctionChanged.Invoke(this, 0, (dirFunc & LnConstants.DirfF0) != 0);
            }
        }

        if (forceNotify || (diffs & LnConstants.DirfDir) != 0)
        {
            DirectionChanged?.Invoke(this, (byte)(dirFunc & LnConstants.DirfDir));
        }
    }

    private void UpdateFunctions5To8(byte func5to8, bool forceNotify)
    {
        if (FunctionChanged is null) return;
        if (!forceNotify && _func5to8 == func5to8) return;

        byte diffs = (byte)(_func5to8 ^ func5to8);
        _func5to8 = func5to8;
        for (byte fn = 5, mask = 1; fn <= 8; fn++, mask <<= 1)
        {
            if (forceNotify || (diffs & mask) != 0)
            {
                FunctionChanged.Invoke(this, fn, (func5to8 & mask) != 0);
            }
        }
    }

    private void UpdateSpeedSteps(ThrottleSpeedSteps steps, bool forceNotify)
    {
        if (SpeedStepsChanged is null) return;
        if (!forceNotify && (_status1 & 0x07) == (byte)steps) return;

        _speedSteps = steps;
        _status1 = (byte)((_status1 & 0xF8) | (byte)steps);
        SpeedStepsChanged.Invoke(this, steps);
    }

    private ThrottleError Fail(ThrottleError err)
    {
        Error?.Invoke(this, err);
        return err;
    }

    private static byte SwapSpeedZeroAndEmStop(byte speed) => speed switch
    {
        0 => 1,
        1 => 0,
        _ => speed,
    };
}
