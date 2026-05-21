using System;

namespace LocoNet.Net;

/// <summary>
/// Result of an LNCV read/write callback.
/// </summary>
/// <remarks>
/// Negative values suppress sending any reply. Zero is "OK" (a response message is sent).
/// Positive values trigger a long-ACK reply containing that value as the error code.
/// Matches the original LocoNet2 signed-byte return convention.
/// </remarks>
public enum LncvResult : sbyte
{
    /// <summary>Do not send any reply.</summary>
    NoReply = -1,
    /// <summary>OK — send a normal response.</summary>
    Ok = 0,
    /// <summary>Long-ACK with code 1 — unsupported CV.</summary>
    Unsupported = 1,
    /// <summary>Long-ACK with code 2 — CV is read-only.</summary>
    ReadOnly = 2,
    /// <summary>Long-ACK with code 3 — value out of range.</summary>
    OutOfRange = 3,
    /// <summary>Long-ACK with code 127 — generic "OK" form.</summary>
    LackOk = 127,
}

/// <summary>
/// Low-level LNCV (Uhlenbrock Configuration Variable) read/write request handler.
/// Implements the request side of LNCV by parsing CFG_READ / CFG_WRITE messages and invoking
/// user callbacks.
/// </summary>
/// <remarks>Managed port of the original LocoNet2 LNCV access helper.</remarks>
public sealed class LocoNetCVAccess : IDisposable
{
    private readonly ILocoNet _locoNet;

    /// <summary>
    /// Read callback. Receives <c>(deviceClass, lncvNumber)</c> and returns the value to send
    /// plus an <see cref="LncvResult"/>.
    /// </summary>
    public Func<ushort, ushort, (LncvResult Result, ushort Value)>? CvRead { get; set; }

    /// <summary>
    /// Write callback. Receives <c>(deviceClass, lncvNumber, value)</c> and returns the result.
    /// </summary>
    public Func<ushort, ushort, ushort, LncvResult>? CvWrite { get; set; }

    public LocoNetCVAccess(ILocoNet locoNet)
    {
        _locoNet = locoNet ?? throw new ArgumentNullException(nameof(locoNet));
        _locoNet.MessageReceived += OnMessageReceived;
    }

    private void OnMessageReceived(object? sender, LnMessageEventArgs e)
    {
        var msg = e.Message;
        if (!LncvMessage.IsLncv(msg))
        {
            return;
        }

        byte reqId = LncvMessage.ReqId(msg);
        byte src = LncvMessage.Source(msg);
        ushort deviceClass = LncvMessage.DeviceClass(msg);
        ushort cv = LncvMessage.LncvNumber(msg);

        switch (reqId)
        {
            case LncvMessage.ReqIdCfgRead:
                if (CvRead is null) return;
                var (rResult, rVal) = CvRead(deviceClass, cv);
                if (rResult == LncvResult.Ok)
                {
                    _locoNet.Send(LncvMessage.MakeResponse(src, deviceClass, cv, rVal, flags: 0));
                }
                else if ((sbyte)rResult > 0)
                {
                    _locoNet.Send(OpCode.LongAck, (byte)((byte)msg.OpCode & 0x7F), (byte)rResult);
                }
                break;

            case LncvMessage.ReqIdCfgWrite:
                if (CvWrite is null) return;
                ushort value = LncvMessage.LncvValue(msg);
                var wResult = CvWrite(deviceClass, cv, value);
                if ((sbyte)wResult >= 0)
                {
                    _locoNet.Send(OpCode.LongAck, (byte)((byte)msg.OpCode & 0x7F), (byte)wResult);
                }
                break;
        }
    }

    public void Dispose() => _locoNet.MessageReceived -= OnMessageReceived;
}
