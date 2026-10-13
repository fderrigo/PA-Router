using System.Text;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Abstractions.Observability;
using PA.Router.Core;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Storage;
using PA.Router.Session;

namespace PA.Router.Hosting.Runtime;

/// <summary>Cosa passare alla destinazione di un handover.</summary>
/// <param name="SourceSessionId">Sessione da cui il lavoro proviene.</param>
/// <param name="DestinationServiceId">Servizio che deve continuare; e' un vincolo, non un bypass.</param>
/// <param name="Objective">Obiettivo scritto dall'operatore: e' l'input del turno nuovo.</param>
/// <param name="WorkspaceRoot">Workspace che la capsule descrive.</param>
/// <param name="Constraints">Vincoli che restano validi.</param>
/// <param name="Decisions">Decisioni gia' prese.</param>
/// <param name="OpenWork">Lavoro ancora aperto.</param>
/// <param name="SourceExecutionId">Esecuzione di origine; senza, l'ultima della sessione.</param>
public sealed record HandoverRequest(
    string SourceSessionId,
    string DestinationServiceId,
    string Objective,
    string WorkspaceRoot,
    IReadOnlyList<string>? Constraints = null,
    IReadOnlyList<string>? Decisions = null,
    IReadOnlyList<string>? OpenWork = null,
    string? SourceExecutionId = null);

/// <summary>
/// Esito di un handover.
///
/// Un rifiuto non e' un turno fallito: e' un handover che non e' partito, e va distinto perche'
/// nessun servizio e' stato contattato.
/// </summary>
/// <param name="DestinationSessionId">Sessione nuova creata per la destinazione, se e' partita.</param>
/// <param name="SourceExecutionId">Esecuzione di origine effettivamente usata.</param>
/// <param name="CapsuleDigest">Digest della capsule consegnata.</param>
/// <param name="Capsule">Capsule costruita, per chi vuole mostrarla.</param>
/// <param name="Turn">Turno eseguito sulla destinazione, quando l'handover e' partito.</param>
/// <param name="Refusal">Ragione del rifiuto, quando l'handover non e' partito.</param>
/// <param name="RefusalDetail">Dettaglio leggibile del rifiuto.</param>
public sealed record HandoverOutcome(
    string? DestinationSessionId,
    string? SourceExecutionId,
    string? CapsuleDigest,
    HandoverCapsule? Capsule,
    TurnOutcome? Turn,
    ErrorCode? Refusal = null,
    string? RefusalDetail = null)
{
    /// <summary>Vero se il lavoro e' stato consegnato alla destinazione.</summary>
    public bool Delivered => Turn is not null;
}

/// <summary>
/// Handover fra servizi: la continuita' passa da una capsule, mai da una cronologia trapiantata.
///
/// E' un'operazione, non un comando: `pa session handover` e `/handover` del REPL la invocano
/// entrambi, e nessuno dei due ne tiene una copia (INV-22).
///
/// Cio' che la destinazione riceve e' **una sessione nuova** e una capsule costruita da dati
/// strutturati. Nessun transcript nativo dell'altro provider, nessuna configurazione, nessuna
/// credenziale: quella roba appartiene al programma da cui viene, e trasferirla sarebbe il modo
/// piu' rapido di far uscire qualcosa che nessuno ha autorizzato.
///
/// La capsule entra nel turno come **blocco dati** con la sua provenienza e riattraversa privacy e
/// policy: un testo che chiedesse di cambiare regola resta un dato
/// (03-specs/SESSION-HANDOVER.md, «Handover in ordine»).
/// </summary>
public static class SessionHandover
{
    /// <summary>Identificatore del blocco dati che porta la capsule nel contesto.</summary>
    public const string CapsuleItemId = "handover-capsule";

    /// <summary>Esegue un handover nell'ordine che la specifica impone.</summary>
    public static Task<HandoverOutcome> RunAsync(
        RouterStack stack,
        PolicyDocument profile,
        HandoverRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);

        return RunAsync(
            stack,
            profile,
            request,
            (root, token) => WorkspaceCheckpointReader.ReadAsync(root, stack.Digest, token),
            cancellationToken);
    }

    /// <summary>
    /// Variante con la lettura del checkpoint iniettata.
    ///
    /// Il controllo di drift confronta due letture separate dalla costruzione della capsule, e una
    /// prova che volesse verificarlo dovrebbe altrimenti correre con il filesystem. Qui la prova
    /// verifica il **confronto**, che e' la regola; la lettura vera resta quella del percorso
    /// pubblico e non ha un secondo chiamante.
    /// </summary>
    internal static async Task<HandoverOutcome> RunAsync(
        RouterStack stack,
        PolicyDocument profile,
        HandoverRequest request,
        Func<string, CancellationToken, Task<WorkspaceCheckpoint>> readCheckpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(readCheckpoint);

        if (request.Objective.Trim().Length == 0)
        {
            return Refused(ErrorCode.InputInvalid, "The objective is empty.");
        }

        if (stack.EnsureCurrent().Snapshot.FindService(request.DestinationServiceId) is null)
        {
            return Refused(ErrorCode.InputInvalid, "The registry does not declare that service.");
        }

        SqliteSessionService sessions = stack.Sessions();

        SessionSummary? summary = await sessions
            .ReadAsync(request.SourceSessionId, cancellationToken)
            .ConfigureAwait(false);

        if (summary is null)
        {
            return Refused(ErrorCode.InputInvalid, "That session does not exist.");
        }

        // 1. Il turno di origine deve essere terminale. Una capsule costruita mentre un'esecuzione
        //    ha effetti incerti descriverebbe un workspace di cui nessuno sa lo stato.
        if (summary.UncertainExecutions.Count > 0)
        {
            return Refused(
                ErrorCode.SideEffectUncertain,
                "The session has uncertain executions: run 'pa session recover' first.");
        }

        // 2. Stato del workspace prima di costruire la capsule. Un workspace che non e' un
        //    repository con un commit non ha un checkpoint, e senza checkpoint la capsule non
        //    proverebbe nulla: e' un rifiuto, non un turno fallito.
        WorkspaceCheckpoint before;

        try
        {
            before = await readCheckpoint(request.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (PaRouterException exception)
        {
            return Refused(exception.Code, exception.Message);
        }

        IReadOnlyList<SessionTurn> turns = await sessions
            .ReadTurnsAsync(request.SourceSessionId, cancellationToken)
            .ConfigureAwait(false);

        string sourceExecution =
            request.SourceExecutionId
            ?? turns.LastOrDefault()?.ExecutionId
            ?? request.SourceSessionId;

        // 3. La capsule e' deterministica e costruita da dati strutturati: nessun riassunto di un
        //    modello, e i fatti verificati arrivano solo da annotazioni dell'operatore.
        HandoverCapsule capsule = CapsuleBuilder.Create(
            request.SourceSessionId,
            sourceExecution,
            request.Objective.Trim(),
            request.Constraints ?? [],
            [],
            request.Decisions ?? [],
            request.OpenWork ?? [],
            [],
            before,
            SystemClock.Instance.UtcNow);

        string capsuleJson = CapsuleBuilder.ToJson(capsule);
        string capsuleDigest = stack.Digest.Compute(Encoding.UTF8.GetBytes(capsuleJson));

        // 6. Il checkpoint si confronta subito prima del dispatch: se il workspace e' cambiato
        //    mentre la capsule veniva preparata, quella capsule descrive uno stato che non esiste
        //    piu' e va rifatta.
        WorkspaceCheckpoint after;

        try
        {
            after = await readCheckpoint(request.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (PaRouterException exception)
        {
            return Refused(exception.Code, exception.Message);
        }

        if (after != before)
        {
            return new HandoverOutcome(
                null,
                sourceExecution,
                capsuleDigest,
                capsule,
                null,
                ErrorCode.ContextChanged,
                "The workspace changed while the capsule was being prepared: build it again.");
        }

        // 5. La destinazione riceve una sessione nuova. Nessun resume nativo e nessun
        //    identificativo di sessione altrui: sarebbe una ripresa che il router non puo'
        //    verificare.
        string destinationSession = Guid.NewGuid().ToString("D", System.Globalization.CultureInfo.InvariantCulture);

        // 4. Pipeline completa con la policy corrente: il cambio di servizio e' un vincolo in piu',
        //    non un permesso. Se la policy non ammette quella destinazione, l'handover viene negato
        //    come qualsiasi altro turno.
        TurnOutcome turn = await TurnRunner.RunAsync(
            stack,
            new TurnRequest(
                profile,
                request.Objective.Trim(),
                destinationSession,
                RequestedServiceId: request.DestinationServiceId,
                ContextItems: [new ContextItem(CapsuleItemId, capsuleJson, Provenance.Workspace, null)]),
            cancellationToken).ConfigureAwait(false);

        // 7. L'audit lega origine, destinazione e capsule. L'esecuzione della riga e' quella di
        //    destinazione, quindi nei metadata basta cio' che non si ricava da sola.
        if (turn.Result?.ExecutionId is string destinationExecution)
        {
            await RecordAsync(stack, destinationExecution, sourceExecution, capsuleDigest, cancellationToken)
                .ConfigureAwait(false);
        }

        return new HandoverOutcome(destinationSession, sourceExecution, capsuleDigest, capsule, turn);
    }

    private static async Task RecordAsync(
        RouterStack stack,
        string destinationExecution,
        string sourceExecution,
        string capsuleDigest,
        CancellationToken cancellationToken)
    {
        try
        {
            await stack.Store.AppendAuditAsync(
                new AuditRecord(
                    GuidIdGenerator.Instance.NewId(),
                    destinationExecution,
                    null,
                    AuditCodes.HandoverRecorded,
                    AuditMetadata.ToJson(
                        ("sourceExecutionId", sourceExecution),
                        ("capsuleDigest", capsuleDigest))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (PaRouterException)
        {
            // Il turno di destinazione e' concluso: una riga di audit che non si scrive non lo
            // annulla, e il guasto viene contato dalla telemetria.
            RouterTelemetry.RecordAuditFailure();
        }
    }

    private static HandoverOutcome Refused(ErrorCode code, string detail) =>
        new(null, null, null, null, null, code, detail);
}
