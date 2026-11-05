using System.Collections.Concurrent;

namespace TokenTracker.Server;

public sealed class RealtimeHub
{
    public static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(60);

    // Metrics arrive in batches every few seconds, and each notification rebuilds
    // every subscriber's full payload. A short coalescing window keeps a burst of
    // ingests from triggering one rebuild per batch while staying imperceptible.
    private static readonly TimeSpan NotifyCoalesceInterval =
        TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<long, RealtimeConnection> _connections = new();
    private long _nextId;
    private long _lastHttpActivityMs;
    private int _notifyPending;

    // Polling clients (no WebSocket) never appear in _connections, so HTTP API
    // calls also count as activity; otherwise the server exits under a live client.
    public void RecordHttpActivity() =>
        Interlocked.Exchange(
            ref _lastHttpActivityMs,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public bool HasRecentHttpActivity(TimeSpan window)
    {
        var last = Interlocked.Read(ref _lastHttpActivityMs);
        return last > 0 &&
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last <
                (long)window.TotalMilliseconds;
    }

    public RealtimeConnection Register()
    {
        var connection = new RealtimeConnection(Interlocked.Increment(ref _nextId));
        _connections[connection.Id] = connection;
        return connection;
    }

    public void Unregister(RealtimeConnection connection) =>
        _connections.TryRemove(connection.Id, out _);

    public int ActiveConnections => _connections.Count;

    public int ActiveVscodeConnections =>
        _connections.Values.Count(connection => connection.IsVscode);

    public void NotifyChanged()
    {
        if (Interlocked.Exchange(ref _notifyPending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(NotifyCoalesceInterval).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
            }

            Interlocked.Exchange(ref _notifyPending, 0);
            foreach (var connection in _connections.Values)
            {
                connection.Signal();
            }
        });
    }
}

public sealed class RealtimeConnection
{
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private volatile bool _isVscode;

    public RealtimeConnection(long id) => Id = id;

    public long Id { get; }

    public bool IsVscode => _isVscode;

    public void SetVscode(bool value) => _isVscode = value;

    public void Signal() => _signal.Release();

    public async Task WaitForSignalAsync(TimeSpan timeout, CancellationToken token)
    {
        await _signal.WaitAsync(timeout, token).ConfigureAwait(false);
        while (await _signal.WaitAsync(0, token).ConfigureAwait(false))
        {
        }
    }
}
