using System.Text.Json;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Core;

/// <summary>Conservative UTF-8 token estimate, including tools and protocol framing, refreshed after each tool round.</summary>
internal static class ConversationMeasure
{
    internal static int InputTokens(SafePayload payload, IReadOnlyList<ToolRound> rounds, int initialEstimate)
    {
        // One byte per token plus framing reserve; never use the common four-byte heuristic for admission.
        long size = payload.Bytes.Length + (long)payload.Instructions.Length
            + 256L * (1 + ChatTranscript.Split(payload).Count);
        if (payload.Tools is { Count: > 0 } tools)
        {
            size += JsonSerializer.SerializeToUtf8Bytes(tools).Length + (long)tools.Count * 256;
        }

        size = Math.Max(size, initialEstimate);
        foreach (ToolRound round in rounds)
        {
            size += JsonSerializer.SerializeToUtf8Bytes(round).Length + 256L * (1 + round.Results.Count);
        }

        return (int)Math.Min(int.MaxValue, size);
    }

    internal static int RequiredWindow(int inputTokens, int outputTokens) =>
        (int)Math.Min(int.MaxValue, Math.Max(4096L, (long)inputTokens + outputTokens));
}
