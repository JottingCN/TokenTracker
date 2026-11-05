using System.Net.WebSockets;
using System.Text.Json;

namespace TokenTracker.Server;

public static class WsEndpoint
{
    private const int MaxMessageBytes = 256 * 1024;
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

                // A client that never sets EndOfMessage (or sends a huge frame)
                // would otherwise grow this buffer without bound. Subscribe
                // messages are tiny, so anything past the cap is not a real
                // subscription: drop the connection instead of hoarding memory.
                if (pending.Length + result.Count > MaxMessageBytes)
                {
                    Console.Error.WriteLine(
                        $"ws message exceeded {MaxMessageBytes} bytes; closing");
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
                        subscription.Update(message);
                        connection.SetVscode(subscription.IsVscode);
                        // Wake the send loop on every subscribe, including the
                        // first one: otherwise a subscribe arriving after the
                        // 1s startup poll would wait for FallbackInterval.
                        connection.Signal();
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
                var state = subscription.Snapshot();
                byte[] payload;
                try
                {
                    var customRange = state.FromMs is > 0 && state.ToMs > state.FromMs;
                    payload = state.View switch
                    {
                        "model" when !string.IsNullOrWhiteSpace(state.Id) =>
                            JsonSerializer.SerializeToUtf8Bytes(
                                new ModelMessage(
                                    "snapshot",
                                    customRange
                                        ? service.GetModel(
                                            state.Id!,
                                            state.FromMs!.Value,
                                            state.ToMs!.Value,
                                            state.Source)
                                        : service.GetModel(
                                            state.Id!,
                                            state.Range,
                                            state.Source)),
                                AppJsonContext.Default.ModelMessage),
                        "session" when !string.IsNullOrWhiteSpace(state.Id) =>
                            JsonSerializer.SerializeToUtf8Bytes(
                                new SessionMessage(
                                    "snapshot",
                                    customRange
                                        ? service.GetSession(
                                            state.Id!,
                                            state.Source,
                                            state.FromMs!.Value,
                                            state.ToMs!.Value,
                                            state.IncludeCalls)
                                        : service.GetSession(
                                            state.Id!,
                                            state.Range,
                                            state.Source,
                                            state.IncludeCalls)),
                                AppJsonContext.Default.SessionMessage),
                        "calls" => JsonSerializer.SerializeToUtf8Bytes(
                            new CallsMessage(
                                "snapshot",
                                customRange
                                    ? service.GetCalls(
                                        state.FromMs!.Value,
                                        state.ToMs!.Value,
                                        state.Utility,
                                        state.Model,
                                        state.Source,
                                        state.Query,
                                        state.Kind,
                                        state.Priced,
                                        state.Period,
                                        state.HasError,
                                        state.Agent,
                                        state.Offset ?? 0,
                                        state.Limit ?? 50)
                                    : service.GetCalls(
                                        state.Range,
                                        state.Utility,
                                        state.Model,
                                        state.Source,
                                        state.Query,
                                        state.Kind,
                                        state.Priced,
                                        state.Period,
                                        state.HasError,
                                        state.Agent,
                                        state.Offset ?? 0,
                                        state.Limit ?? 50)),
                            AppJsonContext.Default.CallsMessage),
                        _ => JsonSerializer.SerializeToUtf8Bytes(
                            new DashboardMessage(
                                "snapshot",
                                customRange
                                    ? service.GetDashboard(
                                        state.FromMs!.Value,
                                        state.ToMs!.Value,
                                        state.Utility,
                                        state.Source)
                                    : service.GetDashboard(
                                        state.Range,
                                        state.Utility,
                                        state.Source)),
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
        private string? _model;
        private string? _source;
        private string? _query;
        private string? _kind;
        private bool? _priced;
        private string? _period;
        private bool? _hasError;
        private string? _agent;
        private int? _offset;
        private int? _limit;
        private bool _includeCalls = true;
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
                _model = message.Model;
                _source = message.Source;
                _query = message.Query;
                _kind = message.Kind;
                _priced = message.Priced;
                _period = message.Period;
                _hasError = message.HasError;
                _agent = message.Agent;
                _offset = message.Offset;
                _limit = message.Limit;
                _includeCalls = message.IncludeCalls ?? true;
                _isVscode = string.Equals(
                    message.Client,
                    "vscode",
                    StringComparison.OrdinalIgnoreCase);
                _hasValue = true;
            }
        }

        public SubscriptionState Snapshot()
        {
            lock (_gate)
            {
                return new SubscriptionState(
                    _view,
                    _id,
                    _range,
                    _utility,
                    _fromMs,
                    _toMs,
                    _model,
                    _source,
                    _query,
                    _kind,
                    _priced,
                    _period,
                    _hasError,
                    _agent,
                    _offset,
                    _limit,
                    _includeCalls);
            }
        }

        public sealed record SubscriptionState(
            string View,
            string? Id,
            int Range,
            bool Utility,
            long? FromMs,
            long? ToMs,
            string? Model,
            string? Source,
            string? Query,
            string? Kind,
            bool? Priced,
            string? Period,
            bool? HasError,
            string? Agent,
            int? Offset,
            int? Limit,
            bool IncludeCalls);
    }
}
