using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TokenTracker.Core.Session;

internal sealed record AgentHostTitle(
    string? Title,
    bool UserNamed,
    string? WorkspacePath);

internal sealed class AgentHostTitleReader
{
    private readonly string? _databasePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public AgentHostTitleReader(string? databasePath, bool resolveDefault)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? resolveDefault ? ResolveDefaultPath() : null
            : databasePath;
    }

    public AgentHostTitle? Get(string sessionId)
    {
        var path = _databasePath;
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        var stamp = ComputeStamp(path);
        lock (_gate)
        {
            if (_cache.TryGetValue(sessionId, out var cached) && cached.Stamp == stamp)
            {
                return Create(cached.Title, cached.UserNamed, cached.WorkspacePath);
            }
        }

        var (title, userNamed, workspacePath) = Query(path, sessionId);
        lock (_gate)
        {
            _cache[sessionId] = new CacheEntry(
                stamp,
                title,
                userNamed,
                workspacePath);
        }

        return Create(title, userNamed, workspacePath);
    }

    private static AgentHostTitle? Create(
        string? title,
        bool userNamed,
        string? workspacePath) =>
        title is null && workspacePath is null
            ? null
            : new AgentHostTitle(title, userNamed, workspacePath);

    private static (string? Title, bool UserNamed, string? WorkspacePath) Query(
        string path,
        string sessionId)
    {
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 2,
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var bySdk = QueryBySdkSessionId(connection, sessionId);
            var ownerUri = "copilotcli:/" + sessionId;
            var byOwner = QueryByOwner(connection, ownerUri);
            var workspacePath = bySdk.WorkspacePath ?? byOwner.WorkspacePath;

            if (bySdk.Title is not null)
            {
                return (bySdk.Title, bySdk.UserNamed, workspacePath);
            }

            if (byOwner.Title is not null)
            {
                return (byOwner.Title, byOwner.UserNamed, workspacePath);
            }

            var state = QueryChatStateTitle(connection, ownerUri);
            return (state.Title, state.UserNamed, workspacePath ?? state.WorkspacePath);
        }
        catch
        {
            return (null, false, null);
        }
    }

    private static (string? Title, bool UserNamed, string? WorkspacePath)
        QueryBySdkSessionId(SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.metadata,
                   (SELECT s.title FROM session_chat_state s
                    WHERE s.chat_uri = c.chat_uri
                    LIMIT 1),
                   (SELECT s.working_directories FROM session_chat_state s
                    WHERE s.chat_uri = c.chat_uri
                    LIMIT 1),
                   (SELECT s.launch_working_directories FROM session_chat_state s
                    WHERE s.chat_uri = c.chat_uri
                    LIMIT 1)
            FROM chats_v2 c
            WHERE c.provider_data IS NOT NULL
              AND c.provider_data LIKE @pattern
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@pattern", $"%\"sdkSessionId\":\"{sessionId}\"%");

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadTitle(reader, 0, 1, 2, 3)
            : (null, false, null);
    }

    private static (string? Title, bool UserNamed, string? WorkspacePath)
        QueryByOwner(SqliteConnection connection, string ownerUri)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.metadata,
                   (SELECT s.title FROM session_chat_state s
                    WHERE s.session_uri = c.owner_session_uri
                    LIMIT 1),
                   (SELECT s.working_directories FROM session_chat_state s
                    WHERE s.session_uri = c.owner_session_uri
                    LIMIT 1),
                   (SELECT s.launch_working_directories FROM session_chat_state s
                    WHERE s.session_uri = c.owner_session_uri
                    LIMIT 1)
            FROM chats_v2 c
            WHERE c.owner_session_uri = @uri
            ORDER BY c.chat_order
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@uri", ownerUri);

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadTitle(reader, 0, 1, 2, 3)
            : (null, false, null);
    }

    private static (string? Title, bool UserNamed, string? WorkspacePath)
        QueryChatStateTitle(SqliteConnection connection, string ownerUri)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT title, working_directories, launch_working_directories
            FROM session_chat_state
            WHERE session_uri = @uri
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@uri", ownerUri);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return (null, false, null);
        }

        var title = reader.IsDBNull(0) ? null : reader.GetString(0).Trim();
        if (title?.Length == 0)
        {
            title = null;
        }

        return (title, false, ParseWorkspace(reader, 1, 2));
    }

    private static (string? Title, bool UserNamed, string? WorkspacePath) ReadTitle(
        SqliteDataReader reader,
        int metadataOrdinal,
        int stateTitleOrdinal,
        int workingOrdinal,
        int launchOrdinal)
    {
        var (summary, userNamed) = reader.IsDBNull(metadataOrdinal)
            ? (null, false)
            : ParseMetadata(reader.GetString(metadataOrdinal));
        var workspacePath = ParseWorkspace(reader, workingOrdinal, launchOrdinal);

        if (summary is not null)
        {
            return (summary, userNamed, workspacePath);
        }

        if (!reader.IsDBNull(stateTitleOrdinal))
        {
            var title = reader.GetString(stateTitleOrdinal).Trim();
            if (title.Length > 0)
            {
                return (title, false, workspacePath);
            }
        }

        return (null, false, workspacePath);
    }

    private static (string? Summary, bool UserNamed) ParseMetadata(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
            {
                return (null, false);
            }

            var summary =
                data.TryGetProperty("summary", out var summaryElement) &&
                summaryElement.ValueKind == JsonValueKind.String
                    ? summaryElement.GetString()?.Trim()
                    : null;
            var source =
                data.TryGetProperty("titleSource", out var sourceElement) &&
                sourceElement.ValueKind == JsonValueKind.String
                    ? sourceElement.GetString()
                    : null;

            return (
                string.IsNullOrWhiteSpace(summary) ? null : summary,
                string.Equals(source, "user", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    private static string? ParseWorkspace(
        SqliteDataReader reader,
        int workingOrdinal,
        int launchOrdinal)
    {
        return ParseWorkingDirectories(
                   reader.IsDBNull(workingOrdinal)
                       ? null
                       : reader.GetString(workingOrdinal))
               ?? ParseWorkingDirectories(
                   reader.IsDBNull(launchOrdinal)
                       ? null
                       : reader.GetString(launchOrdinal));
    }

    private static string? ParseWorkingDirectories(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                var value = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Object
                        when element.TryGetProperty("uri", out var uri) &&
                             uri.ValueKind == JsonValueKind.String =>
                        uri.GetString(),
                    _ => null,
                };
                var path = ToLocalPath(value);
                if (path is not null)
                {
                    return path;
                }
            }
        }
        catch (JsonException)
        {
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
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            // file:///D%3A/path decodes to /D:/path instead of D:\path.
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

    private static long ComputeStamp(string path)
    {
        long stamp = 0;
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    stamp = Math.Max(stamp, File.GetLastWriteTimeUtc(candidate).Ticks);
                }
            }
            catch (IOException)
            {
            }
        }

        return stamp == 0 ? DateTime.UtcNow.Ticks : stamp;
    }

    private static string? ResolveDefaultPath()
    {
        var configured = Environment.GetEnvironmentVariable("TOKENTRACKER_AGENT_HOST_DB");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        foreach (var candidate in DefaultCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Running as a service (SYSTEM): locate the interactive user's database.
        return OperatingSystem.IsWindows() ? FindUserAgentHostDatabase() : null;
    }

    private static IEnumerable<string> DefaultCandidates()
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
                    product,
                    "User",
                    "globalStorage",
                    "agent-host.db");
            }

            yield break;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            yield break;
        }

        foreach (var product in new[] { "Code", "Code - Insiders" })
        {
            yield return Path.Combine(
                appData,
                product,
                "User",
                "globalStorage",
                "agent-host.db");
        }
    }

    private static string? FindUserAgentHostDatabase()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var usersRoot = Path.Combine(root, "Users");
            if (!Directory.Exists(usersRoot))
            {
                return null;
            }

            string? newest = null;
            var newestTime = DateTime.MinValue;
            foreach (var userDirectory in Directory.EnumerateDirectories(usersRoot))
            {
                foreach (var product in new[] { "Code", "Code - Insiders" })
                {
                    var candidate = Path.Combine(
                        userDirectory,
                        "AppData",
                        "Roaming",
                        product,
                        "User",
                        "globalStorage",
                        "agent-host.db");
                    try
                    {
                        if (!File.Exists(candidate))
                        {
                            continue;
                        }

                        var modified = File.GetLastWriteTimeUtc(candidate);
                        if (modified > newestTime)
                        {
                            newest = candidate;
                            newestTime = modified;
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }

            return newest;
        }
        catch
        {
            return null;
        }
    }

    private sealed record CacheEntry(
        long Stamp,
        string? Title,
        bool UserNamed,
        string? WorkspacePath);
}
