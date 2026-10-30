using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TokenTracker.Core.Session;

internal sealed record CopilotChatTitle(
    string? Title,
    string? WorkspacePath);

internal sealed class CopilotChatTitleReader
{
    private const string IndexKey = "chat.ChatSessionStore.index";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    private readonly string[] _userDataDirectories;
    private readonly object _gate = new();
    private readonly Dictionary<string, DatabaseCache> _databaseCache =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public CopilotChatTitleReader(
        IEnumerable<string>? userDataDirectories,
        bool resolveDefault)
    {
        _userDataDirectories = (userDataDirectories ??
                (resolveDefault ? DefaultUserDataDirectories() : Array.Empty<string>()))
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public CopilotChatTitle? Get(string sessionId)
    {
        if (_userDataDirectories.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastRefreshUtc >= RefreshInterval)
            {
                _entries = Load();
                _lastRefreshUtc = now;
            }

            return _entries.TryGetValue(sessionId, out var entry)
                ? new CopilotChatTitle(entry.Title, entry.WorkspacePath)
                : null;
        }
    }

    private Dictionary<string, Entry> Load()
    {
        var merged = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var nextCache = new Dictionary<string, DatabaseCache>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var userDataDirectory in _userDataDirectories)
        {
            foreach (var databasePath in StateDatabases(userDataDirectory))
            {
                try
                {
                    var stamp = GetStamp(databasePath);
                    if (stamp is null)
                    {
                        continue;
                    }

                    if (!_databaseCache.TryGetValue(databasePath, out var cached) ||
                        cached.Stamp != stamp.Value)
                    {
                        cached = new DatabaseCache(stamp.Value, ReadIndex(databasePath));
                    }

                    nextCache[databasePath] = cached;
                    foreach (var (sessionId, entry) in cached.Entries)
                    {
                        if (!merged.TryGetValue(sessionId, out var existing) ||
                            entry.LastMessageDate >= existing.LastMessageDate)
                        {
                            merged[sessionId] = entry;
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or
                    UnauthorizedAccessException or
                    SqliteException or
                    JsonException)
                {
                }
            }
        }

        _databaseCache.Clear();
        foreach (var (path, cache) in nextCache)
        {
            _databaseCache[path] = cache;
        }

        return merged;
    }

    private static long? GetStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? info.LastWriteTimeUtc.Ticks + info.Length
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> StateDatabases(string userDataDirectory)
    {
        var global = Path.Combine(userDataDirectory, "globalStorage", "state.vscdb");
        if (File.Exists(global))
        {
            yield return global;
        }

        var workspaceStorage = Path.Combine(userDataDirectory, "workspaceStorage");
        if (!Directory.Exists(workspaceStorage))
        {
            yield break;
        }

        IEnumerable<string> workspaces;
        try
        {
            workspaces = Directory.EnumerateDirectories(workspaceStorage);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var workspace in workspaces)
        {
            var candidate = Path.Combine(workspace, "state.vscdb");
            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static Dictionary<string, Entry> ReadIndex(string databasePath)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = @key LIMIT 1;";
        command.Parameters.AddWithValue("@key", IndexKey);
        var value = command.ExecuteScalar();
        var json = value switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(json))
        {
            return entries;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("entries", out var indexEntries) ||
            indexEntries.ValueKind != JsonValueKind.Object)
        {
            return entries;
        }

        foreach (var property in indexEntries.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var entry = property.Value;
            var title =
                entry.TryGetProperty("title", out var titleElement) &&
                titleElement.ValueKind == JsonValueKind.String
                    ? titleElement.GetString()?.Trim()
                    : null;
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var sessionId =
                entry.TryGetProperty("sessionId", out var idElement) &&
                idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()
                    : property.Name;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                continue;
            }

            var workspacePath =
                entry.TryGetProperty("workingDirectory", out var workspaceElement) &&
                workspaceElement.ValueKind == JsonValueKind.String
                    ? ToLocalPath(workspaceElement.GetString())
                    : null;
            var lastMessageDate =
                entry.TryGetProperty("lastMessageDate", out var dateElement) &&
                dateElement.ValueKind == JsonValueKind.Number &&
                dateElement.TryGetInt64(out var date)
                    ? date
                    : 0;

            if (entries.TryGetValue(sessionId, out var existing) &&
                existing.LastMessageDate > lastMessageDate)
            {
                continue;
            }

            entries[sessionId] = new Entry(title, workspacePath, lastMessageDate);
        }

        return entries;
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
                    product,
                    "User");
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
                yield return Path.Combine(appData, product, "User");
            }

            yield break;
        }

        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ??
            Path.Combine(homeDirectory, ".config");
        foreach (var product in new[] { "Code", "Code - Insiders" })
        {
            yield return Path.Combine(config, product, "User");
        }
    }

    private sealed record Entry(
        string? Title,
        string? WorkspacePath,
        long LastMessageDate);

    private sealed record DatabaseCache(
        long Stamp,
        Dictionary<string, Entry> Entries);
}
