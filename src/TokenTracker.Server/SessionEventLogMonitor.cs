using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TokenTracker.Core.Storage;

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
    private const int MaxEventLineBytes = 2 * 1024 * 1024;
    private const int MaxPendingBytes = 32 * 1024 * 1024;
    private const int InitialOffsetScanBytes = 64 * 1024;
    private static readonly StringComparer FilePathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    private readonly ConcurrentDictionary<string, SessionFileState> _states =
        new(FilePathComparer);
    private readonly Dictionary<string, SessionEventLogReadState> _persistedStates;
    private readonly LinkedList<string> _seenEventOrder = new();
    private readonly HashSet<string> _seenEventIds =
        new(StringComparer.Ordinal);
    private readonly object _seenGate = new();
    private readonly string _root;
    private readonly RealtimeHub _hub;
    private readonly SpanStore? _store;
    private long _eventsRead;
    private long _uniqueEvents;
    private long _lastReadMs;
    private bool _stateDirty = true;
    private string? _lastError;
    private volatile bool _running;

    public SessionEventLogMonitor(
        string root,
        RealtimeHub hub,
        SpanStore? store = null)
    {
        _root = root;
        _hub = hub;
        _store = store;
        _persistedStates = store?.GetSessionEventLogReadStates()
            .ToDictionary(state => state.FileKey, StringComparer.Ordinal)
            ?? new Dictionary<string, SessionEventLogReadState>(
                StringComparer.Ordinal);
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
                PersistReadStates();
                return;
            }

            var files = Directory.EnumerateDirectories(_root)
                .Select(directory => Path.Combine(directory, "events.jsonl"))
                .Where(File.Exists)
                .OrderBy(file => file, FilePathComparer)
                .Take(MaxSessions)
                .ToArray();

            var present = new HashSet<string>(
                files,
                FilePathComparer);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadFileAsync(file, cancellationToken);
            }

            foreach (var pair in _states)
            {
                if (!present.Contains(pair.Key))
                {
                    if (_states.TryRemove(pair.Key, out _))
                    {
                        _stateDirty = true;
                    }
                }
            }

            _lastError = null;
            PersistReadStates();
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
        var fileKey = GetFileKey(file);
        var state = _states.GetOrAdd(
            file,
            _ => CreateFileState(fileKey));

        var length = info.Length;
        var lastWriteTicks = info.LastWriteTimeUtc.Ticks;
        if (!state.Initialized)
        {
            state.Offset = await FindInitialOffsetAsync(
                file,
                length,
                cancellationToken);
            state.CommittedOffset = state.Offset;
            state.LastWriteTicks = lastWriteTicks;
            state.Initialized = true;
            _stateDirty = true;
            return;
        }

        var changedIdentity =
            length < state.Offset ||
            (length == state.Offset &&
             state.LastWriteTicks != 0 &&
             lastWriteTicks != state.LastWriteTicks);

        if (changedIdentity)
        {
            state.Reset();
            _stateDirty = true;
        }
        var previousLastWriteTicks = state.LastWriteTicks;
        state.LastWriteTicks = lastWriteTicks;
        if (previousLastWriteTicks != lastWriteTicks)
        {
            _stateDirty = true;
        }

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

        var previousCommittedOffset = state.CommittedOffset;
        var combined = new byte[state.Pending.Count + chunk.Length];
        state.Pending.CopyTo(combined, 0);
        chunk.CopyTo(combined, state.Pending.Count);

        var processStart = 0;
        if (state.DiscardingOversizedLine)
        {
            var newline = Array.IndexOf(combined, (byte)'\n');
            if (newline < 0)
            {
                state.DiscardedLineBytes += chunk.Length;
                state.Offset += chunk.Length;
                return;
            }

            state.CommittedOffset += state.DiscardedLineBytes + newline + 1;
            state.DiscardedLineBytes = 0;
            state.DiscardingOversizedLine = false;
            processStart = newline + 1;
        }

        var completeEnd = GetLastCompleteLineEnd(combined);
        if (completeEnd < processStart)
        {
            completeEnd = processStart;
        }

        ProcessCompleteLines(combined, processStart, completeEnd);
        state.CommittedOffset += completeEnd - processStart;

        state.Pending.Clear();
        var tailLength = combined.Length - completeEnd;
        if (tailLength > 0)
        {
            var pendingBytes = _states.Values.Sum(item => item.Pending.Count);
            if (tailLength > MaxEventLineBytes ||
                pendingBytes + tailLength > MaxPendingBytes)
            {
                state.DiscardingOversizedLine = true;
                state.DiscardedLineBytes = tailLength;
                Console.Error.WriteLine(
                    tailLength > MaxEventLineBytes
                        ? "session event log monitor skipped a line larger than " +
                          $"{MaxEventLineBytes} bytes"
                        : "session event log monitor skipped a partial line because " +
                          $"buffered data exceeded {MaxPendingBytes} bytes");
            }
            else
            {
                state.Pending.AddRange(combined.AsSpan(completeEnd, tailLength));
            }
        }

        state.Offset += chunk.Length;
        _stateDirty |= state.CommittedOffset != previousCommittedOffset;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Interlocked.Exchange(ref _lastReadMs, now);
    }

    private SessionFileState CreateFileState(string fileKey)
    {
        return _persistedStates.Remove(fileKey, out var saved)
            ? new SessionFileState(fileKey, saved)
            : new SessionFileState(fileKey);
    }

    private static async Task<long> FindInitialOffsetAsync(
        string file,
        long length,
        CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return 0;
        }

        var minimumOffset = Math.Max(0, length - MaxEventLineBytes);
        var position = length;
        var buffer = new byte[InitialOffsetScanBytes];
        await using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: InitialOffsetScanBytes,
            useAsync: true);

        while (position > minimumOffset)
        {
            var count = (int)Math.Min(
                buffer.Length,
                position - minimumOffset);
            position -= count;
            stream.Seek(position, SeekOrigin.Begin);
            var read = 0;
            while (read < count)
            {
                var bytesRead = await stream.ReadAsync(
                    buffer.AsMemory(read, count - read),
                    cancellationToken);
                if (bytesRead == 0)
                {
                    return length;
                }

                read += bytesRead;
            }

            for (var index = read - 1; index >= 0; index--)
            {
                if (buffer[index] == (byte)'\n')
                {
                    return position + index + 1;
                }
            }
        }

        return length;
    }

    private static string GetFileKey(string file)
    {
        var path = Path.GetFullPath(file);
        if (OperatingSystem.IsWindows())
        {
            path = path.ToUpperInvariant();
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(path)));
    }

    private void PersistReadStates()
    {
        if (_store is null || !_stateDirty)
        {
            return;
        }

        _store.SaveSessionEventLogReadStates(
            _states.Values
                .Where(state => state.Initialized)
                .Select(state => new SessionEventLogReadState(
                    state.FileKey,
                    state.CommittedOffset,
                    state.LastWriteTicks))
                .ToArray());
        _stateDirty = false;
    }

    private void ProcessCompleteLines(
        byte[] content,
        int start,
        int end)
    {
        for (var index = start; index < end;)
        {
            var newline = Array.IndexOf(
                content,
                (byte)'\n',
                index,
                end - index);
            if (newline < 0)
            {
                break;
            }

            var lineLength = newline - index;
            if (lineLength > 0 && content[index + lineLength - 1] == (byte)'\r')
            {
                lineLength--;
            }

            if (lineLength > MaxEventLineBytes)
            {
                Console.Error.WriteLine(
                    $"session event log monitor skipped a line larger than " +
                    $"{MaxEventLineBytes} bytes");
            }
            else if (lineLength > 0)
            {
                ProcessLine(content.AsSpan(index, lineLength));
            }

            index = newline + 1;
        }
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
                if (_states.TryRemove(pair.Key, out _))
                {
                    _stateDirty = true;
                }
            }
        }
    }

    private sealed class SessionFileState
    {
        public SessionFileState(string fileKey)
        {
            FileKey = fileKey;
            Pending = new List<byte>();
        }

        public SessionFileState(
            string fileKey,
            SessionEventLogReadState saved)
            : this(fileKey)
        {
            Offset = saved.Offset;
            CommittedOffset = saved.Offset;
            LastWriteTicks = saved.LastWriteTicks;
            Initialized = true;
        }

        public string FileKey { get; }
        public long Offset { get; set; }
        public long CommittedOffset { get; set; }
        public long LastWriteTicks { get; set; }
        public long DiscardedLineBytes { get; set; }
        public bool DiscardingOversizedLine { get; set; }
        public bool Initialized { get; set; }
        public List<byte> Pending { get; }

        public void Reset()
        {
            Offset = 0;
            CommittedOffset = 0;
            LastWriteTicks = 0;
            DiscardedLineBytes = 0;
            DiscardingOversizedLine = false;
            Pending.Clear();
        }
    }
}
