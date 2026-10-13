using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Policy;

namespace PA.Router.Core;

/// <summary>
/// Conservative reservations for one execution, including fallbacks and tool rounds. Reservations are never
/// refunded from provider usage: missing billing, partial replies and failed connections are not proof of zero cost.
/// </summary>
internal sealed class TurnSpendBudget(CompiledPolicy policy)
{
    private double reserved;

    internal CompiledPolicy RemainingPolicy => policy.MaxCostPerRequest is double cap
        ? policy with { MaxCostPerRequest = Math.Max(0, cap - reserved) }
        : policy;

    internal bool TryReserve(PolicyCandidate candidate, int inputTokens)
    {
        if (policy.MaxCostPerRequest is null) { return true; }

        double? cost = CostCeiling.Of(candidate, inputTokens, policy.MaxOutputBytes);
        if (CostCeiling.Exceeds(cost, RemainingPolicy.MaxCostPerRequest)) { return false; }

        reserved += cost!.Value;
        return true;
    }

    /// <summary>Align both projections of the registry and fail closed on unknown or incomparable paid prices.</summary>
    internal static ExecutionRequestContext Prepare(ExecutionRequestContext context)
    {
        string[] currencies = context.Candidates
            .Where(candidate => candidate.Enabled && context.Policy.AllowsService(candidate.TargetId)
                && candidate.Capability == CapabilityRouting.ForRequest(context.RequiredCapabilities)
                && (context.RequestedTargetId is null || context.RequestedTargetId == candidate.TargetId))
            .Select(candidate => context.Dispatch.GetValueOrDefault(candidate.TargetId)?.Pricing)
            .OfType<ServicePricing>()
            .Where(pricing => pricing.Price != TokenPrice.Free)
            .Select(pricing => pricing.Currency).Distinct(StringComparer.Ordinal).ToArray();

        return context with
        {
            Candidates = [.. context.Candidates.Select(candidate =>
            {
                TargetDispatchInfo? dispatch = context.Dispatch.GetValueOrDefault(candidate.TargetId);
                int output = GenerationLimits.OutputTokens(context.Policy.MaxOutputBytes, candidate.MaxOutputTokens);
                output = GenerationLimits.OutputTokens(output, dispatch?.MaxOutputTokens ?? 0);
                TokenPrice? price = candidate.Calibration.Price;

                if (context.Policy.MaxCostPerRequest is not null
                    && ((dispatch?.Pricing is ServicePricing declared && declared.Price != price)
                        || (price != TokenPrice.Free
                        && (dispatch?.Pricing is not ServicePricing pricing || currencies.Length != 1
                        || string.IsNullOrWhiteSpace(pricing.Currency)
                        // Cache writes may have a premium absent from the two-price registry contract.
                        || dispatch.PromptCaching))))
                {
                    price = null;
                }

                return candidate with { MaxOutputTokens = output, Calibration = candidate.Calibration with { Price = price } };
            })],
        };
    }
}
