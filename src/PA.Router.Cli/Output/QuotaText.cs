using System.Globalization;
using PA.Router.Cli.Localization;
using PA.Router.Hosting.Runtime;

namespace PA.Router.Cli.Output;

/// <summary>
/// Le frasi di un servizio in pausa per quota finita, in un punto solo (24 settembre 2026). L'operatore ha chiesto che i
/// messaggi dicano chiaramente che il servizio non e' piu' usato, fino a quando, e dove si riattiva: le stesse parole nel
/// turno in cui succede, nei rifiuti dopo, negli elenchi dei servizi, in `doctor`, all'avvio del REPL e nel setup.
/// </summary>
internal static class QuotaText
{
    /// <summary>La data come la scrive il resto del prodotto: aaaa-mm-gg, nell'ora di chi legge.</summary>
    internal static string Day(DateTimeOffset instant) =>
        instant.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>L'avviso completo: che cosa e' successo, fino a quando, come si riattiva.</summary>
    internal static string Paused(string name, DateTimeOffset? until) => until is DateTimeOffset date
        ? Ui.Format("quota.paused.until {0} {1}", name, Day(date))
        : Ui.Format("quota.paused.manual {0}", name);

    /// <summary>Lo stato breve, per un elenco di servizi.</summary>
    internal static string State(DateTimeOffset? until) => until is DateTimeOffset date
        ? Ui.Format("quota.state.until {0}", Day(date))
        : Ui.Text("quota.state.manual");

    /// <summary>L'avviso per una pausa letta dallo stato.</summary>
    internal static string Paused(string name, QuotaPause pause)
    {
        ArgumentNullException.ThrowIfNull(pause);
        return Paused(name, pause.Until);
    }

    /// <summary>
    /// Lo stato di pausa di ciascun servizio letto adesso dalla directory dati, per le rese che elencano servizi o rifiuti;
    /// null per un servizio non in pausa.
    /// </summary>
    internal static Func<string, string?> StateOf(string dataDirectory)
    {
        IReadOnlyList<QuotaPause> pauses = QuotaPauses.Active(
            PA.Router.Infrastructure.Storage.StorePaths.For(dataDirectory), PA.Router.Infrastructure.SystemClock.Instance.UtcNow);

        return serviceId => pauses.FirstOrDefault(pause => string.Equals(pause.ServiceId, serviceId, StringComparison.Ordinal)) is QuotaPause pause
            ? State(pause.Until)
            : null;
    }

    /// <summary>La data di una fase «Quota» (`until=aaaa-mm-ggThh:mmZ` oppure `manual`).</summary>
    internal static DateTimeOffset? Until(string? wire) =>
        wire is not null && wire != "manual"
            && DateTimeOffset.TryParseExact(wire, "yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed
            : null;
}
