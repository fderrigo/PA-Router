using System.Globalization;
using System.Text;

namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Codici degli eventi di audit (03-specs/STORAGE-AUDIT.md, «Eventi minimi»).
///
/// L'insieme e' chiuso. Un evento che non compare qui non si scrive: l'audit serve a ricostruire
/// una decisione, e un registro con voci arbitrarie non e' ricostruibile.
/// </summary>
public static class AuditCodes
{
    /// <summary>La richiesta e' stata accettata e ha un identificatore.</summary>
    public const string RequestAccepted = "request_accepted";

    /// <summary>L'analisi privacy ha prodotto una classe e una completezza.</summary>
    public const string PrivacyAnalyzed = "privacy_analyzed";

    /// <summary>I candidati sono stati valutati dalla policy.</summary>
    public const string CandidateEvaluated = "candidate_evaluated";

    /// <summary>Il ranking ha scelto un servizio fra gli ammessi.</summary>
    public const string RouteSelected = "route_selected";

    /// <summary>Intenzione di dispatch, scritta prima di contattare l'adapter.</summary>
    public const string AttemptDispatchIntent = "attempt_dispatch_intent";

    /// <summary>Il tentativo e' partito.</summary>
    public const string AttemptStarted = "attempt_started";

    /// <summary>Un secondo tentativo e' stato considerato dopo un fallimento.</summary>
    public const string FallbackConsidered = "fallback_considered";

    /// <summary>Stato terminale dell'esecuzione; il suffisso e' lo stato in forma wire.</summary>
    public const string ExecutionPrefix = "execution_";

    /// <summary>Un'incertezza amministrativa e' stata chiusa dall'operatore.</summary>
    public const string RecoveryRecorded = "recovery_recorded";

    /// <summary>La manutenzione ha applicato la retention.</summary>
    public const string RetentionApplied = "retention_applied";

    /// <summary>
    /// Un handover ha consegnato una capsule a un altro servizio. La riga appartiene
    /// all'esecuzione di **destinazione**: i metadata portano l'origine e il digest della capsule,
    /// che sono cio' che non si ricava dalla riga stessa.
    /// </summary>
    public const string HandoverRecorded = "handover_recorded";

    /// <summary>
    /// Una chiamata a uno strumento sta per partire (ADR-020). Si scrive prima della chiamata, come
    /// l'intenzione di dispatch: se il processo muore durante la chiamata, questa riga dice che e' partita.
    /// </summary>
    public const string ToolCallStarted = "tool_call_started";

    /// <summary>Una chiamata e' tornata, con la classe del risultato o il codice del guasto.</summary>
    public const string ToolCallCompleted = "tool_call_completed";

    /// <summary>Una chiamata o un risultato non sono stati ammessi, con il motivo.</summary>
    public const string ToolCallDenied = "tool_call_denied";

    /// <summary>Tutti i codici ammessi, per le verifiche e per le diagnosi.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        RequestAccepted,
        PrivacyAnalyzed,
        CandidateEvaluated,
        RouteSelected,
        AttemptDispatchIntent,
        AttemptStarted,
        FallbackConsidered,
        RecoveryRecorded,
        RetentionApplied,
        HandoverRecorded,
        ToolCallStarted,
        ToolCallCompleted,
        ToolCallDenied,
        ExecutionPrefix + "completed",
        ExecutionPrefix + "failed",
        ExecutionPrefix + "denied",
        ExecutionPrefix + "cancelled",
        ExecutionPrefix + "uncertain",
    };

    /// <summary>Vero se il codice appartiene all'insieme chiuso.</summary>
    public static bool IsKnown(string code) => All.Contains(code);
}

/// <summary>
/// Metadata di un evento di audit, con allowlist chiusa delle chiavi.
///
/// Una riga di audit non contiene input, output, prompt, percorsi, credenziali o eventi nativi. Le
/// chiavi ammesse sono poche e note: aggiungerne una «solo per capire meglio» e' il modo in cui un
/// registro diventa un secondo archivio dei contenuti.
/// </summary>
public static class AuditMetadata
{
    /// <summary>Chiavi ammesse nei metadata di un evento.</summary>
    public static IReadOnlySet<string> AllowedKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "targetId",
        "capability",
        "ordinal",
        "errorCode",
        "dataClass",
        "certainty",
        "candidates",
        "admissible",
        "reason",
        "algorithmVersion",
        "tier",
        "score",
        "attempt",
        "state",
        "resolution",

        // Handover: l'esecuzione di origine e il digest della capsule consegnata. Sono
        // identificatori di configurazione e un digest, non contenuti: la capsule resta nel blob
        // cifrato e nell'audit non ne compare una riga.
        "sourceExecutionId",
        "capsuleDigest",

        // Istruzioni dell'applicazione (ADR-020): il digest dice quali istruzioni hanno governato il
        // turno senza conservarne una parola.
        "instructionsDigest",
        "payloadDigest",
        "grantNonceDigest",

        // Strumenti (ADR-020): nome dello strumento, sorgente e giro. Sono identificatori, non argomenti
        // ne' risultati.
        "tool",
        "toolSource",
        "round",

        // Zone (ADR-029): la zona del servizio scelto e se il turno e' uscito dal perimetro, e per quale regola.
        // L'uscita di un allegato si registra sempre.
        "zone",
        "perimeter",
    };

    /// <summary>
    /// Serializza i metadata in JSON. Una chiave fuori allowlist e' un difetto del chiamante e
    /// viene rifiutata subito, non filtrata in silenzio.
    /// </summary>
    public static string ToJson(params (string Key, string Value)[] pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        StringBuilder builder = new("{");

        for (int index = 0; index < pairs.Length; index++)
        {
            (string key, string value) = pairs[index];

            if (!AllowedKeys.Contains(key))
            {
                throw new ArgumentOutOfRangeException(nameof(pairs), "The audit metadata key is not allowed: " + key);
            }

            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append('"').Append(key).Append("\":\"").Append(Sanitize(value)).Append('"');
        }

        return builder.Append('}').ToString();
    }

    /// <summary>Numero in forma invariante, per i metadata che contano invece di descrivere.</summary>
    public static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Valore safe: niente virgolette, niente caratteri di controllo, lunghezza limitata. Un
    /// valore lungo in un audit e' quasi sempre un contenuto finito dove non doveva.
    /// </summary>
    private static string Sanitize(string value)
    {
        StringBuilder builder = new(Math.Min(value.Length, 128));

        foreach (char character in value)
        {
            if (builder.Length == 128)
            {
                break;
            }

            if (character is '"' or '\\' || char.IsControl(character))
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Evento di audit fuori dalle tre transazioni principali: analisi, valutazione, selezione,
/// avvio e ripiego.
/// </summary>
/// <param name="EventId">Identificatore dell'evento.</param>
/// <param name="ExecutionId">Esecuzione a cui appartiene.</param>
/// <param name="AttemptId">Tentativo, quando l'evento ne ha uno.</param>
/// <param name="Code">Codice dell'evento, fra quelli di <see cref="AuditCodes"/>.</param>
/// <param name="MetadataJson">Metadata gia' serializzati da <see cref="AuditMetadata"/>.</param>
public sealed record AuditRecord(
    string EventId,
    string ExecutionId,
    string? AttemptId,
    string Code,
    string MetadataJson);
