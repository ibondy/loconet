using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LocoNet.Net;

/// <summary>
/// Result of a <see cref="LongAckAwaiter.SendAndAwaitAckAsync"/> call.
/// </summary>
public readonly record struct LongAckResult(byte ReplyToOpCode, byte AckCode);

/// <summary>
/// Wraps an <see cref="ILocoNet"/> with the ability to send a request and asynchronously
/// await its matching <c>OPC_LONG_ACK</c> reply. Replies are correlated by opcode: when
/// multiple in-flight requests share the same opcode, replies are delivered in FIFO order.
/// </summary>
/// <remarks>
/// LocoNet itself does not carry a request ID — <c>OPC_LONG_ACK</c> only echoes the
/// 7-bit opcode of the message being acknowledged. Callers that overlap requests of the
/// same opcode therefore rely on the bus preserving order, which is normal for real
/// gateways but worth keeping in mind.
/// </remarks>
public sealed class LongAckAwaiter : IDisposable
{
    private readonly ILocoNet _locoNet;
    private readonly object _gate = new();
    private readonly Dictionary<byte, Queue<TaskCompletionSource<byte>>> _pending = new();
    private bool _disposed;

    public LongAckAwaiter(ILocoNet locoNet)
    {
        _locoNet = locoNet ?? throw new ArgumentNullException(nameof(locoNet));
        _locoNet.MessageReceived += OnMessageReceived;
    }

    /// <summary>
    /// Send <paramref name="request"/> and wait for a matching <c>OPC_LONG_ACK</c>.
    /// </summary>
    /// <param name="request">The outbound request.</param>
    /// <param name="timeout">Maximum time to wait. Use <see cref="Timeout.InfiniteTimeSpan"/> to disable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ack code (the third byte of the long-ack frame).</returns>
    /// <exception cref="TimeoutException">No long-ack arrived within <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled the wait.</exception>
    public async Task<byte> SendAndAwaitAckAsync(LnMsg request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte key = (byte)((byte)request.OpCode & OpCodeExtensions.OpCodeMask);
        var tcs = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var queue))
            {
                queue = new Queue<TaskCompletionSource<byte>>();
                _pending.Add(key, queue);
            }
            queue.Enqueue(tcs);
        }

        try
        {
            _locoNet.Send(request);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                cts.CancelAfter(timeout);
            }

            using (cts.Token.Register(static state =>
                {
                    var t = (TaskCompletionSource<byte>)state!;
                    t.TrySetCanceled();
                }, tcs))
            {
                try
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("Timed out waiting for OPC_LONG_ACK.");
                }
            }
        }
        finally
        {
            RemovePending(key, tcs);
        }
    }

    private void RemovePending(byte key, TaskCompletionSource<byte> tcs)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out var queue))
            {
                return;
            }

            if (queue.Count == 0)
            {
                _pending.Remove(key);
                return;
            }

            // Fast path: head of queue.
            if (ReferenceEquals(queue.Peek(), tcs))
            {
                queue.Dequeue();
            }
            else
            {
                // Rare: cancellation/timeout for a non-head waiter.
                var keep = new Queue<TaskCompletionSource<byte>>(queue.Count);
                foreach (var item in queue)
                {
                    if (!ReferenceEquals(item, tcs))
                    {
                        keep.Enqueue(item);
                    }
                }
                _pending[key] = keep;
            }

            if (_pending[key].Count == 0)
            {
                _pending.Remove(key);
            }
        }
    }

    private void OnMessageReceived(object? sender, LnMessageEventArgs e)
    {
        var msg = e.Message;
        if (msg.OpCode != OpCode.LongAck || msg.Length != 4)
        {
            return;
        }
        byte key = (byte)(msg[1] & OpCodeExtensions.OpCodeMask);
        byte code = msg[2];

        TaskCompletionSource<byte>? tcs = null;
        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                tcs = queue.Dequeue();
                if (queue.Count == 0)
                {
                    _pending.Remove(key);
                }
            }
        }
        tcs?.TrySetResult(code);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _locoNet.MessageReceived -= OnMessageReceived;

        lock (_gate)
        {
            foreach (var queue in _pending.Values)
            {
                foreach (var tcs in queue)
                {
                    tcs.TrySetException(new ObjectDisposedException(nameof(LongAckAwaiter)));
                }
            }
            _pending.Clear();
        }
    }
}
