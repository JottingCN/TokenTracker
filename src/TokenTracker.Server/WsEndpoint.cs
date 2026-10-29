using System.Net.WebSockets;
using System.Text.Json;

namespace TokenTracker.Server;

public static class WsEndpoint
{
    public static async Task HandleAsync(
        HttpContext context,
        DashboardService service,
        RealtimeHub hub)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var subscription = new Subscription();
        var connection = hub.Register();

        try
        {
            var receive = ReceiveLoopAsync(socket, subscription, connection, cts.Token);
            var send = SendLoopAsync(socket, service, subscription, connection, cts.Token);
            await Task.WhenAny(receive, send);
        }
        finally
        {
            hub.Unregister(connection);
        }

        cts.Cancel();

        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    null,
                    CancellationToken.None);
            }
        }
        catch
        {
            // Client already gone.
        }
    }

    private static async Task ReceiveLoopAsync(
        WebSocket socket,
        Subscription subscription,
        RealtimeConnection connection,
        CancellationToken token)
    {
        var buffer = new byte[4096];
        using var pending = new MemoryStream();
        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                pending.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                try
                {
                    var message = JsonSerializer.Deserialize(
                        pending.GetBuffer().AsSpan(0, (int)pending.Length),
                        AppJsonContext.Default.SubscribeMessage);
                    if (message is not null)
                    {
                        var wasSubscribed = subscription.HasValue;
                        subscription.Update(message);
                        connection.SetVscode(subscription.IsVscode);
                        if (wasSubscribed)
                        {
                            connection.Signal();
                        }
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed client messages.
                }

                pending.SetLength(0);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private static async Task SendLoopAsync(
        WebSocket socket,
        DashboardService service,
        Subscription subscription,
        RealtimeConnection connection,
        CancellationToken token)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(1);
            while (!subscription.HasValue &&
                   DateTime.UtcNow < deadline &&
                   !token.IsCancellationRequested)
            {
                await Task.Delay(50, token);
            }

            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var (view, id, range, utility, fromMs, toMs) = subscription.Snapshot();
                byte[] payload;
                try
                {
                    var customRange = fromMs is > 0 && toMs > fromMs;
                    payload = view switch
                    {
                        "model" when !string.IsNullOrWhiteSpace(id) =>
                            JsonSerializer.SerializeToUtf8Bytes(
                                new ModelMessage(
                                    "snapshot",
                                    customRange
                                        ? service.GetModel(id!, fromMs!.Value, toMs!.Value)
                                        : service.GetModel(id!, range)),
                                AppJsonContext.Default.ModelMessage),
                        "session" when !string.IsNullOrWhiteSpace(id) =>
                            JsonSerializer.SerializeToUtf8Bytes(
                                new SessionMessage("snapshot", service.GetSession(id!)),
                                AppJsonContext.Default.SessionMessage),
                        _ => JsonSerializer.SerializeToUtf8Bytes(
                            new DashboardMessage(
                                "snapshot",
                                customRange
                                    ? service.GetDashboard(fromMs!.Value, toMs!.Value, utility)
                                    : service.GetDashboard(range, utility)),
                            AppJsonContext.Default.DashboardMessage),
                    };
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"ws snapshot failed: {exception.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    continue;
                }

                await socket.SendAsync(payload, WebSocketMessageType.Text, true, token);
                await connection.WaitForSignalAsync(
                    RealtimeHub.FallbackInterval,
                    token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private sealed class Subscription
    {
        private readonly object _gate = new();
        private int _range = 2;
        private bool _utility;
        private string _view = "dashboard";
        private string? _id;
        private long? _fromMs;
        private long? _toMs;
        private bool _isVscode;
        private bool _hasValue;

        public bool IsVscode
        {
            get
            {
                lock (_gate)
                {
                    return _isVscode;
                }
            }
        }

        public bool HasValue
        {
            get
            {
                lock (_gate)
                {
                    return _hasValue;
                }
            }
        }

        public void Update(SubscribeMessage message)
        {
            lock (_gate)
            {
                _range = message.Range is >= 0 and <= 4 ? message.Range : 2;
                _utility = message.Utility;
                _view = string.IsNullOrWhiteSpace(message.View) ? "dashboard" : message.View!;
                _id = message.Id;
                _fromMs = message.FromMs;
                _toMs = message.ToMs;
                _isVscode = string.Equals(
                    message.Client,
                    "vscode",
                    StringComparison.OrdinalIgnoreCase);
                _hasValue = true;
            }
        }

        public (string View, string? Id, int Range, bool Utility, long? FromMs, long? ToMs) Snapshot()
        {
            lock (_gate)
            {
                return (_view, _id, _range, _utility, _fromMs, _toMs);
            }
        }
    }
}
