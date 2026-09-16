using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace WindowCast.Server.Sessions;

/// <summary>
/// One browser attached to a session over WebSocket. Frames go through a tiny bounded queue that drops
/// the oldest entry, so a slow phone never stalls capture and always gets the freshest picture.
/// </summary>
public sealed class StreamViewer
{
    private readonly WebSocket _ws;
    private readonly Channel<(byte[] data, bool text)> _outgoing = Channel.CreateBounded<(byte[], bool)>(
        new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public string Id { get; } = Guid.NewGuid().ToString("n")[..8];
    public string ClientAddress { get; }
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public long FramesSent { get; private set; }
    public long FramesDropped => _framesEnqueued - FramesSent - _outgoing.Reader.Count;
    private long _framesEnqueued;

    /// <summary>Raised for each text message from the browser (input JSON).</summary>
    public event Action<StreamViewer, string>? TextReceived;

    public StreamViewer(WebSocket ws, string clientAddress)
    {
        _ws = ws;
        ClientAddress = clientAddress;
    }

    public void EnqueueFrame(byte[] jpeg)
    {
        Interlocked.Increment(ref _framesEnqueued);
        _outgoing.Writer.TryWrite((jpeg, false));
    }

    public void EnqueueText(string json) => _outgoing.Writer.TryWrite((Encoding.UTF8.GetBytes(json), true));

    /// <summary>Runs send and receive loops until the socket closes.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var send = SendLoop(cts.Token);
        var receive = ReceiveLoop(cts.Token);
        await Task.WhenAny(send, receive);
        cts.Cancel();
        _outgoing.Writer.TryComplete();
        try { await Task.WhenAll(send, receive); } catch { }
        try
        {
            if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch { }
    }

    private async Task SendLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var (data, text) in _outgoing.Reader.ReadAllAsync(ct))
            {
                if (_ws.State != WebSocketState.Open) break;
                await _ws.SendAsync(data, text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ct);
                if (!text) FramesSent++;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                if (result.MessageType == WebSocketMessageType.Text)
                    TextReceived?.Invoke(this, Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                message.SetLength(0);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }
}
