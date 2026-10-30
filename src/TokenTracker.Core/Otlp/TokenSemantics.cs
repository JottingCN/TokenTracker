namespace TokenTracker.Core.Otlp;

internal static class TokenSemantics
{
    public static bool UsesExclusiveInputTokens(string? provider) =>
        provider is not null &&
        (provider.Contains("anthropic", StringComparison.OrdinalIgnoreCase) ||
         provider.Contains("claude", StringComparison.OrdinalIgnoreCase));

    public static long TotalInputTokens(
        string? provider,
        long inputTokens,
        long cacheReadTokens,
        long cacheCreationTokens) =>
        UsesExclusiveInputTokens(provider)
            ? inputTokens + cacheReadTokens + cacheCreationTokens
            : inputTokens;
}
