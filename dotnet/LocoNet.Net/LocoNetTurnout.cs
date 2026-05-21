using System;

namespace LocoNet.Net;

/// <summary>
/// A single LocoNet turnout. Send-only helper
/// that emits <c>OPC_SW_REQ</c> messages for the configured 1-based address.
/// </summary>
public sealed class LocoNetTurnout
{
    private readonly ILocoNet _locoNet;

    public LocoNetTurnout(ILocoNet locoNet)
    {
        _locoNet = locoNet ?? throw new ArgumentNullException(nameof(locoNet));
    }

    /// <summary>1-based turnout address (1..2048).</summary>
    public ushort Address { get; set; }

    /// <summary>Last commanded state (true = thrown, false = closed).</summary>
    public bool Thrown { get; private set; }

    /// <summary>
    /// Command the turnout. Sends a single <c>OPC_SW_REQ</c> with the output bit set
    /// (matches the original library's behaviour).
    /// </summary>
    public void SetState(bool thrown)
    {
        Thrown = thrown;
        Switches.RequestSwitch(_locoNet, Address, output: true, thrown: thrown);
    }
}
