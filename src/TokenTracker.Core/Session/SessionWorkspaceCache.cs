using TokenTracker.Core.Storage;

namespace TokenTracker.Core.Session;

// Working directories are usually only published while Agent Host is connected,
// so the last known path per session is kept in the local database and reused offline.
public sealed class SessionWorkspaceCache
{
    private const int FlushThreshold = 16;

    private readonly SpanStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    public SessionWorkspaceCache(SpanStore store) => _store = store;

    public string? Resolve(string sessionId, string? liveWorkspacePath)
    {
        if (string.IsNullOrWhiteSpace(liveWorkspacePath))
        {
            return _store.GetSessionWorkspace(sessionId);
        }

        lock (_gate)
        {
            _pending[sessionId] = liveWorkspacePath;
            if (_pending.Count >= FlushThreshold)
            {
                Flush();
            }
        }

        return liveWorkspacePath;
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            try
            {
                _store.UpsertSessionWorkspaces(
                    new Dictionary<string, string>(
                        _pending,
                        StringComparer.OrdinalIgnoreCase));
                _pending.Clear();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"workspace cache write failed: {exception.Message}");
            }
        }
    }
}
