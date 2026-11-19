using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.AgentHostProtocol;
using TokenTracker.Core.Otlp;

namespace TokenTracker.Core.Session;

internal sealed class AhpToolNameObserver : IAsyncDisposable
{
    private readonly AhpClient _client;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Action<string, StateAction>? _onAction;
    private readonly Dictionary<string, Subscription> _subscriptions =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _channelSessionIds =
        new(StringComparer.Ordinal);
    private readonly List<Task> _readers = new();

    public AhpToolNameObserver(
        AhpClient client,
        Action<string, StateAction>? onAction = null)
    {
        _client = client;
        _onAction = onAction;
    }

    public async Task SyncAsync(
        IEnumerable<SessionSummary> sessions,
        IReadOnlySet<string> detailSessionIds,
        CancellationToken cancellationToken)
    {
        var channelSessionIds = ChannelSessionIds(sessions, detailSessionIds);
        var desired = channelSessionIds.Keys.ToHashSet(StringComparer.Ordinal);
        await _sync.WaitAsync(cancellationToken);
        try
        {
            _channelSessionIds.Clear();
            foreach (var (channel, sessionId) in channelSessionIds)
            {
                _channelSessionIds[channel] = sessionId;
            }

            foreach (var (uri, subscription) in _subscriptions.ToArray())
            {
                if (desired.Contains(uri))
                {
                    continue;
                }

                _subscriptions.Remove(uri);
                try
                {
                    await _client.UnsubscribeAsync(uri, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning(
                        "AHP tool-name unsubscribe failed for {0}: {1}",
                        uri,
                        exception.Message);
                }
                finally
                {
                    subscription.Close();
                }
            }

            foreach (var uri in desired)
            {
                if (_subscriptions.ContainsKey(uri))
                {
                    continue;
                }

                try
                {
                    var (_, subscription) = await _client.SubscribeAsync(
                        uri,
                        cancellationToken: cancellationToken);
                    _subscriptions.Add(uri, subscription);
                    _readers.RemoveAll(reader => reader.IsCompleted);
                    _readers.Add(ReadEventsAsync(
                        subscription,
                        cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning(
                        "AHP tool-name subscription failed for {0}: {1}",
                        uri,
                        exception.Message);
                }
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _sync.WaitAsync();
        try
        {
            foreach (var subscription in _subscriptions.Values)
            {
                subscription.Close();
            }

            _subscriptions.Clear();
            _channelSessionIds.Clear();
        }
        finally
        {
            _sync.Release();
        }

        await Task.WhenAll(_readers);
        _sync.Dispose();
    }

    private static Dictionary<string, string> ChannelSessionIds(
        IEnumerable<SessionSummary> sessions,
        IReadOnlySet<string> detailSessionIds)
    {
        var channels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            var sessionId = CanonicalSessionId(session.Resource);
            if (sessionId is null)
            {
                continue;
            }

            var includeDetails = detailSessionIds.Contains(sessionId);
            if ((includeDetails || IsActive(session.Status)) &&
                session.Resource.StartsWith("ahp-session:", StringComparison.Ordinal))
            {
                channels[session.Resource] = sessionId;
            }

            if (session.Chats is null)
            {
                continue;
            }

            foreach (var chat in session.Chats)
            {
                if ((includeDetails || IsActive(chat.Status)) &&
                    !string.IsNullOrWhiteSpace(chat.Resource) &&
                    chat.Resource.StartsWith("ahp-chat:", StringComparison.Ordinal))
                {
                    channels[chat.Resource] = sessionId;
                }
            }
        }

        return channels;
    }

    private static bool IsActive(SessionStatus? status) =>
        status.HasValue &&
        (status.Value & SessionStatus.InProgress) == SessionStatus.InProgress;

    private static string? CanonicalSessionId(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return null;
        }

        var slash = resource.LastIndexOf('/');
        if (slash < 0 || slash == resource.Length - 1)
        {
            return null;
        }

        return resource[(slash + 1)..];
    }

    private async Task ReadEventsAsync(
        Subscription subscription,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in subscription.Events.ReadAllAsync(cancellationToken))
            {
                if (item is not SubscriptionEventAction action)
                {
                    continue;
                }

                switch (action.Envelope.Action.Value)
                {
                    case SessionToolCallStartAction toolCall:
                        ToolNameResolver.Register(toolCall.ToolName, toolCall.DisplayName);
                        break;
                    case ChatToolCallStartAction toolCall:
                        ToolNameResolver.Register(toolCall.ToolName, toolCall.DisplayName);
                        break;
                }

                if (_channelSessionIds.TryGetValue(
                        subscription.Uri,
                        out var sessionId))
                {
                    _onAction?.Invoke(sessionId, action.Envelope.Action);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "AHP tool-name event stream failed for {0}: {1}",
                subscription.Uri,
                exception.Message);
        }
    }
}
