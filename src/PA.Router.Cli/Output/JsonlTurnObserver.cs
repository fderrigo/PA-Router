using System.Text.Json.Nodes;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Cli.Output;

/// <summary>
/// Gli eventi di un turno come righe JSON, una per riga (S64).
///
/// E' la seconda resa della **stessa** sequenza che il REPL disegna con `ReplTurnObserver`: l'evento
/// e' l'operazione, scritta una volta nel motore; il disegno e' di ciascun front end (INV-22). Non
/// esiste un secondo flusso di eventi per gli script, e non deve esistere: due flussi divergono, e
/// chi legge quello sbagliato vede un turno che non e' accaduto.
///
/// **Un consumatore automatico non deve leggere italiano.** Ogni riga porta un tipo stabile e campi
/// con nomi stabili; le frasi che il terminale mostra a una persona non compaiono qui.
///
/// Le righe vanno su **standard error**, mai su standard output: con <c>--json</c> lo standard
/// output contiene esattamente un documento, e mescolarvi eventi lo renderebbe illeggibile a chi lo
/// analizza. E' la stessa regola che vale gia' per l'avanzamento.
///
/// Sul contenuto vale il confine di sempre: l'unico evento che porta testo e' il blocco di risposta,
/// che e' **gia' passato dallo screening**. Argomenti di strumenti, risultati, credenziali e testo
/// della domanda non attraversano questo flusso.
/// </summary>
/// <param name="writer">Dove scrivere le righe: lo standard error del comando.</param>
public sealed class JsonlTurnObserver(TextWriter writer) : ITurnObserver
{
    /// <summary>Versione dello schema delle righe, dichiarata su ognuna.</summary>
    public const int SchemaVersion = 1;

    private readonly Lock gate = new();

    /// <inheritdoc />
    public void Observe(TurnEvent turnEvent)
    {
        ArgumentNullException.ThrowIfNull(turnEvent);

        if (Describe(turnEvent) is not JsonObject line)
        {
            // Un evento che questa versione non conosce non produce una riga inventata: chi legge
            // deve poter contare sul fatto che ogni riga abbia un tipo dichiarato.
            return;
        }

        line["schemaVersion"] = SchemaVersion;

        // Il turno puo' emettere da piu' thread: due righe intrecciate non sarebbero JSONL.
        lock (gate)
        {
            writer.WriteLine(line.ToJsonString());
        }
    }

    /// <summary>
    /// La riga di un evento. I nomi dei campi sono il contratto: cambiarli e' una decisione di
    /// versione, non un dettaglio di resa.
    /// </summary>
    private static JsonObject? Describe(TurnEvent turnEvent) => turnEvent switch
    {
        TurnPhaseStarted started => new JsonObject
        {
            ["type"] = "phase_started",
            ["phase"] = started.Phase,
        },

        TurnPhaseEnded ended => new JsonObject
        {
            ["type"] = "phase_ended",
            ["phase"] = ended.Phase,
            ["outcome"] = ended.Outcome,
            ["elapsedMs"] = (long)ended.Elapsed.TotalMilliseconds,
        },

        TurnToolCalled tool => new JsonObject
        {
            ["type"] = "tool_called",
            ["tool"] = tool.Tool,
            ["source"] = tool.Source,
            ["round"] = tool.Round,
        },

        // Null significa **non misurato**, zero significa misurato zero: la distinzione vale anche
        // qui, quindi i campi assenti restano assenti invece di diventare zeri inventati.
        TurnUsageObserved usage => new JsonObject
        {
            ["type"] = "usage_observed",
            ["inputTokens"] = usage.Usage.InputTokens,
            ["outputTokens"] = usage.Usage.OutputTokens,
            ["billedCost"] = usage.Usage.BilledCost,
            ["pricedCost"] = usage.Usage.PricedCost,
            ["currency"] = usage.Usage.Currency,
        },

        // Il percorso lo **dichiara** il programma, non lo osserva il router: il campo dice quale
        // delle due cose e', perche' un elenco di file toccati e' un'affermazione dell'agente.
        TurnArtifactChanged artifact => new JsonObject
        {
            ["type"] = "artifact_declared_changed",
            ["path"] = artifact.RelativePath,
        },

        // L'unico evento con testo, e solo perche' quel testo e' gia' stato esaminato.
        TurnBlockReleased released => new JsonObject
        {
            ["type"] = "block_released",
            ["text"] = released.Text,
        },

        _ => null,
    };
}
