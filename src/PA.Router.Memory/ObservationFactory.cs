using System.Globalization;
using System.Text;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Memory;

/// <summary>Evento normalizzato da cui puo' nascere un ricordo.</summary>
/// <param name="ExecutionId">Esecuzione di origine, quando esiste.</param>
/// <param name="AdapterEventId">Identificatore dell'evento dell'adapter, quando l'adapter lo espone.</param>
/// <param name="Sequence">Numero d'ordine assegnato dal coordinatore quando l'evento non ha id.</param>
/// <param name="Kind">Tipo di osservazione.</param>
/// <param name="Text">Testo gia' filtrato dalla privacy.</param>
/// <param name="EvidenceRefs">Riferimenti a prove osservate dal runtime.</param>
/// <param name="ExitCodeObserved">Vero se il runtime ha osservato l'exit code del programma.</param>
public sealed record MemoryEvent(
    string? ExecutionId,
    string? AdapterEventId,
    long Sequence,
    ObservationKind Kind,
    string Text,
    IReadOnlyList<string> EvidenceRefs,
    bool ExitCodeObserved = false);

/// <summary>
/// Costruzione delle osservazioni (03-specs/MEMORY.md).
///
/// Due regole comandano qui. La prima: la verifica non si dichiara, si osserva — un esito di test
/// e' <c>verified</c> solo se il runtime ha visto exit code e riferimento all'evidenza, mai perche'
/// il modello ha scritto «i test passano». La seconda: il testo sintetico usa i campi osservati e
/// i riferimenti, e non riassume inferenze che nessuno ha visto; la riduzione e' un troncamento
/// dichiarato, non un riassunto inventato.
/// </summary>
public static class ObservationFactory
{
    /// <summary>Marcatore aggiunto quando il testo viene troncato al limite.</summary>
    public const string TruncationMarker = "\n[troncato al limite di osservazione]";

    /// <summary>Separatore fra chiave di evento e versione, nella deduplica.</summary>
    public const string VersionSeparator = "#v";

    /// <summary>
    /// Chiave di deduplica: esecuzione piu' identificatore dell'evento dell'adapter. Un evento
    /// senza identificatore usa il numero d'ordine del coordinatore, cosi' un replay dello stesso
    /// stream non produce un secondo ricordo.
    /// </summary>
    public static string EventKey(MemoryEvent source)
    {
        ArgumentNullException.ThrowIfNull(source);

        string execution = source.ExecutionId ?? "no-execution";
        string identifier = string.IsNullOrWhiteSpace(source.AdapterEventId)
            ? "seq-" + source.Sequence.ToString(CultureInfo.InvariantCulture)
            : Normalize(source.AdapterEventId);

        return execution + ":" + identifier;
    }

    /// <summary>
    /// Crea l'osservazione di prima versione. La classe del dato arriva dalla privacy e non e'
    /// negoziabile: lo scope viene ristretto se la classe non consente quello richiesto.
    /// </summary>
    public static ObservationRecord Create(
        string id,
        Guid projectId,
        string sessionId,
        MemoryEvent source,
        DataClass classification,
        MemoryScopeKind requestedScope,
        DateTimeOffset createdAt,
        TimeSpan retention,
        bool publishedForChatbot = false,
        MemoryDestination destination = MemoryDestination.Local)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(source);

        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), "Retention must be positive.");
        }

        MemoryAdmissionResult admission = MemoryAdmission.Evaluate(classification, requestedScope, publishedForChatbot);

        if (!admission.Admitted)
        {
            throw new PaRouterException(ErrorCode.MemoryScopeDenied, "This event cannot become a memory.")
                .WithMetadata("reason", admission.Reason ?? "not_admitted");
        }

        if (!MemoryRouting.IsAdmissible(destination, classification))
        {
            // Chi chiama ha gia' deciso la destinazione: qui si verifica, perche' una destinazione
            // sbagliata manderebbe fuori un dato che non puo' uscire, e sarebbe irreversibile.
            throw new PaRouterException(ErrorCode.MemoryScopeDenied, "That class of memory cannot leave the machine.")
                .WithMetadata("reason", "destination_not_allowed_for_class");
        }

        return new ObservationRecord(
            id,
            projectId,
            sessionId,
            source.ExecutionId,
            EventKey(source),
            source.Kind,
            Verify(source),
            classification,
            admission.Scope,
            publishedForChatbot,
            Version: 1,
            Content: SyntheticText(source),
            EvidenceRefs: [.. source.EvidenceRefs],
            Supersedes: null,
            CreatedAt: createdAt,
            ExpiresAt: createdAt + retention,
            Destination: destination);
    }

    /// <summary>
    /// Nuova versione di un'osservazione esistente.
    ///
    /// Non sovrascrive: crea un ricordo nuovo che dichiara quale sostituisce. La chiave di evento
    /// porta il numero di versione perche' <c>observations</c> impone una sola riga per chiave, e
    /// una conclusione aggiornata non deve cancellare in silenzio quella precedente.
    /// </summary>
    public static ObservationRecord Supersede(
        string id,
        ObservationRecord previous,
        MemoryEvent source,
        DataClass classification,
        DateTimeOffset createdAt,
        TimeSpan retention)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(source);

        MemoryAdmissionResult admission = MemoryAdmission.Evaluate(
            classification,
            previous.Scope,
            previous.PublishedForChatbot);

        if (!admission.Admitted)
        {
            throw new PaRouterException(ErrorCode.MemoryScopeDenied, "This conclusion cannot become a memory.")
                .WithMetadata("reason", admission.Reason ?? "not_admitted");
        }

        int version = previous.Version + 1;

        return previous with
        {
            Id = id,
            EventKey = BaseKey(previous.EventKey) + VersionSeparator + version.ToString(CultureInfo.InvariantCulture),
            Kind = source.Kind,
            Verification = Verify(source),
            Classification = classification,
            Scope = admission.Scope,
            Version = version,
            Content = SyntheticText(source),
            EvidenceRefs = [.. source.EvidenceRefs],
            Supersedes = previous.Id,
            CreatedAt = createdAt,
            ExpiresAt = createdAt + retention,

            // La destinazione si eredita, e non sale mai: una versione nuova di un ricordo locale
            // resta locale anche se adesso il testo risulterebbe pubblico. Scende invece subito se
            // la classe nuova non ammette piu' il servizio potenziato — la regola del «solo public
            // esce» vale a ogni versione, non solo alla prima.
            Destination = MemoryRouting.IsAdmissible(previous.Destination, classification)
                ? previous.Destination
                : MemoryDestination.Local,
        };
    }

    /// <summary>Chiave senza il suffisso di versione.</summary>
    public static string BaseKey(string eventKey)
    {
        ArgumentNullException.ThrowIfNull(eventKey);

        int marker = eventKey.LastIndexOf(VersionSeparator, StringComparison.Ordinal);

        return marker < 0 ? eventKey : eventKey[..marker];
    }

    /// <summary>
    /// Verifica osservata. Un esito di test vale <c>verified</c> solo con exit code osservato e
    /// almeno un riferimento all'evidenza; tutto il resto resta <c>unverified</c>.
    /// </summary>
    private static Verification Verify(MemoryEvent source) =>
        source is { Kind: ObservationKind.TestResult, ExitCodeObserved: true } && source.EvidenceRefs.Count > 0
            ? Verification.Verified
            : Verification.Unverified;

    /// <summary>Intestazione dei riferimenti in coda al testo sintetico.</summary>
    private const string EvidenceHeading = "\n\nProve osservate:";

    /// <summary>
    /// Il solo testo osservato di un'osservazione, senza il tipo in testa e senza i riferimenti in coda (23 settembre
    /// 2026). Serve a chi lo manda a un modello e a chi confronta due ricordi: gli identificatori delle esecuzioni sono
    /// token che il modello non usa — e che il rilevatore aveva scambiato per IBAN — e, diversi per ogni ricordo, facevano
    /// sembrare diverse due risposte uguali. Il tipo e l'identificatore stanno gia' nell'intestazione del ricordo.
    /// Conosce la forma perche' la scrive <see cref="SyntheticText"/>, qui accanto.
    /// </summary>
    public static string Statement(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string text = content;

        if (text.StartsWith('['))
        {
            int close = text.IndexOf("] ", StringComparison.Ordinal);
            if (close > 0 && close <= 32) { text = text[(close + 2)..]; }
        }

        int evidence = text.LastIndexOf(EvidenceHeading, StringComparison.Ordinal);

        // Si toglie solo una coda fatta di soli riferimenti: una risposta che contenga la stessa frase resta intera.
        if (evidence >= 0
            && text[(evidence + EvidenceHeading.Length)..].Split('\n', StringSplitOptions.RemoveEmptyEntries).All(line => line.StartsWith("- ", StringComparison.Ordinal)))
        {
            text = text[..evidence];
        }

        return text.Trim();
    }

    /// <summary>
    /// Testo sintetico deterministico: intestazione con i campi osservati, poi il testo gia'
    /// filtrato, poi i riferimenti. Nessuna sintesi del contenuto.
    /// </summary>
    private static string SyntheticText(MemoryEvent source)
    {
        StringBuilder builder = new();

        builder.Append('[').Append(source.Kind.ToWire()).Append("] ");
        builder.Append(source.Text.Trim());

        if (source.EvidenceRefs.Count > 0)
        {
            builder.Append(EvidenceHeading);

            foreach (string reference in source.EvidenceRefs)
            {
                builder.Append("\n- ").Append(reference);
            }
        }

        return Truncate(builder.ToString());
    }

    /// <summary>
    /// Troncamento dichiarato al limite di osservazione, su un confine di carattere: un ricordo
    /// tagliato deve dire di esserlo, altrimenti sembra un fatto completo.
    /// </summary>
    private static string Truncate(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= MemoryLimits.MaxObservationBytes)
        {
            return text;
        }

        int budget = MemoryLimits.MaxObservationBytes - Encoding.UTF8.GetByteCount(TruncationMarker);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int end = budget;

        while (end > 0 && (bytes[end] & 0xC0) == 0x80)
        {
            end--;
        }

        return Encoding.UTF8.GetString(bytes, 0, end) + TruncationMarker;
    }

    /// <summary>Normalizzazione dell'identificatore di evento: spazi e maiuscole non distinguono.</summary>
    private static string Normalize(string adapterEventId) =>
        adapterEventId.Trim().ToLowerInvariant();
}
