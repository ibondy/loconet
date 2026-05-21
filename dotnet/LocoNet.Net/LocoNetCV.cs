using System;

namespace LocoNet.Net;

/// <summary>
/// High-level LNCV configuration helper: handles discovery, programming start/stop, and
/// delegates CV reads/writes to <see cref="LocoNetCVAccess"/>.
/// </summary>
/// <remarks>Port of <c>src/LocoNetCV.cpp</c>.</remarks>
public sealed class LocoNetCV : IDisposable
{
    private readonly ILocoNet _locoNet;
    private readonly LocoNetCVAccess _cvAccess;

    /// <summary>
    /// Discovery callback. Receives no input; returns the module's
    /// <c>(artNr, moduleAddress)</c> if discoverable.
    /// </summary>
    public Func<(LncvResult Result, ushort ArtNr, ushort ModuleAddress)>? OnDiscovery { get; set; }

    /// <summary>Programming-start callback (PRON).</summary>
    public Func<(LncvResult Result, ushort ArtNr, ushort ModuleAddress)>? OnProgrammingStart { get; set; }

    /// <summary>Programming-stop callback (PROFF). Args: (deviceClass, value).</summary>
    public Action<ushort, ushort>? OnProgrammingStop { get; set; }

    /// <inheritdoc cref="LocoNetCVAccess.CvRead"/>
    public Func<ushort, ushort, (LncvResult Result, ushort Value)>? CvRead
    {
        get => _cvAccess.CvRead;
        set => _cvAccess.CvRead = value;
    }

    /// <inheritdoc cref="LocoNetCVAccess.CvWrite"/>
    public Func<ushort, ushort, ushort, LncvResult>? CvWrite
    {
        get => _cvAccess.CvWrite;
        set => _cvAccess.CvWrite = value;
    }

    public LocoNetCV(ILocoNet locoNet)
    {
        _locoNet = locoNet ?? throw new ArgumentNullException(nameof(locoNet));
        _cvAccess = new LocoNetCVAccess(locoNet);
        _locoNet.MessageReceived += OnMessageReceived;
    }

    private void OnMessageReceived(object? sender, LnMessageEventArgs e)
    {
        var msg = e.Message;
        if (!LncvMessage.IsLncv(msg) || msg.OpCode != OpCode.ImmPacket)
        {
            return;
        }
        if (LncvMessage.ReqId(msg) != LncvMessage.ReqIdCfgRequest)
        {
            return;
        }

        ushort deviceClass = LncvMessage.DeviceClass(msg);
        ushort cv = LncvMessage.LncvNumber(msg);
        ushort value = LncvMessage.LncvValue(msg);
        byte flags = LncvMessage.Flags(msg);
        byte src = LncvMessage.Source(msg);

        // Discovery: deviceClass=0xFFFF, cv=0x0000, value=0xFFFF.
        if (deviceClass == 0xFFFF && cv == 0x0000 && value == 0xFFFF)
        {
            if (OnDiscovery is null) return;
            var (result, artNr, modAddr) = OnDiscovery.Invoke();
            if (result == LncvResult.Ok)
            {
                _locoNet.Send(LncvMessage.MakeResponse(src, artNr, 0, modAddr, flags: 0));
            }
            return;
        }

        if (flags == LncvMessage.FlagProgOn)
        {
            if (OnProgrammingStart is null) return;
            var (result, artNr, modAddr) = OnProgrammingStart.Invoke();
            if (result == LncvResult.Ok)
            {
                _locoNet.Send(LncvMessage.MakeResponse(src, artNr, 0, modAddr, flags: LncvMessage.FlagProgOn));
            }
            return;
        }

        if (flags == LncvMessage.FlagProgOff)
        {
            OnProgrammingStop?.Invoke(deviceClass, value);
        }
    }

    public void Dispose()
    {
        _locoNet.MessageReceived -= OnMessageReceived;
        _cvAccess.Dispose();
    }
}
