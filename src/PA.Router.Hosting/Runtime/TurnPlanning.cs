using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Policy;
using PA.Router.Registry.Loading;
using PA.Router.Routing;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Recinto di una sessione: fin dove un agente puo' arrivare. Lo fissa l'operatore dalla CLI, una
/// volta, e il profilo puo' solo restringerlo (ADR-019, INV-18a). L'host API non ne ha uno.
/// </summary>
/// <param name="WorkspaceRoot">Cartella di lavoro, percorso completo.</param>
/// <param name="Writable">Vero se l'operatore ha concesso la scrittura nella cartella.</param>
/// <param name="ShellAllowed">Vero se l'operatore ammette comandi di sistema, sempre con conferma.</param>
public sealed record SessionFence(string WorkspaceRoot, bool Writable, bool ShellAllowed)
{
    /// <summary>
    /// Recinto dichiarato, oppure null se l'operatore non ne ha dichiarato uno.
    /// </summary>
    /// <returns>Falso se la cartella indicata non esiste.</returns>
    public static bool TryCreate(string? root, bool writable, bool shellAllowed, out SessionFence? fence)
    {
        fence = null;

        if (string.IsNullOrWhiteSpace(root))
        {
            return true;
        }

        string full = Path.GetFullPath(root);

        if (!Directory.Exists(full))
        {
            return false;
        }

        fence = new SessionFence(full, writable, shellAllowed);

        return true;
    }
}

/// <summary>Che cosa il router fara' con una richiesta, prima di farlo.</summary>
/// <param name="Classification">Classificazione del testo.</param>
/// <param name="Capabilities">Capacita' agentiche concesse al turno: il minimo dentro il recinto.</param>
/// <param name="Workspace">Cartella passata all'agente, null per una chat.</param>
/// <param name="NeedsWriteConfirmation">Vero se serve la conferma della prima scrittura della sessione.</param>
/// <param name="NeedsShellConfirmation">Vero se il turno esegue comandi: la conferma serve ogni volta.</param>
/// <param name="Limits">Perche' la richiesta ha avuto meno di quanto chiedeva, in parole.</param>
public sealed record TurnPlan(
    RequestClassification Classification,
    IReadOnlySet<Capability> Capabilities,
    WorkspaceDescriptor? Workspace,
    bool NeedsWriteConfirmation,
    bool NeedsShellConfirmation,
    IReadOnlyList<string> Limits)
{
    /// <summary>Tipo di azione effettivo dopo i limiti del recinto e del profilo.</summary>
    public Intent Intent => RequestAnalyzer.IntentOf(new HashSet<Capability>([Capability.Chat, .. Capabilities]));
}

/// <summary>
/// Dalla classificazione al turno, in un punto solo (INV-22): `pa run`, `pa explain`, il REPL e il
/// suo `/explain` chiamano questa funzione.
///
/// La classificazione dice che cosa il testo sembra chiedere; qui si decide che cosa si concede. Mai
/// oltre il recinto, mai oltre il profilo, e sempre il minimo: una richiesta di modifica in un
/// recinto di sola lettura diventa un'analisi, e lo si dice. Le conferme non si decidono qui, si
/// segnalano: le chiede il front end, che e' l'unico che parla con l'operatore.
/// </summary>
public static class TurnPlanning
{
    /// <summary>Pianifica una richiesta.</summary>
    /// <param name="snapshot">Configurazione in uso.</param>
    /// <param name="profileId">Profilo della richiesta.</param>
    /// <param name="input">Testo scritto dall'operatore, e solo quello.</param>
    /// <param name="fence">Recinto della sessione, null se non dichiarato.</param>
    /// <param name="writeConfirmed">Vero se l'operatore ha gia' confermato la scrittura in questa sessione.</param>
    /// <param name="cancellationToken">Annullamento.</param>
    public static Task<TurnPlan> PlanAsync(
        ConfigurationSnapshot snapshot,
        string profileId,
        string input,
        SessionFence? fence,
        bool writeConfirmed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(input);

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Plan(PolicyCompiler.Compile(TurnInputs.ResolveProfile(snapshot, profileId), snapshot.Config), input, fence, writeConfirmed));
    }

    /// <summary>
    /// Chat o codice lo dicono le regole (<see cref="RequestClassifier"/>). Fino al 24 settembre 2026 poteva deciderlo anche
    /// un classificatore a embedding configurato in <c>routing.classifier</c>; ADR-032 (fase 2) l'ha tolto, e le regole
    /// decidono come gia' facevano quando quel modello non era configurato.
    /// </summary>
    private static TurnPlan Plan(CompiledPolicy policy, string input, SessionFence? fence, bool writeConfirmed)
    {
        RequestClassification rules = RequestClassifier.Classify(input);
        RequestClassification classification = rules with { Signals = [.. rules.Signals, "decide:regole", "regole:" + rules.Intent.ToWire()] };

        List<string> limits = [];

        if (classification.Intent is Intent.Chat)
        {
            return Chat(classification, limits);
        }

        // Il piano lo fanno solo i front end del terminale: l'host API e l'SDK non chiamano questa classe
        // (ApiNeverReachesAgentsTests), e il turno nega capacita' e recinto sul canale di servizio (INV-23). Dal 24 settembre
        // 2026 il canale non viene piu' dal profilo (ADR-032, fase 2), quindi qui non c'e' un canale da guardare.

        if (fence is null)
        {
            limits.Add("nessun recinto dichiarato: la richiesta resta una chat");
            return Chat(classification, limits);
        }

        if (!policy.AllowedCapabilities.Contains(Capability.CodeRead))
        {
            limits.Add("il profilo non ammette la lettura del codice: la richiesta resta una chat");
            return Chat(classification, limits);
        }

        HashSet<Capability> capabilities = [Capability.CodeRead];

        if (classification.Intent is not Intent.CodeChange)
        {
            return new TurnPlan(classification, capabilities, new WorkspaceDescriptor(fence.WorkspaceRoot, false), false, false, limits);
        }

        if (!fence.Writable)
        {
            limits.Add("recinto in sola lettura: la modifica diventa un'analisi");
            return new TurnPlan(classification, capabilities, new WorkspaceDescriptor(fence.WorkspaceRoot, false), false, false, limits);
        }

        if (!policy.AllowedCapabilities.Contains(Capability.FileWrite))
        {
            limits.Add("il profilo non ammette la scrittura di file: la modifica diventa un'analisi");
            return new TurnPlan(classification, capabilities, new WorkspaceDescriptor(fence.WorkspaceRoot, false), false, false, limits);
        }

        capabilities.Add(Capability.FileWrite);

        bool shell = false;

        if (classification.NeedsShell)
        {
            if (!fence.ShellAllowed)
            {
                limits.Add("comandi di sistema fuori dal recinto: non vengono concessi");
            }
            else if (!policy.AllowedCapabilities.Contains(Capability.Shell))
            {
                limits.Add("il profilo non ammette comandi di sistema: non vengono concessi");
            }
            else
            {
                capabilities.Add(Capability.Shell);
                shell = true;
            }
        }

        return new TurnPlan(
            classification,
            capabilities,
            new WorkspaceDescriptor(fence.WorkspaceRoot, true, shell),
            NeedsWriteConfirmation: !writeConfirmed,
            NeedsShellConfirmation: shell,
            limits);
    }

    private static TurnPlan Chat(RequestClassification classification, List<string> limits) =>
        new(classification, new HashSet<Capability>(), null, false, false, limits);
}
