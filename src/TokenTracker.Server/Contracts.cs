using System.Text.Json.Serialization;
using TokenTracker.Core.Models;

namespace TokenTracker.Server;

public sealed record ReceiverInfo(
    string Endpoint,
    bool Running,
    long Batches,
    long Spans,
    string? LastError);

public sealed record AhpSessionPayload(
    string Resource,
    string Provider,
    string Title,
    string Status,
    string? Activity,
    string? WorkspacePath,
    string ModifiedAt,
    int Chats);

public sealed record AhpStatusPayload(
    string State,
    bool Connected,
    int Sessions,
    List<AhpSessionPayload> Items);

public sealed record DashboardPayload(
    SummaryDto Summary,
    List<ModelRow> Models,
    List<SessionRow> Sessions,
    List<CallRow> Calls,
    ReceiverInfo Receiver,
    string UpdatedAt,
    int RetentionDays,
    AhpStatusPayload Ahp);

public sealed record SettingsPayload(int RetentionDays);

public sealed record SettingsUpdateRequest(int? RetentionDays);

public sealed record ModelPayload(
    SummaryDto Summary,
    List<SessionRow> Sessions,
    List<CallRow> Calls,
    string Model);

public sealed record SessionPayload(
    SummaryDto Summary,
    List<CallRow> Calls,
    string SessionId,
    string? Title);

public sealed record ServerInfoPayload(
    string Name,
    string Version,
    int Protocol,
    int Port,
    string StartedAt,
    string? BuildId,
    string[] Features);

public sealed record SubscribeMessage(
    string? Type,
    string? View,
    int Range,
    bool Utility,
    string? Id,
    long? FromMs,
    long? ToMs,
    string? Client);

public sealed record ModelPriceRowPayload(
    string Model,
    bool Customized,
    long Calls,
    bool PeakAware,
    double CachedInput,
    double UncachedInput,
    double Output,
    double? PeakCachedInput,
    double? PeakUncachedInput,
    double? PeakOutput,
    long? LongContextThreshold,
    double? LongCachedInput,
    double? LongUncachedInput,
    double? LongOutput,
    double? LongPeakCachedInput,
    double? LongPeakUncachedInput,
    double? LongPeakOutput,
    double? CacheWrite,
    double? PeakCacheWrite,
    double? LongCacheWrite,
    double? LongPeakCacheWrite);

public sealed record PriceUpdateRequest(
    string? Model,
    bool PeakAware,
    double CachedInput,
    double UncachedInput,
    double Output,
    double? PeakCachedInput,
    double? PeakUncachedInput,
    double? PeakOutput,
    long? LongContextThreshold,
    double? LongCachedInput,
    double? LongUncachedInput,
    double? LongOutput,
    double? LongPeakCachedInput,
    double? LongPeakUncachedInput,
    double? LongPeakOutput,
    double? CacheWrite,
    double? PeakCacheWrite,
    double? LongCacheWrite,
    double? LongPeakCacheWrite);

public sealed record LocalImportRequest(string[]? Paths);

public sealed record LocalImportPayload(
    long Scanned,
    long Matched,
    long Inserted,
    long Skipped,
    string[] Errors);

public sealed record DashboardMessage(string Type, DashboardPayload Data);

public sealed record ModelMessage(string Type, ModelPayload Data);

public sealed record SessionMessage(string Type, SessionPayload Data);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DashboardMessage))]
[JsonSerializable(typeof(ModelMessage))]
[JsonSerializable(typeof(SessionMessage))]
[JsonSerializable(typeof(SubscribeMessage))]
[JsonSerializable(typeof(List<ModelPriceRowPayload>))]
[JsonSerializable(typeof(ModelPriceRowPayload))]
[JsonSerializable(typeof(PriceUpdateRequest))]
[JsonSerializable(typeof(LocalImportRequest))]
[JsonSerializable(typeof(LocalImportPayload))]
[JsonSerializable(typeof(AhpStatusPayload))]
[JsonSerializable(typeof(AhpSessionPayload))]
[JsonSerializable(typeof(SettingsPayload))]
[JsonSerializable(typeof(SettingsUpdateRequest))]
[JsonSerializable(typeof(ServerInfoPayload))]
public partial class AppJsonContext : JsonSerializerContext;
