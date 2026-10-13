using System.Globalization;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Localization;

namespace PA.Router.Cli.Repl;

/// <summary>
/// Disegno di un rifiuto: la difficolta', poi un servizio per riga con **il numero che ha deciso**.
///
/// Prima erano ventisette righe per tre servizi: cinque motivi ciascuno, ognuno con il suo rimedio
/// ripetuto per intero, e i numeri da nessuna parte. Il peso visivo era proporzionale al numero di
/// servizi accesi, non all'importanza — e l'unica cosa che l'operatore poteva cambiare, la
/// difficolta' che aveva alzato le soglie, non compariva affatto.
///
/// Qui: la difficolta' per prima, perche' cinque motivi su tre servizi sono quasi sempre **una
/// causa sola** vista da tre parti; un servizio ausiliario in una riga, perche' «non risponde alle
/// richieste» e' l'unica cosa vera da dirne; i rimedi una volta in fondo, non per servizio.
/// </summary>
internal static class ReplDenialView
{
    /// <summary>Colonna dei valori, perche' i numeri stiano incolonnati e si confrontino a occhio.</summary>
    private const int NameColumn = 34;

    /// <summary>
    /// Scrive il rifiuto. Con <paramref name="verbose"/> ogni motivo di ogni servizio con i suoi
    /// parametri; senza, la sola cosa che decide per ciascuno.
    /// </summary>
    /// <param name="renderer">Resa da usare.</param>
    /// <param name="denial">Rifiuto con i suoi numeri, costruito dal motore.</param>
    /// <param name="verbose">Vero per ogni motivo con i suoi parametri.</param>
    /// <param name="nameOf">
    /// Come si chiama un servizio per una persona. Il motore porta l'identificatore, che e' opaco e
    /// stabile apposta; il nome lo conosce il front end, perche' e' localizzato e perche' dipende dal
    /// nome che l'operatore ha dato all'istanza.
    /// </param>
    /// <param name="interactive">
    /// Falso fuori dal REPL (`pa run`): Ctrl+O e /why non esistono, e i suggerimenti rimandano a `--json`.
    /// </param>
    /// <param name="quotaOf">Stato di pausa per quota finita di un servizio (<see cref="Output.QuotaText.StateOf"/>).</param>
    internal static void Write(IReplRenderer renderer, DenialDetail denial, bool verbose, Func<string, string>? nameOf = null, bool interactive = true, Func<string, string?>? quotaOf = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(denial);

        string Named(ServiceDenial service) => nameOf?.Invoke(service.ServiceId) ?? service.Name;

        if (denial.Difficulty is string difficulty)
        {
            // Dal 24 settembre 2026 (ADR-032, fase 3) la difficolta' non pretende un voto: si dice con la sola finestra.
            renderer.Warn("  " + (denial.MinContextTokens is int window
                ? Ui.Format("deny.difficulty {0} {1}", Ui.Text("band." + difficulty), window.ToString(CultureInfo.InvariantCulture))
                : Ui.Format("deny.difficultyOnly {0}", Ui.Text("band." + difficulty))));
        }

        foreach (ServiceDenial service in denial.Services.Where(item => !item.Disabled))
        {
            if (!service.AnswersRequests)
            {
                // Un embedding o un rilevatore non risponde a una richiesta: elencargli cinque
                // motivi di policy e' vero e inutile, ed era meta' del muro.
                renderer.Warn("  " + Pad(Named(service)) + Ui.Text("deny.auxiliary"));
                continue;
            }

            if (service.Facts.Count == 0)
            {
                renderer.Warn("  " + Pad(Named(service)) + Ui.Text("deny.unknown"));
                continue;
            }

            IReadOnlyList<DenialFact> shown = verbose ? service.Facts : Decisive(service.Facts);
            bool first = true;

            foreach (DenialFact fact in shown)
            {
                // Un servizio in pausa per quota finita dice fino a quando, non solo «ha finito la quota» (24 settembre 2026).
                string said = fact.Code == "QUOTA_EXHAUSTED" && quotaOf?.Invoke(service.ServiceId) is string paused
                    ? paused + "  (" + fact.Code + ")"
                    : Fact(fact);

                renderer.Warn("  " + Pad(first ? Named(service) : string.Empty) + said);
                first = false;
            }

            int hidden = service.Facts.Count - shown.Count;

            if (hidden > 0)
            {
                renderer.Info("  " + Pad(string.Empty) + Ui.Format(interactive ? "deny.more {0}" : "deny.moreJson {0}", hidden.ToString(CultureInfo.InvariantCulture)));
            }
        }

        string[] off = [.. denial.Services.Where(service => service.Disabled).Select(Named)];

        if (off.Length > 0)
        {
            renderer.Info("  " + Ui.Format("deny.disabled {0}", string.Join(", ", off)));
        }

        Remedies(renderer, denial, verbose, interactive);
    }

    /// <summary>
    /// I motivi che l'operatore puo' togliere, una volta ciascuno e non per servizio. Un rimedio
    /// ripetuto tre volte non e' tre volte piu' utile: e' tre volte piu' lungo.
    /// </summary>
    private static void Remedies(IReplRenderer renderer, DenialDetail denial, bool verbose, bool interactive)
    {
        string[] codes =
        [
            .. denial.Services
                .Where(service => !service.Disabled && service.AnswersRequests)
                .SelectMany(service => verbose ? service.Facts : Decisive(service.Facts))
                .Select(fact => fact.Code)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        foreach (string code in codes)
        {
            string key = "remedy." + code;

            if (Ui.Contains(key)) { renderer.Info("  " + Ui.Text(key)); }
        }

        renderer.Info("  " + Ui.Text(!interactive ? "deny.jsonHint" : verbose ? "deny.compactHint" : "deny.verboseHint"));
    }

    /// <summary>
    /// Il motivo che decide: se fosse l'unico, toglierlo basterebbe. Prima la finestra, poi controlli e capacita'. Il
    /// resto si conta e si offre.
    /// </summary>
    private static IReadOnlyList<DenialFact> Decisive(IReadOnlyList<DenialFact> facts)
    {
        string[] order =
        [
            PolicyReason.ContextWindowInsufficient.ToWire(),
            PolicyReason.ControlUnavailable.ToWire(),
            PolicyReason.CapabilityMissing.ToWire(),
        ];

        foreach (string code in order)
        {
            if (facts.FirstOrDefault(fact => fact.Code == code) is DenialFact found) { return [found]; }
        }

        return [facts[0]];
    }

    /// <summary>Il fatto in una riga: che cosa c'e' e che cosa servirebbe, poi il codice.</summary>
    private static string Fact(DenialFact fact)
    {
        // Un servizio di terzi senza prezzo non ha una cifra da confrontare con il tetto di spesa (ADR-032, fase 5): si dice
        // perche' resta fuori, non «-».
        if (fact.Code == PolicyReason.CostLimit.ToWire() && fact.Observed is null && fact.Required is string cap)
        {
            return Ui.Format("fact.COST_LIMIT.unknown {0}", cap) + "  (" + fact.Code + ")";
        }

        string key = "fact." + fact.Code + (fact.Required is null ? fact.Observed is null ? string.Empty : " {0}" : " {0} {1}");

        // Una zona di fiducia si legge con il suo nome (ADR-029); gli altri valori sono gia' numeri o nomi.
        string observed = ReplConfirmation.Zone(fact.Observed);
        string? required = fact.Required is null ? null : ReplConfirmation.Zone(fact.Required);

        string text = Ui.Contains(key)
            ? required is not null
                ? Ui.Format(key, fact.Observed is null ? "-" : observed, required)
                : fact.Observed is not null ? Ui.Format(key, observed) : Ui.Text(key)
            // Un motivo senza una frase con i numeri si dice con la frase breve del racconto, che esiste per ogni motivo
            // (una prova lo pretende): «un motivo che questa schermata non sa spiegare» per un servizio escluso da
            // `--target` o dall'elenco dell'etichetta non diceva niente (24 settembre 2026).
            : Ui.Contains("narr.why." + fact.Code) ? Ui.Text("narr.why." + fact.Code) : Ui.Text("deny.unknown");

        return text + "  (" + fact.Code + ")";
    }

    private static string Pad(string name) =>
        name.Length >= NameColumn ? name + " " : name.PadRight(NameColumn);
}
