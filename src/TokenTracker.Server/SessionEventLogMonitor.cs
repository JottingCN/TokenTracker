using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace TokenTracker.Server;

public sealed record SessionEventLogStatus(
    string Root,
    bool Running,
    int MonitoredSessions,
    long EventsRead,
    long UniqueEvents,
    string? LastReadText,
    string? LastError);

public sealed class SessionEventLogMonitor
{
    private const int MaxSessions = 256;
    private const int MaxSeenEventIds = 4096;
    private const int ReadChunkBytes = 256 * 1024;
    private readonly ConcurrentDictionary<string, SessionFileState> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _seenEventOrder = new();
    private readonly HashSet<string> _seenEventIds =
        new(StringComparer.Ordinal);
    private readonly object _seenGate = new();
    private readonly string _root;
    private readonly RealtimeHub _hub;
    private long _eventsRead;
    private long _uniqueEvents;
    private long _lastReadMs;
    private string? _lastError;
    private volatile bool _running;

    public SessionEventLogMonitor(
        string root,
        RealtimeHub hub)
    {
        _root = root;
        _hub = hub;
    }

    public SessionEventLogStatus GetStatus() => new(
        _root,
        _running,
        _states.Count,
        Interlocked.Read(ref _eventsRead),
        Interlocked.Read(ref _uniqueEvents),
        _lastReadMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(_lastReadMs)
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss")
            : null,
        _lastError);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _running = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _running = false;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                _lastError = null;
                PruneMissingSessions();
                return;
            }

            var files = Directory.EnumerateDirectories(_root)
                .Select(directory => Path.Combine(directory, "events.jsonl"))
                .Where(File.Exists)
                .Take(MaxSessions)
                .ToArray();

            var present = new HashSet<string>(
                files,
                StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadFileAsync(file, cancellationToken);
            }

            foreach (var pair in _states)
            {
                if (!present.Contains(pair.Key))
                {
                    _states.TryRemove(pair.Key, out _);
                }
            }

            _lastError = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            Console.Error.WriteLine(
                $"session event log monitor failed: {exception.Message}");
        }
    }

    private async Task ReadFileAsync(
        string file,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(file);
        var state = _states.GetOrAdd(
            file,
            static (path, _) => new SessionFileState(path),
            info);

        var length = info.Length;
        var createdTicks = info.CreationTimeUtc.Ticks;
        var lastWriteTicks = info.LastWriteTimeUtc.Ticks;
        var changedIdentity =
            state.CreatedTicks != createdTicks ||
            length < state.Offset ||
            (length == state.Offset &&
             state.LastWriteTicks != 0 &&
             lastWriteTicks != state.LastWriteTicks);

        if (changedIdentity)
        {
            state.Offset = 0;
            state.Pending.Clear();
            state.CreatedTicks = createdTicks;
        }
        state.LastWriteTicks = lastWriteTicks;

        if (length <= state.Offset)
        {
            return;
        }

        var toRead = (int)Math.Min(
            length - state.Offset,
            ReadChunkBytes);

        byte[] chunk;
        try
        {
            await using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            stream.Seek(state.Offset, SeekOrigin.Begin);
            chunk = new byte[toRead];
            var read = 0;
            while (read < toRead)
            {
                var count = await stream.ReadAsync(
                    chunk.AsMemory(read, toRead - read),
                    cancellationToken);
                if (count == 0)
                {
                    break;
                }
                read += count;
            }

            if (read < chunk.Length)
            {
                Array.Resize(ref chunk, read);
            }
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        if (chunk.Length == 0)
        {
            return;
        }

        var combined = new byte[state.Pending.Count + chunk.Length];
        state.Pending.CopyTo(combined, 0);
        chunk.CopyTo(combined, state.Pending.Count);

        var completeEnd = GetLastCompleteLineEnd(combined);
        if (completeEnd == 0)
        {
            state.Pending.Clear();
            state.Pending.AddRange(combined);
            state.Offset += chunk.Length;
            return;
        }

        state.Pending.Clear();
        for (var index = 0; index < completeEnd;)
        {
            var newline = Array.IndexOf(combined, (byte)'\n', index, completeEnd - index);
            if (newline < 0)
            {
                break;
            }

            var lineLength = newline - index;
            if (lineLength > 0 && combined[index + lineLength - 1] == (byte)'r')
            {
                lineLength--;
            }

            if (lineLength > 0)
            {
                ProcessLine(combined.AsSpan(index, lineLength));
            }

            index = newline + 1;
        }

        state.Offset += chunk.Length;

        var tailLength = combined.Length - completeEnd;
        if (tailLength > 0)
        {
            state.Pending.AddRange(combined.AsSpan(completeEnd, tailLength));
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Interlocked.Exchange(ref _lastReadMs, now);
    }

    private void ProcessLine(ReadOnlySpan<byte> utf8Line)
    {
        try
        {
            var json = Encoding.UTF8.GetString(utf8Line);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            Interlocked.Increment(ref _eventsRead);

            if (!root.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var eventId = idElement.GetString();
            if (string.IsNullOrWhiteSpace(eventId))
            {
                return;
            }

            if (!MarkUnique(eventId))
            {
                return;
            }

            Interlocked.Increment(ref _uniqueEvents);
            _hub.NotifyChanged();
        }
        catch (JsonException)
        {
        }
    }

    private bool MarkUnique(string eventId)
    {
        lock (_seenGate)
        {
            if (!_seenEventIds.Add(eventId))
            {
                return false;
            }

            _seenEventOrder.AddLast(eventId);
            while (_seenEventOrder.Count > MaxSeenEventIds)
            {
                var oldest = _seenEventOrder.First!;
                _seenEventIds.Remove(oldest.Value);
                _seenEventOrder.RemoveFirst();
            }
            return true;
        }
    }

    private static int GetLastCompleteLineEnd(byte[] content)
    {
        var newline = Array.LastIndexOf(content, (byte)'\n');
        return newline >= 0 ? newline + 1 : 0;
    }

    private void PruneMissingSessions()
    {
        foreach (var pair in _states)
        {
            if (!Directory.Exists(Path.GetDirectoryName(pair.Key)))
            {
                _states.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class SessionFileState
    {
        public SessionFileState(string path)
        {
            Path = path;
            Pending = new List<byte>();
        }

        public string Path { get; }
        public long Offset { get; set; }
        public long CreatedTicks { get; set; }
        public long LastWriteTicks { get; set; }
        public List<byte> Pending { get; }
    }
}
