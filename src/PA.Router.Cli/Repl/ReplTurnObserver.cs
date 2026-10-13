using System.Text;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Output;

namespace PA.Router.Cli.Repl;

/// <summary>
/// Chi guarda un turno e lo disegna nel REPL (S58).
///
/// E' la meta' front-end di [ADR-021](../../../docs/02-architecture/adr/ADR-021-terminale-moderno.md):
/// il motore emette eventi, questo oggetto decide come si vedono. L'evento e' l'operazione, scritta
/// una volta in <c>PA.Router.Hosting</c>; il disegno e' di ciascun front end (INV-22).
///
/// Il testo che arriva qui **e' gia' passato dallo screening**: un <see cref="TurnBlockReleased"/>
/// esiste solo per il testo ammesso dalla regola di rilascio di 03-specs/PRIVACY.md. Resta comunque
/// testo di un fornitore, quindi viene mostrato senza interpretarlo, come ogni altra risposta.
/// </summary>
/// <param name="renderer">Resa del REPL.</param>
internal sealed class ReplTurnObserver(IReplRenderer renderer, ReplUsageTotals? totals = null, Func<string, string?>? modelOf = null) : ITurnObserver
{
    private readonly StringBuilder shown = new();
    private readonly List<string> changed = [];
    private readonly List<(string Phase, string Outcome, TimeSpan Elapsed)> phases = [];
    private readonly long started = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>File che il programma dichiara di aver toccato in questo turno, in ordine d'arrivo.</summary>
    internal IReadOnlyList<string> Changed => changed;

    /// <summary>Vero se almeno un blocco e' gia' comparso: la coda non va ristampata per intero.</summary>
    internal bool ShownAnything => shown.Length > 0;

    /// <summary>
    /// Servizio scelto dal routing in questo turno, osservato dalla fase che lo ha scelto. La barra
    /// di stato dice chi **ha** risposto: e' un fatto, e arriva da qui.
    /// </summary>
    internal string? ChosenServiceId { get; private set; }

    /// <summary>
    /// Le fasi del turno, gia' rese leggibili, nell'ordine in cui sono arrivate. Durante il turno
    /// vivono in una riga sola che si riscrive; questa e' la stessa cosa in forma che resta.
    /// </summary>
    internal IReadOnlyList<(string Phase, string Detail)> Rendered =>
        [.. phases.Select(phase => (
            Ui.Text(phase.Phase),
            Outcome(phase.Phase, phase.Outcome) + " (" + Duration(phase.Elapsed) + ")"))];

    /// <summary>Testo gia' mostrato a blocchi, per non farlo rileggere due volte.</summary>
    internal string Shown => shown.ToString();

    /// <summary>
    /// Vero se le fasi si scrivono mentre arrivano. Senza lo schermo del REPL — un copione, una pipe — sono l'unico
    /// segno di avanzamento e vanno su standard error, dove non disturbano; nello schermo del REPL no (S117): li'
    /// erano righe blu che scorrevano e sparivano, e le sostituiscono il pallino d'attesa e il racconto.
    /// </summary>
    internal bool ShowPhases { get; init; }

    /// <summary>Vero se il consumo si scrive quando arriva; nel livello verboso lo dice il racconto.</summary>
    internal bool ShowUsage { get; init; } = true;

    /// <summary>Le fasi come le ha dichiarate il motore: il racconto del turno le legge da qui (S117).</summary>
    internal IReadOnlyList<(string Phase, string Outcome, TimeSpan Elapsed)> Phases => phases;

    /// <summary>L'ultimo consumo dichiarato dal servizio in questo turno, se lo ha dichiarato.</summary>
    internal UsageReport? Usage { get; private set; }

    /// <summary>Tempo del turno visto da questo terminale.</summary>
    internal TimeSpan Elapsed => System.Diagnostics.Stopwatch.GetElapsedTime(started);

    /// <summary>Le coppie <c>chiave=valore</c> di un esito, come le scrive il motore.</summary>
    internal static IReadOnlyDictionary<string, string> Values(string outcome) =>
        outcome.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).Where(pair => pair.Length == 2)
            .GroupBy(pair => pair[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First()[1], StringComparer.Ordinal);

    /// <summary>L'esito di una fase in parole, come lo scriveva la riga delle fasi.</summary>
    internal string Describe(string phase, string outcome) => Outcome(phase, outcome);

    /// <inheritdoc />
    public void Observe(TurnEvent turnEvent)
    {
        ArgumentNullException.ThrowIfNull(turnEvent);

        switch (turnEvent)
        {
            case TurnBlockReleased released:
                shown.Append(released.Text);
                renderer.Answer(released.Text);
                break;

            case TurnPhaseEnded ended:
                // Le fasi non si disegnano piu' mentre il turno gira (S117): l'operatore le leggeva come
                // righe che scorrevano e sparivano. Si conservano per il racconto del livello verboso, e
                // l'esito si calcola subito perche' registra il servizio scelto.
                phases.Add((ended.Phase, ended.Outcome, ended.Elapsed));
                string described = Outcome(ended.Phase, ended.Outcome);
                if (ShowPhases) { renderer.Phase(Ui.Text(ended.Phase) + ": " + described + " (" + Duration(ended.Elapsed) + ")"); }

                // Un servizio andato in pausa per quota finita si dice subito, a ogni livello (24 settembre 2026): da qui in
                // avanti il router non lo usa, e l'operatore deve saperlo adesso, non scoprirlo dalle risposte.
                if (ended.Phase == "Quota")
                {
                    IReadOnlyDictionary<string, string> quota = Values(ended.Outcome);
                    string service = quota.GetValueOrDefault("service", "?");
                    renderer.Warn(Output.QuotaText.Paused(modelOf?.Invoke(service) ?? service, Output.QuotaText.Until(quota.GetValueOrDefault("until"))));
                }
                break;

            case TurnToolCalled tool:
                renderer.Event(
                    Ui.Text("Strumento ") + tool.Tool + " (" + tool.Source + Ui.Text(", giro ")
                    + tool.Round.ToString(Formats.Invariant) + ")");
                break;

            case TurnUsageObserved usage:
                // Il totale della sessione si somma qui, dagli eventi: il router non stima nulla e
                // non tiene una contabilita' propria accanto a quella dichiarata dai servizi.
                totals?.Add(usage.Usage);
                Usage = usage.Usage;
                if (ShowUsage) { renderer.Event(Describe(usage.Usage)); }
                break;

            case TurnArtifactChanged artifact:
                // Dichiarato dal programma, non osservato dal router: la parola «dichiara» ci sta
                // apposta, perche' un elenco di file toccati e' una affermazione dell'agente.
                changed.Add(artifact.RelativePath);
                renderer.Event(Ui.Text("File dichiarato modificato: ") + artifact.RelativePath);
                break;

            case TurnPhaseStarted started:
                // Nello schermo del REPL che il turno sia in corso lo dice il pallino sotto la conversazione (S117).
                if (ShowPhases) { renderer.Phase(started.Phase); }
                break;

            default:
                // Un evento nuovo non deve rompere una resa vecchia.
                break;
        }
    }

    private static string Duration(TimeSpan elapsed) => elapsed.TotalSeconds >= 1
        ? elapsed.TotalSeconds.ToString("0.0", Formats.Invariant) + " s"
        : elapsed.TotalMilliseconds.ToString("0", Formats.Invariant) + " ms";

    /// <summary>
    /// L'esito di una fase, detto per cio' che significa e con i numeri che lo giustificano. I valori sono quelli
    /// che il motore ha calcolato: qui si scrivono, non si ricalcolano. Il nome del modello viene dal registro.
    /// </summary>
    private string Outcome(string phase, string outcome)
    {
        Dictionary<string, string> values = outcome.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).Where(pair => pair.Length == 2)
            .GroupBy(pair => pair[0], StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First()[1], StringComparer.Ordinal);
        string Value(string key) => values.GetValueOrDefault(key, "?");

        switch (phase)
        {
            case "Analisi locale" when values.Count > 0:
                return Ui.Format("turn.analysis {0} {1}", Ui.Text("class." + Value("class")), Ui.Text("certainty." + Value("certainty")))
                    // La difficolta' e' la causa delle soglie: si dice dove viene decisa, non solo
                    // dove morde.
                    + (values.ContainsKey("difficulty")
                        ? " · " + Ui.Format("turn.difficulty {0}", Ui.Text("band." + Value("difficulty")))
                        : string.Empty);

            case "Policy" when values.Count > 0:
                // «1 servizi su 10» non lo scrive nessuno. Dal 24 settembre 2026 (ADR-032, fase 3) la sola soglia e' la
                // finestra: nessun voto minimo.
                string admissible = Value("admissible");
                string band = values.ContainsKey("minContext") ? Ui.Format("turn.bandFree {0}", Value("minContext")) : Ui.Text("turn.noBand");

                return (admissible switch
                {
                    "0" => Ui.Format("turn.policyNone {0}", Value("total")),
                    "1" => Ui.Format("turn.policyOne {0}", Value("total")),
                    _ => Ui.Format("turn.policy {0} {1}", admissible, Value("total")),
                }) + " " + band;

            case "Selezione target" when Value("service") == "-":
                return Ui.Text("turn.noTarget");

            case "Selezione target" when values.Count > 0:
                string id = Value("service");
                // Il nome, non l'identificatore: quello resta opaco apposta e non si mostra (S117).
                string model = modelOf?.Invoke(id) is { Length: > 0 } name ? name : id;
                ChosenServiceId = id;
                // In decimi e i pesi in percentuale (S116, S117). La qualita' viene dall'ordine dei modelli (ADR-032, fase 3).
                string Part(string name, string component, string weight) =>
                    Ui.Text(name) + " " + Tenths(Value(component)) + " (" + Percent(Value(weight)) + "%)";
                string text = Ui.Format("turn.target {0}", model) + " "
                    + Ui.Format("turn.score {0}", Tenths(Value("score")) + "/10") + " = "
                    + Part("turn.quality", "quality", "wQuality") + " + "
                    + Part("turn.latency", "latency", "wLatency") + " + "
                    + Part("turn.cost", "cost", "wCost") + " + "
                    + Part("turn.quota", "quota", "wQuota")
                    + " [" + Value("algorithm") + "]. ";
                if (values.TryGetValue("next", out string? next))
                {
                    text += Ui.Format("turn.others {0}", string.Join(", ", next.Split(',').Select(item =>
                    {
                        string[] parts = item.Split(':', 2);
                        string other = modelOf?.Invoke(parts[0]) is { Length: > 0 } otherName ? otherName : parts[0];
                        return other + " " + (parts.Length > 1 ? Tenths(parts[1]) : "?");
                    })));
                }
                else { text += Ui.Text("turn.onlyOne"); }
                return text + (values.ContainsKey("tie") ? " " + Ui.Text("turn.tie") : string.Empty);

            case "Esecuzione":
                return outcome == "ok" ? Ui.Text("turn.executed") : Ui.Format("turn.stopped {0}", outcome);

            default:
                return Ui.Text(outcome);
        }
    }

    /// <summary>Una soglia fra 0 e 1 detta come il voto da 0 a 10 che l'operatore assegna.</summary>
    private static string Percent(string weight) => double.TryParse(weight, System.Globalization.NumberStyles.Float, Formats.Invariant, out double parsed)
        ? (parsed * 100).ToString("0", Formats.Invariant) : weight;

    private static string Tenths(string value) => double.TryParse(value, System.Globalization.NumberStyles.Float, Formats.Invariant, out double parsed)
        ? (parsed * 10).ToString("0.#", Formats.Invariant) : value;

    /// <summary>Consumo dichiarato dal provider. Null significa non misurato, e si dice.</summary>
    private static string Describe(UsageReport usage)
    {
        List<string> parts = [];

        if (usage.InputTokens is long input)
        {
            parts.Add(input.ToString(Formats.Invariant) + Ui.Text(" token in ingresso"));
        }

        if (usage.OutputTokens is long output)
        {
            parts.Add(output.ToString(Formats.Invariant) + Ui.Text(" in uscita"));
        }

        if (usage.BilledCost is decimal cost)
        {
            parts.Add(Ui.Text("costo dichiarato ") + cost.ToString("0.####", Formats.Invariant));
        }
        else if (usage.PricedCost is decimal priced)
        {
            parts.Add(Ui.Format("usage.priced {0} {1}", priced.ToString("0.######", Formats.Invariant), usage.Currency ?? string.Empty));
        }

        if (usage.Truncated)
        {
            parts.Add(Ui.Text("usage.truncated"));
        }

        return parts.Count == 0
            ? Ui.Text("Consumo non dichiarato dal servizio.")
            : Ui.Text("Consumo: ") + string.Join(", ", parts);
    }
}
