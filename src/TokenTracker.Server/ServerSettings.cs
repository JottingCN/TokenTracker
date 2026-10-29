using TokenTracker.Core.Storage;

namespace TokenTracker.Server;

public sealed class ServerSettings
{
    public const int MaxRetentionDays = 36_500;

    private int _retentionDays;

    public ServerSettings(int retentionDays)
    {
        _retentionDays = Math.Clamp(retentionDays, 0, MaxRetentionDays);
    }

    public int RetentionDays => Volatile.Read(ref _retentionDays);

    public void SetRetentionDays(int days) =>
        Volatile.Write(ref _retentionDays, Math.Clamp(days, 0, MaxRetentionDays));

    public int ApplyRetention(SpanStore store)
    {
        var days = RetentionDays;
        return days <= 0
            ? 0
            : store.DeleteOlderThan(
                DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds());
    }
}
