namespace LocoNet.Net;

/// <summary>Receive-side packet statistics.</summary>
public sealed class LnRxStats
{
    public ulong RxPackets { get; internal set; }
    public ulong RxErrors  { get; internal set; }

    public void Reset()
    {
        RxPackets = 0;
        RxErrors = 0;
    }
}

/// <summary>Transmit-side packet statistics.</summary>
public sealed class LnTxStats
{
    public ulong TxPackets { get; internal set; }
    public ulong TxErrors  { get; internal set; }
    public ulong Collisions { get; internal set; }

    public void Reset()
    {
        TxPackets = 0;
        TxErrors = 0;
        Collisions = 0;
    }
}
