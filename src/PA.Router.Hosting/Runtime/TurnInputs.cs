using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Interpretazione di cio' che l'operatore dichiara per un turno oltre al testo: capacita'
/// agentiche e workspace.
///
/// Sta qui perche' ha due chiamanti che devono dare la stessa risposta: <c>pa run</c>, che legge
/// gli argomenti, e il worker, che riceve gli stessi valori dal canale. Il worker li rivaluta
/// invece di fidarsi del client: il canale e' un confine, e un turno che arriva da li' deve essere
/// giudicato con la stessa regola di uno scritto al terminale (INV-22).
/// </summary>
public static class TurnInputs
{
    /// <summary>
    /// Profilo richiesto, oppure quello predefinito della configurazione. Un profilo che nessun
    /// file dichiara e' un errore, non un ripiego sul predefinito: sostituirlo in silenzio
    /// cambierebbe le autorizzazioni di chi lo ha chiesto.
    /// </summary>
    public static PolicyDocument ResolveProfile(Registry.Loading.ConfigurationSnapshot snapshot, string? profileId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string id = profileId ?? snapshot.Config.DefaultProfileId;

        if (!snapshot.Profiles.TryGetValue(id, out PolicyDocument? profile))
        {
            throw new PaRouterException(ErrorCode.PolicyInvalid, "That profile is not declared by the configuration.")
                .WithMetadata("reason", "profile_unknown");
        }

        return profile;
    }

    /// <summary>
    /// Capacita' agentiche richieste. La chat non si chiede: c'e' sempre, e la aggiunge il turno.
    /// </summary>
    /// <param name="values">Valori in forma wire, come li scrive l'operatore.</param>
    /// <param name="capabilities">Insieme riconosciuto.</param>
    /// <param name="unknown">Primo valore non riconosciuto, quando ce n'e' uno.</param>
    /// <returns>Vero se tutti i valori sono riconosciuti.</returns>
    public static bool TryParseCapabilities(
        IEnumerable<string>? values,
        out IReadOnlySet<Capability> capabilities,
        out string? unknown)
    {
        HashSet<Capability> parsed = [];

        unknown = null;

        foreach (string value in values ?? [])
        {
            if (!WireEnum.TryParse(value, out Capability capability))
            {
                // Un valore sconosciuto non si ignora: ignorarlo trasformerebbe una richiesta
                // agentica in una chat, e il turno risponderebbe a una domanda diversa.
                unknown = value;
                capabilities = new HashSet<Capability>();

                return false;
            }

            parsed.Add(capability);
        }

        capabilities = parsed;

        return true;
    }

    /// <summary>
    /// Workspace dichiarato per il turno, oppure null se non ne e' stato dichiarato uno.
    /// </summary>
    /// <param name="root">Radice indicata dall'operatore.</param>
    /// <param name="writable">Vero solo se l'operatore ha concesso la scrittura.</param>
    /// <param name="workspace">Workspace risolto con percorso completo.</param>
    /// <returns>Falso se la radice e' indicata e non esiste.</returns>
    public static bool TryResolveWorkspace(string? root, bool writable, out WorkspaceDescriptor? workspace)
    {
        workspace = null;

        if (string.IsNullOrWhiteSpace(root))
        {
            return true;
        }

        string full = Path.GetFullPath(root);

        if (!Directory.Exists(full))
        {
            return false;
        }

        // Il confine di scrittura e' una scelta esplicita: senza di essa l'agente legge e basta.
        workspace = new WorkspaceDescriptor(full, writable);

        return true;
    }

    /// <summary>Forma wire delle capacita', per chi le deve trasportare.</summary>
    public static IReadOnlyList<string> ToWire(IReadOnlySet<Capability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return [.. capabilities.Select(capability => capability.ToWire()).Order(StringComparer.Ordinal)];
    }
}
