using PA.Router.Abstractions.Contracts;

namespace PA.Router.Core;

/// <summary>Cosa un provider offre davvero, secondo quanto e' stato osservato.</summary>
/// <param name="Adapter">Adapter interessato.</param>
/// <param name="PortableSkills">Vero se le skill testuali del router arrivano nel payload.</param>
/// <param name="NativeSkills">Vero se il router sa attivare le skill native del programma.</param>
/// <param name="NativePlugins">Vero se il router sa abilitare un plugin nativo del programma.</param>
/// <param name="NativeMemoryTools">Vero se il programma espone strumenti di memoria al router.</param>
/// <param name="Note">Motivo, in forma stabile, di cio' che manca.</param>
public sealed record ProviderCapability(
    AdapterKind Adapter,
    bool PortableSkills,
    bool NativeSkills,
    bool NativePlugins,
    bool NativeMemoryTools,
    string Note);

/// <summary>
/// Matrice di cio' che i provider offrono al router (03-specs/PLUGINS-SKILLS.md, S16).
///
/// E' scritta per dire dei no. Le skill testuali funzionano ovunque, perche' sono testo che entra
/// nel payload autorizzato. Le skill native, i plugin nativi e gli strumenti di memoria del
/// programma **non** sono attivabili dal router in questa beta: nessuno di quei percorsi e' stato
/// osservato e collaudato, e dichiararli disponibili sarebbe una promessa di interoperabilita' che
/// non esiste.
///
/// Il punto che conta per l'operatore: la memoria integrata continua a funzionare comunque, perche'
/// arriva dall'auto-injection del router, che non dipende da hook o plugin del provider.
/// </summary>
public static class ProviderCapabilities
{
    /// <summary>Motivo standard per un percorso nativo non collaudato.</summary>
    public const string NotCertified = "native_binding_not_certified";

    /// <summary>Capacita' osservate, per adapter.</summary>
    public static IReadOnlyList<ProviderCapability> All { get; } =
    [
        new ProviderCapability(
            AdapterKind.Ollama,
            PortableSkills: true,
            NativeSkills: false,
            NativePlugins: false,
            NativeMemoryTools: false,
            Note: "modello locale senza strumenti: skill e memoria arrivano nel payload"),

        new ProviderCapability(
            AdapterKind.ClaudeCodeCli,
            PortableSkills: true,
            NativeSkills: false,
            NativePlugins: false,
            NativeMemoryTools: false,
            Note: NotCertified),

        new ProviderCapability(
            AdapterKind.CodexCli,
            PortableSkills: true,
            NativeSkills: false,
            NativePlugins: false,
            NativeMemoryTools: false,
            Note: NotCertified),

        new ProviderCapability(
            AdapterKind.OpenCodeCli,
            PortableSkills: true,
            NativeSkills: false,
            NativePlugins: false,
            NativeMemoryTools: false,
            Note: NotCertified),

        // I due percorsi HTTP verso un modello remoto non hanno strumenti nativi da collaudare:
        // ricevono un payload e rispondono. Le skill testuali e la memoria arrivano li' dentro,
        // come per il modello locale.
        new ProviderCapability(
            AdapterKind.OpenAiCompatible,
            PortableSkills: true,
            NativeSkills: false,
            NativePlugins: false,
            NativeMemoryTools: false,
            Note: "modello remoto senza strumenti: skill e memoria arrivano nel payload"),

        new ProviderCapability(
            AdapterKind.Anthropic,
            PortableSkills: true,
            NativeSkills: false,
            NativePlugins: false,
            NativeMemoryTools: false,
            Note: "modello remoto senza strumenti: skill e memoria arrivano nel payload"),
    ];

    /// <summary>Capacita' dell'adapter indicato.</summary>
    public static ProviderCapability For(AdapterKind adapter) =>
        All.FirstOrDefault(capability => capability.Adapter == adapter)
        ?? new ProviderCapability(adapter, false, false, false, false, NotCertified);

    /// <summary>
    /// Vero se una skill che richiede strumenti puo' essere eseguita. Nella beta e' sempre falso:
    /// una skill con requisiti di strumenti produce SKILL_UNSUPPORTED, e non l'istruzione di
    /// installare qualcosa.
    /// </summary>
    public static bool SupportsToolRequiringSkills(AdapterKind adapter) => For(adapter).NativeSkills;
}
