using System.Globalization;
using System.Text.Json;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Localization;
using PA.Router.Cli.Repl;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Output;

/// <summary>
/// La traccia di audit di un'esecuzione scritta per una persona (24 settembre 2026). Prima `pa audit show` stampava i codici
/// degli eventi («attempt_dispatch_intent»), gli identificatori dei servizi e i motivi come array JSON su piu' righe. Qui:
/// che cosa e' successo in ordine, i tentativi e i candidati con il nome del servizio, i motivi con le frasi del racconto
/// del turno. I codici restano in `--json`.
/// </summary>
internal static class AuditText
{
    /// <param name="trail">Traccia letta.</param>
    /// <param name="snapshot">Configurazione, per i nomi dei servizi; null se non si legge.</param>
    /// <param name="line">Dove scrivere una riga.</param>
    internal static void Write(AuditTrail trail, ConfigurationSnapshot? snapshot, Action<string> line)
    {
        ArgumentNullException.ThrowIfNull(trail);
        ArgumentNullException.ThrowIfNull(line);

        string Named(string id) => snapshot is null ? id : ServiceNames.Display(snapshot, id);

        // Embedding e rilevatori lavorano per il router e non rispondono: i motivi di policy che raccolgono sono veri e
        // inutili (S116), quindi si dicono in una riga sola, come nel racconto del turno.
        bool Helper(string id) => snapshot?.FindService(id) is ServiceDescriptor service && !ServiceSetupCatalog.AnswersRequests(service.Capability);
        string Phrase(string prefix, string? code) =>
            code is null ? "-" : Ui.Contains(prefix + code) ? Ui.Text(prefix + code) : code;

        line(Ui.Format("audit.title {0}", trail.ExecutionId));
        line(string.Empty);
        line(Ui.Text("audit.happened"));

        foreach (AuditEntry entry in trail.Events)
        {
            line("  · " + Phrase("audit.event.", entry.Code));
        }

        if (trail.Attempts.Count > 0)
        {
            line(string.Empty);
            line(Ui.Text("audit.attempts"));

            foreach (AuditAttempt attempt in trail.Attempts)
            {
                line("  " + attempt.Ordinal.ToString(Formats.Invariant) + ". " + Named(attempt.TargetId) + " — "
                    + Phrase("audit.state.", attempt.State)
                    + (attempt.ErrorCode is string code ? " (" + Phrase("narr.attempt.", code) + ")" : string.Empty)
                    + " · " + Phrase("audit.effects.", attempt.SideEffectState));
            }
        }

        foreach (IGrouping<int, AuditDecision> round in trail.Decisions.GroupBy(decision => decision.Ordinal).OrderBy(group => group.Key))
        {
            line(string.Empty);

            // L'ordinale 0 e' una decisione presa prima di ogni tentativo, cioe' il turno negato (S83).
            line(round.Key == 0
                ? Ui.Text("audit.candidatesBefore")
                : Ui.Format("audit.candidates {0}", round.Key.ToString(Formats.Invariant)));

            foreach (AuditDecision decision in round.Where(decision => decision.Allowed).OrderBy(decision => Named(decision.TargetId), StringComparer.CurrentCultureIgnoreCase))
            {
                line("  ● " + Named(decision.TargetId) + " — " + (Score(decision.ScoreJson) is string score
                    ? Ui.Format("audit.admittedScore {0}", score)
                    : Ui.Text("audit.admitted")));
            }

            string[] disabled = [.. round
                .Where(decision => !decision.Allowed && Reasons(decision.ReasonsJson).Contains("TARGET_DISABLED"))
                .Select(decision => Named(decision.TargetId))
                .Order(StringComparer.CurrentCultureIgnoreCase)];

            foreach (AuditDecision decision in round
                .Where(decision => !decision.Allowed && !Helper(decision.TargetId) && !Reasons(decision.ReasonsJson).Contains("TARGET_DISABLED"))
                .OrderBy(decision => Named(decision.TargetId), StringComparer.CurrentCultureIgnoreCase))
            {
                line("  ○ " + Named(decision.TargetId) + " — "
                    + string.Join("; ", Reasons(decision.ReasonsJson).Select(reason => Phrase("narr.why.", reason))));
            }

            if (disabled.Length > 0) { line("  " + Ui.Format("narr.disabled {0}", string.Join(", ", disabled))); }

            string[] helpers = [.. round
                .Where(decision => Helper(decision.TargetId) && !Reasons(decision.ReasonsJson).Contains("TARGET_DISABLED"))
                .Select(decision => Named(decision.TargetId))
                .Order(StringComparer.CurrentCultureIgnoreCase)];

            if (helpers.Length > 0) { line("  " + Ui.Format("narr.auxiliary {0}", string.Join(", ", helpers))); }
        }

        line(string.Empty);
        line(Ui.Text("audit.hint"));
    }

    private static string[] Reasons(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind is JsonValueKind.Array
                ? [.. document.RootElement.EnumerateArray().Where(item => item.ValueKind is JsonValueKind.String).Select(item => item.GetString()!)]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Il punteggio in decimi, come lo mostra il racconto; null se non c'e'.</summary>
    private static string? Score(string? json)
    {
        if (json is null) { return null; }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("score", out JsonElement score) && score.TryGetDouble(out double value)
                ? (value * 10).ToString("0.#", CultureInfo.InvariantCulture)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
