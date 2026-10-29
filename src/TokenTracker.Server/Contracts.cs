using System.Text.Json.Serialization;
using TokenTracker.Core.Models;

namespace TokenTracker.Server;

public sealed record ReceiverInfo(
    string Endpoint,
    bool Running,
    long Batches,
    long Spans,
    string? LastError);

public sealed record DashboardPayload(
    SummaryDto Summary,
    List<ModelRow> Models,
    List<SessionRow> Sessions,
    List<CallRow> Calls,
    ReceiverInfo Receiver,
    string UpdatedAt,
    SummaryDto UtilitySummary);

public sealed record ModelPayload(
    SummaryDto Summary,
    List<SessionRow> Sessions,
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
    bool HasDefault,
    bool PeakAware,
    double CachedInput,
    double UncachedInput,
    double Output,
    double? PeakCachedInput,
    double? PeakUncachedInput,
    double? PeakOutput);

public sealed record PriceUpdateRequest(
    string? Model,
    bool PeakAware,
    double CachedInput,
    double UncachedInput,
    double Output,
    double? PeakCachedInput,
    double? PeakUncachedInput,
    double? PeakOutput);

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
[JsonSerializable(typeof(ServerInfoPayload))]
public partial class AppJsonContext : JsonSerializerContext;
