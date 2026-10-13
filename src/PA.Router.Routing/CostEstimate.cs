using PA.Router.Abstractions.Contracts;

namespace PA.Router.Routing;

/// <summary>
/// Stima del costo di una richiesta, in un punto solo: la usano il ranking e l'analisi locale, e se
/// ciascuno la calcolasse a modo proprio l'explain mostrerebbe un costo diverso da quello che ha
/// deciso (INV-22).
/// </summary>
public static class CostEstimate
{
    /// <summary>
    /// Token in uscita con cui stimare. E' il tetto imposto dal profilo, lo stesso per tutti i
    /// candidati: stimare con il tetto di ciascun servizio penalizzerebbe chi ne dichiara uno piu'
    /// largo, a parita' di richiesta.
    /// </summary>
    public static int OutputTokens(int maxOutputBytes) => Math.Max(0, maxOutputBytes) / ContextLimits.BytesPerToken;

    /// <summary>Costo stimato, null se il prezzo e' sconosciuto.</summary>
    public static double? Of(TokenPrice? price, int inputTokens, int outputTokens) =>
        price?.Estimate(inputTokens, outputTokens);

    /// <summary>
    /// Componente di costo relativa ai candidati con un prezzo: il piu' economico vale 1, gli altri la
    /// frazione. Un costo zero vale 1; accanto a un candidato gratuito, uno a pagamento vale 0.
    /// </summary>
    public static double RelativeScore(double cheapest, double cost) =>
        cost <= 0 ? 1 : Math.Round(Math.Clamp(cheapest / cost, 0, 1), 6, MidpointRounding.ToEven);
}
