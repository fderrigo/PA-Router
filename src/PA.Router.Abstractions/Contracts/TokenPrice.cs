namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Prezzo a consumo di un servizio, per milione di token. Serve a stimare quanto costa **una
/// richiesta**, non a fatturare: il consumo vero lo dice il fornitore.
/// </summary>
/// <param name="InputPerMillionTokens">Prezzo di un milione di token in ingresso.</param>
/// <param name="OutputPerMillionTokens">Prezzo di un milione di token in uscita.</param>
public readonly record struct TokenPrice(double InputPerMillionTokens, double OutputPerMillionTokens)
{
    /// <summary>Nessun costo a consumo: modello sulla macchina dell'operatore, o abbonamento.</summary>
    public static TokenPrice Free => new(0, 0);

    /// <summary>Costo stimato di una richiesta con i token indicati.</summary>
    public double Estimate(int inputTokens, int outputTokens) =>
        ((Math.Max(0, inputTokens) * InputPerMillionTokens) + (Math.Max(0, outputTokens) * OutputPerMillionTokens))
        / 1_000_000d;
}

/// <summary>
/// Prezzo dichiarato nel registro. La valuta e' esplicita perche' due prezzi in valute diverse non si
/// confrontano: la configurazione che li mescola viene rifiutata alla lettura.
/// </summary>
/// <param name="Currency">Codice ISO 4217, per esempio <c>EUR</c>.</param>
/// <param name="InputPerMillionTokens">Prezzo di un milione di token in ingresso.</param>
/// <param name="OutputPerMillionTokens">Prezzo di un milione di token in uscita.</param>
public sealed record ServicePricing(string Currency, double InputPerMillionTokens, double OutputPerMillionTokens)
{
    /// <summary>Prezzo per il calcolo.</summary>
    public TokenPrice Price => new(InputPerMillionTokens, OutputPerMillionTokens);
}
