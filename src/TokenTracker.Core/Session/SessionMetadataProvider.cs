namespace TokenTracker.Core.Session;

public sealed record SessionMetadata(
    string SessionId,
    string? Title,
    bool UserNamed,
    string? WorkspacePath = null);

public sealed class SessionMetadataProvider
{
    private readonly string _rootDirectory;
    private readonly AgentHostTitleReader _agentHost;
    private readonly Dictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public SessionMetadataProvider(
        string? rootDirectory = null,
        string? agentHostDatabasePath = null)
    {
        _rootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot",
            "session-state");
        _agentHost = new AgentHostTitleReader(
            agentHostDatabasePath,
            resolveDefault: agentHostDatabasePath is null);
    }

    public SessionMetadata? Get(string? sessionId)
    {
        if (!IsSafeSessionId(sessionId))
        {
            return null;
        }

        var workspace = GetWorkspaceMetadata(sessionId!);
        var agent = _agentHost.Get(sessionId!);
        var workspacePath = workspace?.WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            workspacePath = agent?.WorkspacePath;
        }

        if (agent is { UserNamed: true })
        {
            return new SessionMetadata(
                sessionId!,
                agent.Title,
                true,
                workspacePath);
        }

        if (workspace is { UserNamed: true, Title: not null })
        {
            return workspace with { WorkspacePath = workspacePath };
        }

        if (agent?.Title is not null)
        {
            return new SessionMetadata(
                sessionId!,
                agent.Title,
                workspace?.UserNamed == true,
                workspacePath);
        }

        return workspace is null
            ? workspacePath is null
                ? null
                : new SessionMetadata(sessionId!, null, false, workspacePath)
            : workspace with { WorkspacePath = workspacePath };
    }

    private SessionMetadata? GetWorkspaceMetadata(string sessionId)
    {
        var directory = Path.Combine(_rootDirectory, sessionId);
        var file = Path.Combine(directory, "workspace.yaml");

        if (!File.Exists(file))
        {
            _cache.Remove(sessionId);
            return null;
        }

        DateTime lastWriteUtc;
        try
        {
            lastWriteUtc = File.GetLastWriteTimeUtc(file);
        }
        catch (IOException)
        {
            return _cache.TryGetValue(sessionId, out var cached) ? cached.Metadata : null;
        }

        if (_cache.TryGetValue(sessionId, out var entry) &&
            entry.LastWriteUtc == lastWriteUtc)
        {
            return entry.Metadata;
        }

        try
        {
            var metadata = Parse(sessionId, File.ReadLines(file));
            _cache[sessionId] = new CacheEntry(lastWriteUtc, metadata);
            return metadata;
        }
        catch (FileNotFoundException)
        {
            _cache.Remove(sessionId);
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            _cache.Remove(sessionId);
            return null;
        }
        catch (IOException)
        {
            return _cache.TryGetValue(sessionId, out var cached) ? cached.Metadata : null;
        }
    }

    private static SessionMetadata Parse(
        string sessionId,
        IEnumerable<string> lines)
    {
        string? title = null;
        string? workspacePath = null;
        var userNamed = false;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw) ||
                raw[0] == '#' ||
                raw[0] == '-' ||
                char.IsWhiteSpace(raw[0]))
            {
                continue;
            }

            var separator = raw.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = raw[..separator].Trim();
            var value = Unquote(raw[(separator + 1)..].Trim());

            switch (key)
            {
                case "name":
                    title = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;
                case "user_named":
                    userNamed = bool.TryParse(value, out var parsed) && parsed;
                    break;
                case "cwd":
                    workspacePath = string.IsNullOrWhiteSpace(value) ? null : value;
                    break;
            }
        }

        return new SessionMetadata(sessionId, title, userNamed, workspacePath);
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2)
        {
            return value;
        }

        var first = value[0];
        var last = value[^1];
        if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
        {
            return value[1..^1];
        }

        return value;
    }

    private static bool IsSafeSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            sessionId.Length > 128 ||
            sessionId is "." or "..")
        {
            return false;
        }

        foreach (var character in sessionId)
        {
            if (!char.IsLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record CacheEntry(DateTime LastWriteUtc, SessionMetadata Metadata);
}
