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

    public static string Aic(long? nanoAiu) =>
        nanoAiu is null
            ? "-"
            : $"{nanoAiu.Value / 1_000_000_000d:0.####} AIC";

    public static string Period(string? period) =>
        period switch
        {
            null or "" or "unknown" => "-",
            _ => period,
        };

    public static string Source(string? source) =>
        source?.ToLowerInvariant() switch
        {
            "extension-host" or "copilot-chat" => "Copilot Chat",
            "agent-host" or "github-copilot" => "Agent Host",
            null or "" => "unknown",
            _ => source!,
        };

    public static string Operation(string? operation) =>
        string.IsNullOrWhiteSpace(operation) ? "-" : operation;

    public static string Duration(long? milliseconds) =>
        milliseconds is null
            ? "-"
            : milliseconds.Value < 1_000
                ? $"{milliseconds.Value} ms"
                : $"{milliseconds.Value / 1_000d:0.00} s";

    public static string Duration(double? milliseconds) =>
        milliseconds is null
            ? "-"
            : Duration((long)Math.Round(milliseconds.Value));

    public static string Speed(double? tokensPerSecond) =>
        tokensPerSecond is null
            ? "-"
            : $"{tokensPerSecond.Value:0.0} tok/s";

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

    // Language-neutral: clients localize the missing-title fallback themselves.
    public static string SessionDisplay(string? title, string? sessionId) =>
        string.IsNullOrWhiteSpace(title) ? SessionShort(sessionId) : title;
}
