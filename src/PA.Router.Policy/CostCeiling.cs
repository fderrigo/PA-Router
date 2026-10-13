using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Policy;

/// <summary>
/// Quanto puo' costare **al massimo** una richiesta su un servizio, in un punto solo: la usano il tetto di spesa della
/// policy (ADR-032, fase 5), `pa explain` e il racconto di un rifiuto. Se ciascuno la calcolasse a modo proprio, il
/// rifiuto mostrerebbe una cifra diversa da quella che ha deciso (INV-22).
///
/// E' la stima con i token della richiesta in ingresso e l'uscita limitata dal piu' stretto fra il tetto della
/// configurazione e quello dichiarato dal servizio (`maxOutputTokens`), che l'adapter fa rispettare. La stima del ranking
/// usa invece lo stesso tetto per tutti, ed e' giusta per confrontare: non per dire quanto si spende.
/// </summary>
public static class CostCeiling
{
    /// <summary>Costo massimo, null se il prezzo e' sconosciuto.</summary>
    /// <param name="price">Prezzo marginale del servizio: zero se non ha costo a consumo, null se sconosciuto.</param>
    /// <param name="inputTokens">Token stimati della richiesta con il suo contesto.</param>
    /// <param name="maxOutputBytes">Byte massimi della risposta ammessi dalla configurazione.</param>
    /// <param name="serviceMaxOutputTokens">Tetto di uscita dichiarato dal servizio; 0 se non dichiarato.</param>
    public static double? Of(TokenPrice? price, int inputTokens, int maxOutputBytes, int serviceMaxOutputTokens)
    {
        if (price is not TokenPrice known
            || !double.IsFinite(known.InputPerMillionTokens) || known.InputPerMillionTokens < 0
            || !double.IsFinite(known.OutputPerMillionTokens) || known.OutputPerMillionTokens < 0)
        {
            return null;
        }

        double estimate = known.Estimate(inputTokens, GenerationLimits.OutputTokens(maxOutputBytes, serviceMaxOutputTokens));
        return double.IsFinite(estimate) ? estimate : null;
    }

    /// <summary>Costo massimo della richiesta su un candidato.</summary>
    public static double? Of(PolicyCandidate candidate, int inputTokens, int maxOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // A nested agent chooses its own model calls; a per-generation limit cannot bound its spend.
        return candidate.Kind is TargetKind.Agent && candidate.Calibration.Price != TokenPrice.Free
            ? null
            : Of(candidate.Calibration.Price, inputTokens, maxOutputBytes, candidate.MaxOutputTokens);
    }

    /// <summary>
    /// Vero se il candidato resta fuori dal tetto: costa di piu', oppure il suo prezzo e' sconosciuto e quindi non si puo'
    /// dimostrare che ci stia sotto. Senza tetto non esclude nessuno.
    /// </summary>
    public static bool Exceeds(double? ceiling, double? cap) =>
        cap is double limit && (!double.IsFinite(limit) || limit < 0
            || ceiling is not double cost || !double.IsFinite(cost) || cost < 0 || cost > limit);
}
