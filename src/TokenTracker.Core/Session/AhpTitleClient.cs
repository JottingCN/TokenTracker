using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenTracker.Core.Session;

internal sealed record AhpTitle(
    string Title,
    string? WorkspacePath);

public sealed record AhpConnectionStatus(
    string State,
    bool Connected,
    int Sessions);

internal sealed class AhpTitleClient
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly string[] _userDataDirectories;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, AhpTitle> _titles =
        new(StringComparer.OrdinalIgnoreCase);
    private string _state;
    private int _sessionCount;

    private WebSocket? _socket;
    private SemaphoreSlim? _sendLock;
    private Dictionary<int, TaskCompletionSource<JsonElement?>>? _pending;
    private int _nextId;

    public AhpTitleClient(
        IEnumerable<string>? userDataDirectories,
        bool resolveDefault)
    {
        _userDataDirectories = (userDataDirectories ??
                (resolveDefault ? DefaultUserDataDirectories() : Array.Empty<string>()))
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _state = _userDataDirectories.Length == 0 ? "disabled" : "searching";

        if (_userDataDirectories.Length > 0)
        {
            _ = Task.Run(() => RunAsync(_cancellation.Token));
        }
    }

    public AhpConnectionStatus GetStatus()
    {
        lock (_gate)
        {
            return new AhpConnectionStatus(
                _state,
                _state == "connected",
                _sessionCount);
        }
    }

    public event Action? StatusChanged;

    private void SetState(string state)
    {
        var changed = false;
        lock (_gate)
        {
            if (_state != state)
            {
                _state = state;
                changed = true;
            }
        }

        if (changed)
        {
            StatusChanged?.Invoke();
        }
    }

    public AhpTitle? Get(string sessionId)
    {
        lock (_gate)
        {
            return _titles.TryGetValue(sessionId, out var title) ? title : null;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var endpoint = DiscoverEndpoint();
                if (endpoint is not null)
                {
                    SetState("connecting");
                    await RunConnectionAsync(endpoint, cancellationToken);
                }
                else
                {
                    SetState("searching");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                SetState("searching");
            }

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunConnectionAsync(
        AhpEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        using var stream = await ConnectStreamAsync(endpoint, cancellationToken);
        var (socket, sendLock, pending) = await UpgradeAsync(stream, endpoint, cancellationToken);
        _socket = socket;
        _sendLock = sendLock;
        _pending = pending;
        using var connectionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readTask = ReadLoopAsync(
            socket,
            pending,
            connectionCancellation.Token);
        try
        {
            await InitializeAsync(connectionCancellation.Token);
            lock (_gate)
            {
                _titles.Clear();
            }

            await ListSessionsAsync(connectionCancellation.Token);
            SetState("connected");
            _ = RefreshLoopAsync(connectionCancellation.Token);
            await readTask;
        }
        finally
        {
            connectionCancellation.Cancel();
            _socket = null;
            _sendLock = null;
            _pending = null;
            socket.Dispose();
            var changed = false;
            lock (_gate)
            {
                changed = _titles.Count > 0 ||
                    _state != "searching" ||
                    _sessionCount != 0;
                _titles.Clear();
                _state = "searching";
                _sessionCount = 0;
            }

            if (changed)
            {
                StatusChanged?.Invoke();
            }

            try
            {
                await readTask;
            }
            catch
            {
            }
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
                await ListSessionsAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
            }
        }
    }

    private static async Task<Stream> ConnectStreamAsync(
        AhpEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(endpoint.Path))
        {
            var path = endpoint.Path;
            if (OperatingSystem.IsWindows() &&
                path.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
            {
                var pipe = new NamedPipeClientStream(
                    ".",
                    path[@"\\.\pipe\".Length..],
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                pipe.Connect(5000);
                return pipe;
            }

            var socket = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified);
            await socket.ConnectAsync(
                new UnixDomainSocketEndPoint(path),
                cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }

        var client = new TcpClient();
        await client.ConnectAsync(
            endpoint.Host ?? "127.0.0.1",
            endpoint.Port,
            cancellationToken);
        return new NetworkStream(client.Client, ownsSocket: true);
    }

    private static async Task<(
        WebSocket Socket,
        SemaphoreSlim SendLock,
        Dictionary<int, TaskCompletionSource<JsonElement?>> Pending)> UpgradeAsync(
        Stream stream,
        AhpEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request =
            $"GET /?tkn={Uri.EscapeDataString(endpoint.Token)} HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {key}\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n";
        var requestBytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(requestBytes, cancellationToken);

        var header = await ReadHeadersAsync(stream, cancellationToken);
        if (!header.Contains(" 101 ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AHP WebSocket upgrade failed");
        }

        var socket = WebSocket.CreateFromStream(
            stream,
            isServer: false,
            subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(20));
        return (
            socket,
            new SemaphoreSlim(1, 1),
            new Dictionary<int, TaskCompletionSource<JsonElement?>>());
    }

    private static async Task<string> ReadHeadersAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var builder = new StringBuilder();
        while (builder.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return builder.ToString();
            }
        }

        throw new InvalidOperationException("AHP handshake closed");
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var parameters = new JsonObject
        {
            ["channel"] = "ahp-root://",
            ["protocolVersions"] = new JsonArray("1.1.0", "1.0.0", "0.9.0"),
            ["clientId"] = "token-tracker",
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "Token Tracker",
                ["version"] = "1.0",
            },
            ["initialSubscriptions"] = new JsonArray("ahp-root://"),
            ["_meta"] = new JsonObject
            {
                ["vscode.ahpSessionUris"] = true,
                ["vscode.ahpChatUris"] = true,
            },
        };

        var result = await SendRequestAsync("initialize", parameters, cancellationToken);
        if (result is null)
        {
            throw new InvalidOperationException("AHP initialize failed");
        }
    }

    private async Task ListSessionsAsync(CancellationToken cancellationToken)
    {
        string? cursor = null;
        var pages = 0;
        var total = 0;
        do
        {
            if (++pages > 1_000)
            {
                return;
            }

            var parameters = new JsonObject
            {
                ["channel"] = "ahp-root://",
                ["limit"] = 200,
            };
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                parameters["cursor"] = cursor;
            }

            var result = await SendRequestAsync(
                "listSessions",
                parameters,
                cancellationToken);
            if (result is not { } document ||
                !document.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            total += items.GetArrayLength();
            foreach (var item in items.EnumerateArray())
            {
                var resource = GetString(item, "resource");
                var title = GetString(item, "title");
                if (string.IsNullOrWhiteSpace(resource) ||
                    string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                UpdateTitle(
                    resource,
                    title,
                    GetWorkingDirectory(item, "workingDirectories"));

                if (item.TryGetProperty("chats", out var chats) &&
                    chats.ValueKind == JsonValueKind.Array)
                {
                    foreach (var chat in chats.EnumerateArray())
                    {
                        var chatResource = GetString(chat, "resource");
                        if (string.IsNullOrWhiteSpace(chatResource))
                        {
                            continue;
                        }

                        var chatTitle = GetString(chat, "title");
                        UpdateTitle(
                            chatResource,
                            string.IsNullOrWhiteSpace(chatTitle) ? title : chatTitle,
                            GetWorkingDirectory(item, "workingDirectories"));
                    }
                }
            }

            cursor = GetString(document, "nextCursor");
        } while (!string.IsNullOrWhiteSpace(cursor));

        lock (_gate)
        {
            if (_sessionCount == total)
            {
                return;
            }

            _sessionCount = total;
        }

        StatusChanged?.Invoke();
    }

    private async Task<JsonElement?> SendRequestAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var socket = _socket;
        var sendLock = _sendLock;
        var pending = _pending;
        if (socket is null || sendLock is null || pending is null)
        {
            return null;
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pending)
        {
            pending[id] = completion;
        }

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };

        await sendLock.WaitAsync(cancellationToken);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
            await socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }

        try
        {
            return await completion.Task.WaitAsync(RequestTimeout, cancellationToken);
        }
        catch
        {
            lock (pending)
            {
                pending.Remove(id);
            }

            throw;
        }
    }

    private async Task ReadLoopAsync(
        WebSocket socket,
        Dictionary<int, TaskCompletionSource<JsonElement?>> pending,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(message.ToArray());
            message.SetLength(0);
            try
            {
                HandleMessage(text, pending);
            }
            catch (JsonException)
            {
                // A malformed notification must not tear down the title stream.
            }
        }
    }

    private void HandleMessage(
        string text,
        Dictionary<int, TaskCompletionSource<JsonElement?>> pending)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        if (root.TryGetProperty("id", out var idElement) &&
            idElement.TryGetInt64(out var id))
        {
            TaskCompletionSource<JsonElement?>? completion;
            lock (pending)
            {
                pending.Remove((int)id, out completion);
            }

            if (completion is null)
            {
                return;
            }

            if (root.TryGetProperty("result", out var result))
            {
                completion.TrySetResult(result.Clone());
            }
            else
            {
                completion.TrySetException(
                    new InvalidOperationException("AHP request failed"));
            }

            return;
        }

        if (!root.TryGetProperty("method", out var methodElement) ||
            methodElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var method = methodElement.GetString();
        if (method == "action")
        {
            HandleAction(root);
        }
        else if (method == "root/sessionSummaryChanged")
        {
            HandleSummaryChanged(root);
        }
    }

    private void HandleAction(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("channel", out var channelElement) ||
            channelElement.ValueKind != JsonValueKind.String ||
            !parameters.TryGetProperty("action", out var action) ||
            !action.TryGetProperty("type", out var typeElement) ||
            typeElement.GetString() != "session/titleChanged")
        {
            return;
        }

        var channel = channelElement.GetString();
        var title = GetString(action, "title");
        if (!string.IsNullOrWhiteSpace(channel) &&
            !string.IsNullOrWhiteSpace(title))
        {
            UpdateTitle(channel, title, null);
        }
    }

    private void HandleSummaryChanged(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("session", out var sessionElement) ||
            sessionElement.ValueKind != JsonValueKind.String ||
            !parameters.TryGetProperty("changes", out var changes))
        {
            return;
        }

        var session = sessionElement.GetString();
        var title = GetString(changes, "title");
        if (!string.IsNullOrWhiteSpace(session) &&
            !string.IsNullOrWhiteSpace(title))
        {
            UpdateTitle(session, title, null);
        }
    }

    private void UpdateTitle(
        string resource,
        string title,
        string? workspacePath)
    {
        lock (_gate)
        {
            if (_titles.TryGetValue(resource, out var existing))
            {
                workspacePath ??= existing.WorkspacePath;
            }

            var value = new AhpTitle(title.Trim(), workspacePath);
            _titles[resource] = value;
            var canonical = CanonicalSessionId(resource);
            if (canonical is not null)
            {
                _titles[canonical] = value;
            }
        }
    }

    private AhpEndpoint? DiscoverEndpoint()
    {
        AhpEndpoint? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (var userDataDirectory in _userDataDirectories)
        {
            var entriesDirectory = Path.Combine(
                userDataDirectory,
                "agent-host",
                "local-endpoint",
                "entries");
            if (!Directory.Exists(entriesDirectory))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(entriesDirectory, "*.json");
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    var modified = File.GetLastWriteTimeUtc(file);
                    if (modified <= bestTime)
                    {
                        continue;
                    }

                    var endpoint = ParseEndpoint(File.ReadAllText(file), checkProcess: true);
                    if (endpoint is not null)
                    {
                        best = endpoint;
                        bestTime = modified;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or
                    UnauthorizedAccessException or
                    JsonException)
                {
                }
            }

            var legacy = Path.Combine(
                userDataDirectory,
                "agent-host",
                "local-endpoint",
                "metadata.json");
            if (File.Exists(legacy))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(legacy));
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        var modified = File.GetLastWriteTimeUtc(legacy);
                        if (modified > bestTime)
                        {
                            foreach (var entry in document.RootElement.EnumerateArray())
                            {
                                var endpoint = ParseEndpoint(
                                    entry.GetRawText(),
                                    checkProcess: true);
                                if (endpoint is not null)
                                {
                                    best = endpoint;
                                    bestTime = modified;
                                }
                            }
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or
                    UnauthorizedAccessException or
                    JsonException)
                {
                }
            }
        }

        return best;
    }

    private static AhpEndpoint? ParseEndpoint(string json, bool checkProcess)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var version) ||
            !version.TryGetInt32(out var schema) ||
            schema != 2)
        {
            return null;
        }

        var type = GetString(root, "type");
        if (!string.Equals(type, "editor", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "standalone", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var pid = GetInt32(root, "pid");
        if (checkProcess && pid > 0 && !IsProcessAlive(pid))
        {
            return null;
        }

        var token = GetString(root, "connectionToken");
        if (string.IsNullOrWhiteSpace(token) ||
            !root.TryGetProperty("endpoint", out var endpoint) ||
            endpoint.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var endpointType = GetString(endpoint, "type");
        var path = GetString(endpoint, "path");
        var host = GetString(endpoint, "host");
        var port = GetInt32(endpoint, "port");
        if (endpointType == "socket" && !string.IsNullOrWhiteSpace(path))
        {
            return new AhpEndpoint(token, path, null, 0);
        }

        if (endpointType == "tcp" && port > 0)
        {
            return new AhpEndpoint(token, null, host ?? "127.0.0.1", port);
        }

        return null;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            // A permission or platform failure is not proof that the PID is dead.
            return true;
        }
    }

    private static string? CanonicalSessionId(string resource)
    {
        var value = resource;
        var query = value.IndexOf('?');
        if (query >= 0)
        {
            value = value[..query];
        }

        var hash = value.IndexOf('#');
        if (hash >= 0)
        {
            value = value[..hash];
        }

        var slash = value.LastIndexOf('/');
        if (slash >= 0)
        {
            value = value[(slash + 1)..];
        }
        else
        {
            var colon = value.IndexOf(':');
            if (colon >= 0)
            {
                value = value[(colon + 1)..];
            }
        }

        try
        {
            value = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? GetWorkingDirectory(
        JsonElement item,
        string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var directories) ||
            directories.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var directory in directories.EnumerateArray())
        {
            if (directory.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var path = ToLocalPath(directory.GetString());
            if (path is not null)
            {
                return path;
            }
        }

        return null;
    }

    private static string? ToLocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var path = uri.LocalPath;
            if (path.Length >= 3 &&
                path[0] == '/' &&
                char.IsLetter(path[1]) &&
                path[2] == ':')
            {
                return path[1..].Replace('/', '\\');
            }

            return path;
        }

        return Path.IsPathRooted(value) ? value : null;
    }

    private static string? GetString(JsonElement parent, string property)
    {
        return parent.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int GetInt32(JsonElement parent, string property)
    {
        return parent.TryGetProperty(property, out var value) &&
               value.TryGetInt32(out var result)
            ? result
            : 0;
    }

    private static IEnumerable<string> DefaultUserDataDirectories()
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var product in new[] { "Code", "Code - Insiders" })
            {
                yield return Path.Combine(
                    home,
                    "Library",
                    "Application Support",
                    product);
            }

            yield break;
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
            {
                yield break;
            }

            foreach (var product in new[] { "Code", "Code - Insiders" })
            {
                yield return Path.Combine(appData, product);
            }

            yield break;
        }

        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ??
            Path.Combine(homeDirectory, ".config");
        foreach (var product in new[] { "Code", "Code - Insiders" })
        {
            yield return Path.Combine(config, product);
        }
    }

    private sealed record AhpEndpoint(
        string Token,
        string? Path,
        string? Host,
        int Port);
}
