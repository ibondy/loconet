using System;

namespace LocoNet.Net;

/// <summary>
/// Minimal abstraction over the LocoNet I/O layer used by higher-level helpers
/// (throttle, turnout, fast clock, etc.) so they can be tested without a real connection.
/// </summary>
public interface ILocoNet
{
    /// <summary>Raised when a complete LocoNet message has been received.</summary>
    event EventHandler<LnMessageEventArgs>? MessageReceived;

    /// <summary>Enqueue a message for transmission. Returns immediately.</summary>
    void Send(LnMsg message);

    /// <summary>Convenience: build and send a 4-byte (opcode + d1 + d2 + checksum) message.</summary>
    void Send(OpCode opCode, byte d1, byte d2) => Send(LnMsg.Make(opCode, d1, d2));
}
