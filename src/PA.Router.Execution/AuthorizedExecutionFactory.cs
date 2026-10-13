using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Execution;

/// <summary>
/// Unica via per costruire un'<see cref="AuthorizedExecution"/>.
///
/// Il costruttore del tipo e' interno alle astrazioni e visibile solo a questo modulo: un
/// adapter, un host o un plugin non possono fabbricare un'esecuzione autorizzata, e la fabbrica
/// pretende comunque un grant e un payload gia' analizzato (INV-05).
/// </summary>
public static class AuthorizedExecutionFactory
{
    /// <summary>Compone l'esecuzione da consegnare all'adapter.</summary>
    /// <param name="grant">Grant monouso emesso per questo tentativo.</param>
    /// <param name="target">Target ammesso dalla policy.</param>
    /// <param name="payload">Payload gia' analizzato e minimizzato.</param>
    /// <param name="limits">Limiti effettivi dell'esecuzione.</param>
    /// <param name="model">Modello esatto dichiarato dal registro, se presente.</param>
    /// <param name="endpoint">Endpoint locale, per gli adapter HTTP.</param>
    /// <param name="executable">Programma da lanciare, per gli adapter a processo.</param>
    /// <param name="workspace">Workspace autorizzato, solo per gli adapter agentici.</param>
    /// <param name="secretRef">Nome del segreto nel vault, mai il valore.</param>
    /// <param name="extraArguments">Argomenti dichiarati dal registro, verificati di nuovo qui.</param>
    /// <param name="maxOutputTokens">Tetto di uscita in token dichiarato dal registro; zero se assente.</param>
    /// <param name="rounds">Giri modello-strumento gia' avvenuti nel turno.</param>
    public static AuthorizedExecution Create(
        ExecutionGrant grant,
        AdmissibleTarget target,
        SafePayload payload,
        ExecutionLimits limits,
        string? model = null,
        Uri? endpoint = null,
        string? executable = null,
        WorkspaceDescriptor? workspace = null,
        string? secretRef = null,
        IReadOnlyList<string>? extraArguments = null,
        int maxOutputTokens = 0,
        IReadOnlyList<ToolRound>? rounds = null)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(limits);

        if (!string.Equals(grant.TargetId, target.TargetId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The grant was issued for a different target.", nameof(grant));
        }

        if (!string.Equals(grant.PayloadDigest, payload.PayloadDigest, StringComparison.Ordinal))
        {
            throw new ArgumentException("The grant was issued for a different payload.", nameof(grant));
        }

        if (workspace is not null && target.Kind is not TargetKind.Agent)
        {
            // Un workspace ha senso solo per un agente: darlo a un LLM sarebbe un permesso che
            // nessuno ha valutato.
            throw new ArgumentException("Only an agent target may receive a workspace.", nameof(workspace));
        }

        if (payload.HasInstructions && target.Kind is TargetKind.Agent)
        {
            // Le istruzioni arrivano da un'applicazione esterna, e l'host API non raggiunge mai un
            // agente (INV-23). Un programma agentico non ha un ruolo di sistema in cui metterle: finirebbero
            // mescolate alla richiesta dell'operatore.
            throw new ArgumentException("Only a model target may receive application instructions.", nameof(payload));
        }

        if ((payload.Tools is { Count: > 0 } || rounds is { Count: > 0 }) && target.Kind is TargetKind.Agent)
        {
            // Gli strumenti di un'applicazione sono chiamate a server e API governate dal router; un agente
            // ha i propri strumenti nel recinto dell'operatore, e l'host API non lo raggiunge (INV-23).
            throw new ArgumentException("Only a model target may receive application tools.", nameof(payload));
        }

        return new AuthorizedExecution(
            grant.ExecutionId,
            grant.AttemptId,
            grant,
            payload,
            target.TargetId,
            target.Adapter,
            target.Kind,
            model,
            endpoint,
            target.InferencePlacement,
            secretRef,
            executable,
            limits,
            workspace,
            Vetted(target.Adapter, extraArguments),
            maxOutputTokens,
            rounds ?? []);
    }

    /// <summary>
    /// Argomenti del registro, verificati di nuovo qui. Il controllo alla lettura della
    /// configurazione resta il primo: questo e' il secondo, e serve perche' un'esecuzione
    /// autorizzata non deve poter nascere con un argomento che rimuove un controllo, qualunque
    /// percorso l'abbia costruita.
    /// </summary>
    private static IReadOnlyList<string> Vetted(AdapterKind adapter, IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return [];
        }

        if (AgentArgumentPolicy.FirstForbidden(adapter, arguments) is string forbidden)
        {
            throw new PaRouterException(
                ErrorCode.ControlUnavailable,
                "The service declares an argument that removes a control.")
                .WithMetadata("reason", "forbidden_argument")
                .WithMetadata("argument", forbidden);
        }

        return arguments;
    }
}
