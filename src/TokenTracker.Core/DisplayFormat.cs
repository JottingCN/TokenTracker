using System.Globalization;

namespace TokenTracker.Core;

public static class DisplayFormat
{
    public static string Number(long value) =>
        value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Rate(double? rate) =>
        rate is null ? "-" : rate.Value.ToString("P1", CultureInfo.CurrentCulture);

    public static string Cost(double cost, long unpricedCalls = 0) =>
        unpricedCalls > 0
            ? $"¥{cost:0.000000} + {unpricedCalls} unpriced"
            : $"¥{cost:0.000000}";

    public static string LocalTime(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
            .ToLocalTime()
            .ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);

    public static string Price(double? value) =>
        value is null ? "-" : $"¥{value.Value:0.000000}";

    public static string Period(string? period) =>
        period switch
        {
            "peak" => "高峰",
            "idle" => "空闲",
            "flat" => "固定",
            "unknown" => "-",
            null or "" => "-",
            _ => period,
        };

    public static string SessionShort(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return "-";
        }

        return sessionId.Length > 20
            ? $"{sessionId[..8]}…"
            : sessionId;
    }

    public static string SessionDisplay(string? title, string? sessionId) =>
        string.IsNullOrWhiteSpace(title)
            ? $"未命名 · {SessionShort(sessionId)}"
            : title;
}
