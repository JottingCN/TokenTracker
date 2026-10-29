namespace TokenTracker_App;

internal sealed record ModelDescriptor(
    string ModelId,
    string Provider,
    string ShortName,
    string DisplayName);

internal static class ModelNaming
{
    public static ModelDescriptor Describe(string modelId)
    {
        var parts = modelId.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var leaf = parts.Length > 0 ? parts[^1] : modelId;
        var provider = InferProvider(leaf);
        if (provider is null)
        {
            provider = parts.Length >= 2 ? parts[^2] : "其他";
        }

        var shortName = FormatShortName(leaf, provider);
        var displayName = string.IsNullOrWhiteSpace(shortName)
            ? provider
            : $"{provider} {shortName}";

        return new ModelDescriptor(modelId, provider, shortName, displayName);
    }

    private static string? InferProvider(string leaf)
    {
        if (leaf.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            return "DeepSeek";
        }

        if (leaf.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) ||
            leaf.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
            leaf.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
            leaf.StartsWith("o4", StringComparison.OrdinalIgnoreCase))
        {
            return "OpenAI";
        }

        if (leaf.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
        {
            return "Anthropic";
        }

        if (leaf.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
        {
            return "Google";
        }

        if (leaf.StartsWith("llama", StringComparison.OrdinalIgnoreCase))
        {
            return "Meta";
        }

        if (leaf.StartsWith("kimi", StringComparison.OrdinalIgnoreCase))
        {
            return "Kimi";
        }

        if (leaf.StartsWith("mimo", StringComparison.OrdinalIgnoreCase))
        {
            return "小米 MiMo";
        }

        if (leaf.StartsWith("minimax", StringComparison.OrdinalIgnoreCase))
        {
            return "MiniMax";
        }

        if (leaf.StartsWith("glm", StringComparison.OrdinalIgnoreCase))
        {
            return "智谱 GLM";
        }

        return null;
    }

    private static readonly string[] ModelPrefixes =
    [
        "deepseek",
        "gpt",
        "claude",
        "gemini",
        "llama",
        "kimi",
        "mimo",
        "minimax",
        "glm",
    ];

    private static string FormatShortName(string leaf, string provider)
    {
        var value = leaf;
        foreach (var prefix in ModelPrefixes)
        {
            if (value.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
            {
                value = value[(prefix.Length + 1)..];
                break;
            }
        }

        var words = value.Split(
            ['-', '_'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (word.Length == 0 || char.IsDigit(word[0]))
            {
                continue;
            }

            words[i] = char.ToUpperInvariant(word[0]) + word[1..];
        }

        return string.Join(' ', words);
    }
}
