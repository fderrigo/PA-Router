using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Hosting.Runtime;

namespace PA.Router.Runtime.Ipc;

/// <summary>
/// Letture dell'operatore — ricerca e stato della memoria, pacchetti, skill — nella forma in cui
/// viaggiano sul canale del worker.
///
/// Chi possiede lo store le chiama direttamente; il worker le chiama per conto di un client. In
/// entrambi i casi la richiesta e la funzione sono le stesse, quindi la risposta non puo'
/// dipendere da chi possiede la directory dati (INV-22). Prima il REPL, con un worker vivo, non
/// rispondeva affatto: dichiarava la lettura non disponibile.
/// </summary>
public static class ReadRequests
{
    /// <summary>Ricerca nei ricordi dell'ambito della sessione indicata.</summary>
    public static async Task<MemorySearchPayload> SearchMemoryAsync(
        RouterStack stack,
        MemoryQueryPayload request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(request);

        // Anche una lettura usa la configurazione che c'e' adesso: una memoria disattivata un
        // momento fa non si interroga.
        stack.EnsureCurrent();
        RequireMemory(stack);

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "The query is empty.")
                .WithMetadata("reason", "query_empty");
        }

        PolicyDocument profile = TurnInputs.ResolveProfile(stack.Snapshot, request.ProfileId);

        // L'ambito lo decide il profilo: la sessione arriva dal client, il resto dalla
        // configurazione posseduta da chi risponde.
        using MemoryStack memory = MemoryQueries.Open(stack, profile, request.SessionId, out MemoryScope scope);

        MemorySearchResult result = await MemoryQueries
            .SearchAsync(
                memory,
                scope,
                request.Query,
                request.Limit > 0 ? request.Limit : MemoryLimits.DefaultSearchLimit,
                cancellationToken)
            .ConfigureAwait(false);

        return new MemorySearchPayload(
            result.Mode.ToWire(),
            result.IndexStatus.ToWire(),
            [.. result.Hits.Select(hit => new MemoryHitPayload(
                hit.ObservationId,
                hit.Kind.ToWire(),
                hit.Verification.ToWire(),
                hit.CreatedAt,
                hit.Title))],
            // «In ricostruzione» e' ottimista quando l'indicizzazione fallisce e riprova: senza il codice del
            // guasto l'operatore non ha modo di sapere che non si stava costruendo nulla.
            MemoryQueries.Failure(memory)?.Code,
            MemoryQueries.Failure(memory)?.At);
    }

    /// <summary>Stato della memoria visibile dall'ambito della sessione, con la coda.</summary>
    public static async Task<MemoryOverviewPayload> MemoryOverviewAsync(
        RouterStack stack,
        MemoryQueryPayload request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(request);

        // Anche una lettura usa la configurazione che c'e' adesso: una memoria disattivata un
        // momento fa non si interroga.
        stack.EnsureCurrent();
        RequireMemory(stack);

        PolicyDocument profile = TurnInputs.ResolveProfile(stack.Snapshot, request.ProfileId);

        using MemoryStack memory = MemoryQueries.Open(stack, profile, request.SessionId, out MemoryScope scope);

        MemoryQueueStatus status = await MemoryQueries.StatusAsync(memory, scope, cancellationToken).ConfigureAwait(false);

        return new MemoryOverviewPayload(
            status.Configured,
            status.ModelFingerprint,
            status.Observations,
            status.JobsPending,
            status.JobsRunning,
            status.JobsFailed,
            status.Generation,
            status.LastJobError?.Code,
            status.LastJobError?.At);
    }

    /// <summary>Pacchetti installati, con lo stato e se sono abilitati nel progetto.</summary>
    public static PluginListPayload ListPlugins(RouterStack stack)
    {
        ArgumentNullException.ThrowIfNull(stack);

        stack.EnsureCurrent();
        RequireExtensions(stack);

        return new PluginListPayload(
        [
            .. ExtensionQueries.ListPlugins(stack.PluginCatalog(), stack.Snapshot)
                .Select(plugin => new PluginPayload(
                    plugin.Id,
                    plugin.Version,
                    plugin.State.ToWire(),
                    plugin.EnabledHere))
        ]);
    }

    /// <summary>Skill offerte dai pacchetti installati.</summary>
    public static SkillListPayload ListSkills(RouterStack stack)
    {
        ArgumentNullException.ThrowIfNull(stack);

        stack.EnsureCurrent();
        RequireExtensions(stack);

        return new SkillListPayload(
        [
            .. ExtensionQueries.ListSkills(stack.PluginCatalog())
                .Select(skill => new SkillPayload(skill.SkillId, skill.PluginId, skill.State.ToWire()))
        ]);
    }

    private static void RequireMemory(RouterStack stack)
    {
        if (!MemoryQueries.Available(stack.Snapshot))
        {
            throw new PaRouterException(ErrorCode.MemoryUnavailable, "The local memory is disabled in the configuration.")
                .WithMetadata("reason", "memory_disabled");
        }
    }

    private static void RequireExtensions(RouterStack stack)
    {
        if (!ExtensionQueries.Available(stack.Snapshot))
        {
            throw new PaRouterException(ErrorCode.ExtensionPermissionDenied, "Extensions are disabled in the configuration.")
                .WithMetadata("reason", "extensions_disabled");
        }
    }
}
