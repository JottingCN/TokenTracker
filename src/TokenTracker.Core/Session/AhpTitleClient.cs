using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AgentHostProtocol;

namespace TokenTracker.Core.Session;

internal sealed record AhpTitle(
    string Title,
    string? WorkspacePath);

public sealed record AhpSessionInfo(
    string Resource,
    string Provider,
    string Title,
    string Status,
    string? Activity,
    string? WorkspacePath,
    string ModifiedAt,
    int Chats);

public sealed record AhpConnectionStatus(
    string State,
    bool Connected,
    int Sessions,
    IReadOnlyList<AhpSessionInfo> Items);

internal sealed class AhpTitleClient
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly string[] _userDataDirectories;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, AhpTitle> _titles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AhpSessionInfo> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private string _state;

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
                _sessions.Count,
                _sessions.Values
                    .OrderByDescending(item => item.ModifiedAt, StringComparer.Ordinal)
                    .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
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
        var socket = await UpgradeAsync(stream, endpoint, cancellationToken);
        await using var client = AhpClient.Connect(
            new AhpStreamTransport(socket),
            new ClientConfig { DefaultRequestTimeout = RequestTimeout });
        using var connectionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var rootSubscription = client.AttachSubscription(
            ProtocolVersion.RootResourceUri);
        var eventsTask = ReadEventsAsync(
            rootSubscription,
            connectionCancellation.Token);
        try
        {
            await client.InitializeAsync(
                "token-tracker",
                ProtocolVersion.Supported,
                new[] { ProtocolVersion.RootResourceUri },
                connectionCancellation.Token);
            lock (_gate)
            {
                _titles.Clear();
                _sessions.Clear();
            }

            await ListSessionsAsync(client, connectionCancellation.Token);
            SetState("connected");
            _ = RefreshLoopAsync(client, connectionCancellation.Token);
            await client.Completion.WaitAsync(cancellationToken);
            await eventsTask;
        }
        finally
        {
            connectionCancellation.Cancel();
            rootSubscription.Dispose();
            lock (_gate)
            {
                _titles.Clear();
                _sessions.Clear();
                _state = "searching";
            }

            StatusChanged?.Invoke();
        }
    }

    private async Task RefreshLoopAsync(
        AhpClient client,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
                await ListSessionsAsync(client, cancellationToken);
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

    private async Task ReadEventsAsync(
        Subscription subscription,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in subscription.Events.ReadAllAsync(cancellationToken))
            {
                switch (item)
                {
                    case SubscriptionEventSessionSummaryChanged changed:
                        UpdateSession(changed.Params.Session, changed.Params.Changes);
                        break;
                    case SubscriptionEventSessionAdded added:
                        UpdateSession(added.Params.Summary);
                        break;
                    case SubscriptionEventSessionRemoved removed:
                        RemoveSession(removed.Params.Session);
                        break;
                    case SubscriptionEventAction action
                        when action.Envelope.Action.Value is SessionTitleChangedAction titleChanged:
                        UpdateTitle(
                            action.Envelope.Channel,
                            titleChanged.Title,
                            null);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async Task ListSessionsAsync(
        AhpClient client,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        var pages = 0;
        do
        {
            if (++pages > 1_000)
            {
                return;
            }

            var result = await client.RequestAsync<ListSessionsParams, ListSessionsResult>(
                "listSessions",
                new ListSessionsParams
                {
                    Channel = ProtocolVersion.RootResourceUri,
                    Limit = 200,
                    Cursor = cursor,
                },
                cancellationToken);
            if (result is null)
            {
                return;
            }

            foreach (var session in result.Items)
            {
                if (string.IsNullOrWhiteSpace(session.Resource) ||
                    string.IsNullOrWhiteSpace(session.Title))
                {
                    continue;
                }

                var workspace = GetWorkingDirectory(session.WorkingDirectories);
                UpdateSession(session);
                UpdateTitle(session.Resource, session.Title, workspace);
                if (session.Chats is not null)
                {
                    foreach (var chat in session.Chats)
                    {
                        if (!string.IsNullOrWhiteSpace(chat.Resource))
                        {
                            UpdateTitle(
                                chat.Resource,
                                string.IsNullOrWhiteSpace(chat.Title)
                                    ? session.Title
                                    : chat.Title,
                                workspace);
                        }
                    }
                }
            }

            cursor = result.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        StatusChanged?.Invoke();
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

    private static async Task<WebSocket> UpgradeAsync(
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

        return WebSocket.CreateFromStream(
            stream,
            isServer: false,
            subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(20));
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

    private static string? GetWorkingDirectory(IEnumerable<string>? directories)
    {
        if (directories is null)
        {
            return null;
        }

        foreach (var directory in directories)
        {
            var path = ToLocalPath(directory);
            if (path is not null)
            {
                return path;
            }
        }

        return null;
    }

    private void RemoveTitle(string resource)
    {
        lock (_gate)
        {
            _titles.Remove(resource);
            var canonical = CanonicalSessionId(resource);
            if (canonical is not null)
            {
                _titles.Remove(canonical);
            }
        }
    }

    private void UpdateTitle(
        string resource,
        string? title,
        string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(resource) ||
            string.IsNullOrWhiteSpace(title))
        {
            return;
        }

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

    private void UpdateSession(SessionSummary summary)
    {
        if (string.IsNullOrWhiteSpace(summary.Resource) ||
            string.IsNullOrWhiteSpace(summary.Title))
        {
            return;
        }

        var workspace = GetWorkingDirectory(summary.WorkingDirectories);
        UpdateTitle(summary.Resource, summary.Title, workspace);
        lock (_gate)
        {
            _sessions[summary.Resource] = new AhpSessionInfo(
                summary.Resource,
                summary.Provider ?? string.Empty,
                summary.Title.Trim(),
                MapSessionStatus(summary.Status),
                summary.Activity,
                workspace,
                summary.ModifiedAt ?? string.Empty,
                summary.Chats?.Count ?? 0);
        }
    }

    private void UpdateSession(string resource, PartialSessionSummary changes)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return;
        }

        lock (_gate)
        {
            _sessions.TryGetValue(resource, out var existing);
            var title = changes.Title ?? existing?.Title;
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            var workspace = changes.WorkingDirectories is null
                ? existing?.WorkspacePath
                : GetWorkingDirectory(changes.WorkingDirectories);
            var info = new AhpSessionInfo(
                resource,
                changes.Provider ?? existing?.Provider ?? string.Empty,
                title.Trim(),
                changes.Status is { } status
                    ? MapSessionStatus(status)
                    : existing?.Status ?? "unknown",
                changes.Activity ?? existing?.Activity,
                workspace,
                changes.ModifiedAt ?? existing?.ModifiedAt ?? string.Empty,
                changes.Chats?.Count ?? existing?.Chats ?? 0);
            _sessions[resource] = info;
            UpdateTitle(resource, info.Title, info.WorkspacePath);
        }
    }

    private void RemoveSession(string resource)
    {
        lock (_gate)
        {
            _sessions.Remove(resource);
        }

        RemoveTitle(resource);
    }

    private static string MapSessionStatus(SessionStatus status)
    {
        if ((status & SessionStatus.InputNeeded) == SessionStatus.InputNeeded)
        {
            return "input_needed";
        }

        if ((status & SessionStatus.InProgress) == SessionStatus.InProgress)
        {
            return "in_progress";
        }

        if ((status & SessionStatus.Error) == SessionStatus.Error)
        {
            return "error";
        }

        return (status & SessionStatus.Idle) == SessionStatus.Idle
            ? "idle"
            : "unknown";
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
