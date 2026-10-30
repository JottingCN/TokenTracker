using System.Net.WebSockets;
using System.Text;
using Microsoft.AgentHostProtocol;

namespace TokenTracker.Core.Session;

/// <summary>
/// Bridges the SDK's transport seam to a WebSocket created over a local named
/// pipe or Unix socket; the SDK's default ClientWebSocket transport only dials
/// ws:// endpoints.
/// </summary>
internal sealed class AhpStreamTransport : ITransport
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _disposed;

    public AhpStreamTransport(WebSocket socket)
    {
        _socket = socket;
    }

    public async ValueTask SendAsync(
        TransportMessage message,
        CancellationToken cancellationToken = default)
    {
        var bytes = message.Frame == TransportFrame.Text
            ? Encoding.UTF8.GetBytes(message.Text ?? string.Empty)
            : message.Binary.ToArray();
        var type = message.Frame == TransportFrame.Text
            ? WebSocketMessageType.Text
            : WebSocketMessageType.Binary;

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(bytes, type, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask<TransportMessage> ReceiveAsync(
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("AHP transport closed");
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            var bytes = message.ToArray();
            return result.MessageType == WebSocketMessageType.Text
                ? TransportMessage.FromText(Encoding.UTF8.GetString(bytes))
                : TransportMessage.FromBinary(bytes);
        }
    }

    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Token Tracker shutdown",
                    cancellationToken);
            }
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _socket.Dispose();
            _sendLock.Dispose();
        }
    }

    public ValueTask DisposeAsync() =>
        CloseAsync(CancellationToken.None);
}
