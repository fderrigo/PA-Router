using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Come si indica un servizio quando lo si scrive a mano.
///
/// Da quando l'identificatore non compare piu' a schermo (S117), pretenderlo come argomento
/// renderebbe indigitabile proprio il rimedio che il router suggerisce: «alza il voto di
/// `model-17af1d65a13f4886babdb2d79cac433e`» non lo scrive nessuno. Si indica quindi come lo si
/// legge — con il modello, con il nome dell'istanza, o con un prefisso — e l'identificatore resta
/// valido per chi lo ha sottomano.
///
/// E' la stessa regola con cui si sceglie una conversazione (S62), e vale la stessa cautela: un
/// riferimento **ambiguo non sceglie nulla**. Prendere il primo che capita significherebbe votare o
/// modificare un servizio al posto di un altro senza dirlo.
/// </summary>
public static class ServicePicker
{
    /// <summary>Caratteri minimi di un prefisso: sotto, quasi tutto somiglia a tutto.</summary>
    public const int MinimumPrefix = 4;

    /// <summary>
    /// Il servizio indicato, oppure null con il motivo.
    /// </summary>
    /// <param name="snapshot">Configurazione in vigore.</param>
    /// <param name="text">Quello che l'operatore ha scritto.</param>
    /// <param name="reason">Perche' non si e' scelto: <c>service_unknown</c> o <c>service_ambiguous</c>.</param>
    public static ServiceDescriptor? Resolve(ConfigurationSnapshot snapshot, string? text, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        reason = null;
        string wanted = (text ?? string.Empty).Trim();

        if (wanted.Length == 0)
        {
            reason = "service_unknown";
            return null;
        }

        IReadOnlyList<ServiceDescriptor> services = snapshot.Registry.Services;

        // L'identificatore intero vince su tutto: chi lo scrive sa gia' quale vuole.
        if (services.FirstOrDefault(service => string.Equals(service.Id, wanted, StringComparison.Ordinal)) is ServiceDescriptor exact)
        {
            return exact;
        }

        if (Single(services.Where(service => Matches(service.Model, wanted)), out ServiceDescriptor? byModel, ref reason)) { return byModel; }
        if (Single(services.Where(service => Matches(service.Name, wanted)), out ServiceDescriptor? byName, ref reason)) { return byName; }

        if (wanted.Length < MinimumPrefix)
        {
            reason = "service_unknown";
            return null;
        }

        _ = Single(
            services.Where(service => service.Id.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)
                || Starts(service.Model, wanted)
                || Starts(service.Name, wanted)),
            out ServiceDescriptor? byPrefix,
            ref reason);

        if (byPrefix is null) { reason ??= "service_unknown"; }

        return byPrefix;
    }

    /// <summary>Uno solo passa; nessuno o piu' d'uno non scelgono, e il motivo e' diverso.</summary>
    private static bool Single(IEnumerable<ServiceDescriptor> candidates, out ServiceDescriptor? found, ref string? reason)
    {
        ServiceDescriptor[] matches = [.. candidates];
        found = matches.Length == 1 ? matches[0] : null;

        if (matches.Length > 1) { reason = "service_ambiguous"; }

        return found is not null;
    }

    private static bool Matches(string? value, string wanted) =>
        value is { Length: > 0 } && string.Equals(value, wanted, StringComparison.OrdinalIgnoreCase);

    private static bool Starts(string? value, string wanted) =>
        value is { Length: > 0 } && value.StartsWith(wanted, StringComparison.OrdinalIgnoreCase);
}
