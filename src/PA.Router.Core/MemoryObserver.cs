using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Abstractions.Observability;
using PA.Router.Memory;
using PA.Router.Privacy;

namespace PA.Router.Core;

/// <summary>
/// Osservatore del turno: trasforma cio' che il router ha davvero visto in un ricordo.
///
/// Registra soltanto il risultato gia' filtrato dallo screening e i riferimenti osservati. Non
/// riassume, non interpreta e non conserva ragionamento interno o eventi grezzi del provider: se
/// una cosa non e' stata osservata, non diventa un fatto perche' e' stata scritta in memoria
/// (03-specs/MEMORY.md, «Flusso in ingresso»).
///
/// La classe del ricordo la decide la privacy sul testo dell'output, non chi lo ha prodotto: un
/// risultato che contiene un dato personale resta nella sessione anche se il turno era di progetto.
/// </summary>
public sealed class MemoryObserver
{
    private readonly IObservationStore store;
    private readonly PrivacyScanner scanner;
    private readonly IMetadataDigest digest;
    private readonly IIdGenerator ids;
    private readonly IClock clock;
    private readonly IMemoryAudit? audit;

    /// <param name="store">Persistenza delle osservazioni.</param>
    /// <param name="scanner">Scansione privacy del testo osservato.</param>
    /// <param name="digest">Digest con chiave locale, per i chunk.</param>
    /// <param name="ids">Generatore di identificatori.</param>
    /// <param name="clock">Orologio.</param>
    /// <param name="audit">
    /// Registro degli eventi di memoria. Senza registro la destinazione non viene tracciata, ed e'
    /// il caso dei comandi di sola lettura dei test: il prodotto lo passa sempre.
    /// </param>
    public MemoryObserver(
        IObservationStore store,
        PrivacyScanner scanner,
        IMetadataDigest digest,
        IIdGenerator ids,
        IClock clock,
        IMemoryAudit? audit = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(clock);

        this.store = store;
        this.scanner = scanner;
        this.digest = digest;
        this.ids = ids;
        this.clock = clock;
        this.audit = audit;
    }

    /// <summary>
    /// Registra il risultato di un turno completato. Un turno negato, fallito o incerto non produce
    /// alcun ricordo: non c'e' nulla di osservato da conservare.
    /// </summary>
    /// <returns>L'osservazione scritta, oppure null se non c'era nulla da registrare.</returns>
    public async ValueTask<ObservationRecord?> ObserveAsync(
        Guid projectId,
        string sessionId,
        ExecutionResult result,
        TimeSpan retention,
        CancellationToken cancellationToken,
        DataClass floor = DataClass.Public)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(result);

        if (result.State is not ExecutionState.Completed || result.Output is not string output || output.Length == 0)
        {
            return null;
        }

        PrivacyReport report = scanner.Scan(output, floor);

        if (report.AggregateClass is DataClass.Secret)
        {
            // Un segreto non entra in memoria in nessuna forma.
            return null;
        }

        if (report.Certainty is not ScanCertainty.Complete)
        {
            // Classe non determinata: utilizzabile nel turno, non conservabile.
            return null;
        }

        bool shareable = report.AggregateClass is DataClass.Public or DataClass.Internal;

        MemoryEvent source = new(
            result.ExecutionId,
            AdapterEventId: null,
            Sequence: result.AttemptsUsed,
            Kind: ObservationKind.Note,
            Text: output,
            EvidenceRefs: result.ExecutionId is string execution ? [execution] : []);

        // Ogni ricordo va all'indice locale: l'indice potenziato fuori dalla macchina non esiste piu' (ADR-032).
        MemoryDestination destination = MemoryDestination.Local;

        ObservationRecord observation = ObservationFactory.Create(
            ids.NewId(),
            projectId,
            sessionId,
            source,
            report.AggregateClass,
            shareable ? MemoryScopeKind.Project : MemoryScopeKind.Session,
            clock.UtcNow,
            retention,
            publishedForChatbot: false,
            destination);

        IReadOnlyList<MemoryChunk> chunks = MemoryChunker.Split(observation.Id, observation.Content, digest, ids);

        ObservationRecord written = await store.AppendAsync(observation, chunks, cancellationToken).ConfigureAwait(false);

        await RecordRouteAsync(written, report, cancellationToken).ConfigureAwait(false);

        // Il conteggio segue la scrittura, non l'intenzione: un'osservazione respinta sopra non e'
        // mai entrata in memoria.
        RouterTelemetry.RecordMemoryIngest();

        return written;
    }

    /// <summary>
    /// Registra la destinazione con la classe e la versione dell'insieme di rilevatori che l'ha
    /// decisa. Senza la versione dei rilevatori la riga non e' verificabile a posteriori: la stessa
    /// classe puo' venire da insiemi diversi, e un giorno la differenza conta.
    /// </summary>
    private ValueTask RecordRouteAsync(
        ObservationRecord written,
        PrivacyReport report,
        CancellationToken cancellationToken)
    {
        if (audit is null)
        {
            return ValueTask.CompletedTask;
        }

        return audit.RecordAsync(
            MemoryAuditCodes.Routed,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["destination"] = written.Destination.ToWire(),
                ["classification"] = written.Classification.ToWire(),
                ["certainty"] = report.Certainty.ToWire(),
                ["detectorVersion"] = report.DetectorVersion,
                ["observationId"] = written.Id,
                ["sessionId"] = written.SessionId,
                ["projectId"] = written.ProjectId.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
            },
            cancellationToken);
    }
}
