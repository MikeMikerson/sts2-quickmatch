using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using QuickMatch.Transport;

namespace QuickMatchRelay.Relay;

/// <summary>
/// QuickMatch's relay connections over <see cref="ClientWebSocket"/>: one WebSocket per connection to
/// <c>&lt;relay&gt;/v1/relay/&lt;region&gt;</c>, never through a proxy (an HTTPS proxy could hold
/// the match up) and without following redirects (ClientWebSocket does not). The address comes from
/// <see cref="RelaySettings"/>; tests pass a local <c>ws://</c> one.
/// </summary>
internal sealed class WebSocketRelayTransport(Uri relay) : IRelayTransport
{
    public const int ConnectTimeoutMs = 10000;
    /// <summary>After the owner lets go, queued messages may take this long to leave before the socket is cut.</summary>
    public const int CloseTimeoutMs = 2000;
    /// <summary>
    /// Messages waiting to leave, at most: about five seconds of the relay's per-connection rate. A
    /// connection that falls this far behind (a stalled network) is closed rather than queueing stale
    /// messages without end; QuickMatch then carries the match over lobby chat and connects again.
    /// </summary>
    public const int MaxQueuedMessages = 512;
    /// <summary>The relay's data frame: 9 header bytes and up to 64 KB, with room for its other frames.</summary>
    private const int MaxIncomingBytes = 9 + 64 * 1024 + 1024;

    public IRelaySocket Open(string region, IRelayListener listener)
    {
        var connection = new Connection(listener);
        if (!IsRegion(region))
        {
            connection.Fail($"refused region \"{region}\"");
        }
        else
        {
            connection.Start(new Uri(relay, $"/v1/relay/{region}"));
        }
        return connection;
    }

    private static bool IsRegion(string region)
    {
        if (region.Length is 0 or > 32)
        {
            return false;
        }
        foreach (char c in region)
        {
            if (c is < 'a' or > 'z')
            {
                return false;
            }
        }
        return true;
    }

    private sealed class Connection(IRelayListener listener) : IRelaySocket
    {
        private readonly ClientWebSocket _socket = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<byte[]> _outbox = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(MaxQueuedMessages) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        /// <summary>1 once the listener has heard Closed or the owner disposed the socket: nothing more is reported.</summary>
        private int _ended;

        public void Start(Uri address)
        {
            _socket.Options.Proxy = null;
            _ = Task.Run(() => RunAsync(address));
        }

        public void Fail(string reason) => _ = Task.Run(() => End(reason));

        public void Send(byte[] message)
        {
            // TryWrite also fails once the connection is ending; only a full queue is an overflow.
            if (Volatile.Read(ref _ended) == 0 && !_outbox.Writer.TryWrite(message) && _outbox.Reader.Count >= MaxQueuedMessages)
            {
                _outbox.Writer.TryComplete();
                _stop.Cancel();
                End($"more than {MaxQueuedMessages} messages waiting to be sent to the relay");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                // What is queued (a goodbye) still goes; the send loop then closes the socket.
                _outbox.Writer.TryComplete();
                _stop.CancelAfter(CloseTimeoutMs);
            }
        }

        private async Task RunAsync(Uri address)
        {
            string reason;
            try
            {
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    connect.CancelAfter(ConnectTimeoutMs);
                    try
                    {
                        await _socket.ConnectAsync(address, connect.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                    {
                        throw new TimeoutException($"no connection after {ConnectTimeoutMs} ms");
                    }
                }
                if (Volatile.Read(ref _ended) == 0)
                {
                    listener.Opened();
                }
                Task sending = SendLoopAsync();
                reason = await ReceiveLoopAsync().ConfigureAwait(false);
                _outbox.Writer.TryComplete();
                _stop.Cancel();
                await sending.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                reason = e switch
                {
                    WebSocketException or TimeoutException => e.Message,
                    OperationCanceledException => "connection ended",
                    _ => $"{e.GetType().Name}: {e.Message}",
                };
            }
            finally
            {
                _stop.Cancel();
                _socket.Dispose();
            }
            End(reason);
        }

        private async Task<string> ReceiveLoopAsync()
        {
            var buffer = new byte[MaxIncomingBytes];
            while (true)
            {
                int filled = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (filled == buffer.Length)
                    {
                        return "message from the relay too large";
                    }
                    result = await _socket.ReceiveAsync(buffer.AsMemory(filled), _stop.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return $"relay closed the connection ({(int?)_socket.CloseStatus} {_socket.CloseStatusDescription})";
                    }
                    filled += result.Count;
                }
                while (!result.EndOfMessage);
                // Text messages are the relay's answers to a text "ping"; QuickMatch only speaks binary.
                if (result.MessageType == WebSocketMessageType.Binary && Volatile.Read(ref _ended) == 0)
                {
                    listener.Received(buffer.AsSpan(0, filled).ToArray());
                }
            }
        }

        private async Task SendLoopAsync()
        {
            try
            {
                await foreach (byte[] message in _outbox.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                {
                    await _socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, _stop.Token).ConfigureAwait(false);
                }
                // The owner let go and everything queued has left: close politely, then stop receiving.
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", _stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested || _socket.State != WebSocketState.Open)
            {
                // The connection is ending anyway; the receive loop reports why.
            }
            finally
            {
                _stop.Cancel();
            }
        }

        private void End(string reason)
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                listener.Closed(reason);
            }
        }
    }
}
