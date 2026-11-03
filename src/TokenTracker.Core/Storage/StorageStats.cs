namespace TokenTracker.Core.Storage;

public sealed record StorageTableInfo(string Name, long Rows);

public sealed record StorageStats(
    string DatabasePath,
    long SizeBytes,
    long WalBytes,
    long ShmBytes,
    long PageSize,
    long PageCount,
    string JournalMode,
    long RawSpans,
    long CombinedSpans,
    long OutcomeMetricPoints,
    List<StorageTableInfo> Tables);
