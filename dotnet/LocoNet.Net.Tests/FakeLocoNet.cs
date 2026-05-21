using System;
using System.Collections.Generic;

namespace LocoNet.Net.Tests;

/// <summary>Test double that captures sent messages and lets tests inject incoming ones.</summary>
internal sealed class FakeLocoNet : ILocoNet
{
    public List<LnMsg> Sent { get; } = new();

    public event EventHandler<LnMessageEventArgs>? MessageReceived;

    public void Send(LnMsg message) => Sent.Add(message);

    public void Receive(LnMsg message) => MessageReceived?.Invoke(this, new LnMessageEventArgs(message));
}
