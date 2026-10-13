using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Core;
using PA.Router.Hosting.Runtime;
using PA.Router.Memory;
using PA.Router.Registry.Loading;
using PA.Router.Runtime.Ipc;

namespace PA.Router.Runtime.Worker;

/// <summary>
/// I metodi applicativi del worker: eseguire un turno e dire a che punto e' la coda di
/// indicizzazione.
///
/// Il worker non si fida di cio' che un client dichiara oltre al testo e a quali vincoli
/// aggiungere: profilo, canale, progetto e registro vengono dalla configurazione che il worker
/// possiede. Un client puo' restringere la scelta, mai allargarla.
/// </summary>
internal sealed class WorkerTurns
{
    private readonly RouterStack stack;
    private readonly PA.Router.Infrastructure.Storage.SqliteMemoryStore memoryStore;
    private readonly TurnEventBroker events;

    internal WorkerTurns(RouterStack stack, TurnEventBroker events)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(events);

        this.stack = stack;
        this.events = events;
        memoryStore = stack.MemoryStore();
    }

    /// <summary>Esegue un turno e restituisce cio' che il client deve stampare.</summary>
    internal async Task<RunResultPayload> RunAsync(RunRequestPayload request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Stessa regola del profilo delle letture: un profilo non dichiarato e' un errore. Il
        // profilo si cerca nella configurazione che c'e' adesso sul disco.
        PolicyDocument profile = TurnInputs.ResolveProfile(stack.EnsureCurrent().Snapshot, request.ProfileId);

        // Skill, capacita' e workspace arrivano dal canale e vengono rivalutati con la stessa regola
        // di `pa run`: il worker non si fida del client, e un turno che passa di qui non deve essere
        // giudicato diversamente da uno scritto al terminale (INV-22).
        if (!TurnInputs.TryParseCapabilities(request.Capabilities, out IReadOnlySet<Capability> capabilities, out string? unknown))
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "Unknown capability: " + unknown + ".")
                .WithMetadata("reason", "capability_unknown");
        }

        if (!TurnInputs.TryResolveWorkspace(request.WorkspaceRoot, request.WorkspaceWritable, out WorkspaceDescriptor? workspace))
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "The workspace directory does not exist.")
                .WithMetadata("reason", "workspace_missing");
        }

        // Gli eventi del turno escono dal worker e raggiungono chi sta guardando (S65): chi ha
        // chiesto il turno, e chiunque si sia attaccato a quella conversazione da un altro
        // terminale. Prima nascevano e morivano qui dentro.
        TurnOutcome outcome = await TurnRunner.RunAsync(
            stack,
            new TurnRequest(
                profile,
                request.Input,
                request.SessionId,
                request.ServiceId,
                request.SkillId,
                capabilities,
                workspace,
                Attached(request.ContextItems))
            {
                Observer = events.For(request.SessionId),
                ExternalConfirmed = request.ExternalConfirmed,
            },
            cancellationToken).ConfigureAwait(false);

        if (outcome.Result is not ExecutionResult result)
        {
            throw new PaRouterException(ErrorCode.ExtensionPermissionDenied, "The requested skill is not available.")
                .WithMetadata("reason", "skill_unavailable");
        }

        // Il servizio scelto e' quello dell'unica decisione ammessa: se la policy ha negato tutto
        // non c'e' un servizio da nominare, e dirlo e' preferibile a inventarlo.
        string? chosen = result.Decisions.FirstOrDefault(decision => decision.Allowed)?.TargetId;

        // Effetti e tentativi viaggiano con l'esito (S77): senza, un agente che ha toccato dei file
        // chiudeva con effetti `possible` e la risposta del worker non lo diceva, mentre lo stesso
        // turno eseguito dal client lo dichiarava. Con un worker vivo e' la configurazione piu'
        // usata, non un caso di bordo.
        return new RunResultPayload(
            result.ExecutionId ?? string.Empty,
            result.State.ToWire(),
            chosen,
            result.Output,
            result.ErrorCode?.ToWire(),
            [.. result.Decisions.SelectMany(decision => decision.Reasons).Select(reason => reason.ToWire()).Distinct().Order(StringComparer.Ordinal)],
            memoryStore.CountJobs(MemoryJobState.Pending),
            result.SideEffects.ToWire(),
            result.AttemptsUsed,
            // Il rifiuto con i suoi numeri: con un worker vivo e' l'unico modo perche' il terminale
            // possa dire contro quale soglia un servizio e' stato misurato.
            result.Denial,
            result.WeakPerimeter,
            result.BelowDemand);
    }

    /// <summary>
    /// Contenuti allegati dal client (S63), rivalutati come tutto il resto.
    ///
    /// Due regole, e sono entrambe restrizioni. La **classe non arriva dal client**: l'allegato entra
    /// senza classe dichiarata e la decide la scansione di questo processo, perche' lasciarla
    /// dichiarare a chi invia significherebbe lasciargliela abbassare. E la **provenienza e'
    /// limitata** a <c>workspace</c> e <c>operator</c>: un client non puo' spacciare un testo per
    /// ricordo o per uscita di un agente, che portano regole diverse.
    /// </summary>
    private static IReadOnlyList<ContextItem>? Attached(IReadOnlyList<ContextItemPayload>? items)
    {
        if (items is not { Count: > 0 })
        {
            return null;
        }

        List<ContextItem> attached = [];

        foreach (ContextItemPayload item in items)
        {
            Provenance provenance = item.Provenance switch
            {
                "workspace" => Provenance.Workspace,
                "operator" => Provenance.Operator,
                _ => throw new PaRouterException(
                        ErrorCode.InputInvalid,
                        "That context provenance cannot be declared by a client.")
                    .WithMetadata("reason", "provenance_not_allowed"),
            };

            attached.Add(new ContextItem(item.Id, item.Text, provenance, DeclaredClass: null));
        }

        return attached;
    }

    /// <summary>Stato della coda di indicizzazione, senza toccare la coda.</summary>
    internal MemoryStatusPayload Status(long drained) => new(
        memoryStore.CountJobs(MemoryJobState.Pending),
        memoryStore.CountJobs(MemoryJobState.Failed),
        drained);
}
