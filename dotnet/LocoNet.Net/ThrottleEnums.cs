using System;

namespace LocoNet.Net;

/// <summary>State machine states for <see cref="LocoNetThrottle"/>.</summary>
public enum ThrottleState : byte
{
    Free = 0,
    Idle,
    Release,
    Acquire,
    Select,
    Dispatch,
    SlotMove,
    SlotForceFree,
    SlotResume,
    SlotSteal,
    InUse,
}

/// <summary>Decoder speed-step modes (low 3 bits of slot status 1).</summary>
public enum ThrottleSpeedSteps : byte
{
    Steps28      = 0, // 000 = 28 step / 3-byte packet regular mode
    Steps28Tri   = 1, // 001 = 28 step Motorola trinary
    Steps14      = 2, // 010 = 14 step
    Steps128     = 3, // 011 = 128 step
    Steps28Adv   = 4, // 100 = 28 step advanced consisting
    Steps128Adv  = 7, // 111 = 128 step advanced consisting
}

/// <summary>Error codes reported via <see cref="LocoNetThrottle.Error"/>.</summary>
public enum ThrottleError : byte
{
    Ok = 0,
    SlotInUse,
    Busy,
    NotSelected,
    NoLoco,
    NoSlots,
}

/// <summary>Throttle options bit flags.</summary>
[Flags]
public enum ThrottleOptions : byte
{
    None           = 0,
    DeferredSpeed  = 0x01,
}
