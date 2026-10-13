using PA.Router.Abstractions.Contracts;

namespace PA.Router.Abstractions;

/// <summary>One explicit generation budget shared by admission and the HTTP adapters.</summary>
public static class GenerationLimits
{
    /// <summary>
    /// Converts the configured byte budget into a token policy, then restricts it by the service ceiling.
    /// This is not a tokenizer or a guarantee about UTF-8 size: the independent byte limit still applies.
    /// </summary>
    public static int OutputTokens(int maxOutputBytes, int serviceMaxOutputTokens)
    {
        if (maxOutputBytes <= 0) { return 0; }
        int tokens = Math.Max(1, maxOutputBytes / ContextLimits.BytesPerToken);
        return serviceMaxOutputTokens > 0 ? Math.Min(tokens, serviceMaxOutputTokens) : tokens;
    }
}
