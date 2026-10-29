using System.Collections.Concurrent;

namespace TokenTracker.Server;

public sealed class RealtimeHub
{
    public static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<long, RealtimeConnection> _connections = new();
    private long _nextId;

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
        foreach (var connection in _connections.Values)
        {
            connection.Signal();
        }
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
