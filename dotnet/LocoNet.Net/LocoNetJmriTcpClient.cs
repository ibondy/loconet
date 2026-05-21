using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace LocoNet.Net;

/// <summary>
/// A LocoNet client that speaks JMRI's <c>LocoNetOverTcp</c> ASCII protocol
/// (<c>SEND xx xx xx</c> / <c>RECEIVE xx xx xx</c> lines, CRLF-terminated, ASCII encoding).
/// </summary>
/// <remarks>
/// <para>
/// The client sends outbound frames as <c>SEND</c> lines and surfaces both <c>SEND</c> and
/// <c>RECEIVE</c> lines from the server via <see cref="MessageReceived"/>. JMRI servers
/// typically emit <c>RECEIVE</c> for bus traffic and may also echo <c>SEND</c> from peer
/// clients.
/// </para>
/// <para>This type is single-use: dispose and recreate to reconnect.</para>
/// </remarks>
public sealed class LocoNetJmriTcpClient : ILocoNet, IAsyncDisposable, IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly Channel<LnMsg> _txChannel = Channel.CreateUnbounded<LnMsg>(
        new UnboundedChannelOptions { SingleReader = true });

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private Task? _writeLoop;

    public LocoNetJmriTcpClient(string host, int port)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _port = port;
    }

    /// <inheritdoc cref="LocoNetTcpClient.MessageReceived"/>
    public event EventHandler<LnMessageEventArgs>? MessageReceived;

    /// <inheritdoc cref="LocoNetTcpClient.Disconnected"/>
    public event EventHandler<Exception?>? Disconnected;

    /// <summary>Number of malformed lines received (bad keyword, hex, length or checksum).</summary>
    public long RxLineErrors { get; private set; }

    /// <summary>Number of frames successfully received.</summary>
    public long RxFrames { get; private set; }

    /// <summary>Number of frames successfully sent.</summary>
    public long TxFrames { get; private set; }

    /// <summary>Whether the underlying TCP connection is currently open.</summary>
    public bool IsConnected => _client?.Connected ?? false;

    /// <summary>Open the TCP connection and start the background read/write loops.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not null) throw new InvalidOperationException("Already connected.");

        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
        _stream = _client.GetStream();
        _cts = new CancellationTokenSource();

        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        _writeLoop = Task.Run(() => WriteLoopAsync(_cts.Token));
    }

    /// <inheritdoc />
    public void Send(LnMsg message)
    {
        if (!_txChannel.Writer.TryWrite(message))
        {
            throw new InvalidOperationException("Transmit channel is closed; the connection is no longer active.");
        }
    }

    public ValueTask SendAsync(LnMsg message, CancellationToken cancellationToken = default)
        => _txChannel.Writer.WriteAsync(message, cancellationToken);

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            using var reader = new StreamReader(_stream!, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;

                if (JmriLineParser.TryParse(line, out _, out LnMsg msg))
                {
                    RxFrames++;
                    MessageReceived?.Invoke(this, new LnMessageEventArgs(msg));
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    RxLineErrors++;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _txChannel.Writer.TryComplete();
            Disconnected?.Invoke(this, failure);
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            using var writer = new StreamWriter(_stream!, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = false,
            };
            await foreach (var msg in _txChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await writer.WriteLineAsync(JmriLineParser.FormatSend(msg).AsMemory(), ct).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
                TxFrames++;
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Surfaced via the read loop's Disconnected event when the socket closes.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _txChannel.Writer.TryComplete();
        try
        {
            if (_readLoop is not null) await _readLoop.ConfigureAwait(false);
            if (_writeLoop is not null) await _writeLoop.ConfigureAwait(false);
        }
        catch { }
        _stream?.Dispose();
        _client?.Dispose();
        _cts?.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
