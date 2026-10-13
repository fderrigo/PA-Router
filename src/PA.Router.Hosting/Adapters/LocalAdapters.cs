using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Adapters.Anthropic;
using PA.Router.Adapters.ClaudeCode;
using PA.Router.Adapters.Codex;
using PA.Router.Adapters.Ollama;
using PA.Router.Adapters.OpenCode;
using PA.Router.Detectors.Onnx;
using PA.Router.Infrastructure.Process;

namespace PA.Router.Hosting.Adapters;

/// <summary>
/// Adapter disponibili nel prodotto.
///
/// Tre famiglie: il modello locale via HTTP su loopback, i servizi remoti — compatibili OpenAI
/// oppure Anthropic, che ha un contratto suo — e i programmi ufficiali avviati come processi
/// supervisionati. I programmi ufficiali sono registrati ma **non abilitati**: i loro
/// servizi nascono disabilitati nel registro, e la probe non dichiara alcun controllo finche' non e'
/// stato osservato con un turno reale. Registrare l'adapter e abilitare il servizio sono due cose
/// diverse, e la seconda resta una decisione dell'operatore (evidence/S13/ADAPTER-COMPATIBILITY.md).
///
/// Le osservazioni non vengono conservate: la freschezza dura quindici minuti e una probe di
/// dieci minuti fa non dice se il servizio e' vivo adesso.
/// </summary>
public sealed class LocalAdapters : IDisposable
{
    /// <summary>Tempo massimo di un'osservazione: una probe conclude, non genera.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    // I client di una generazione non hanno una scadenza propria: il tempo del turno lo fissa la deadline della policy,
    // e i cento secondi predefiniti taglierebbero a meta' una generazione locale lunga.
    private readonly HttpClient client = PlacementRules.CreateModelClient(Placement.Lan);

    // Il client dei servizi remoti ha una deadline di connessione piu' lunga e la verifica del
    // certificato predefinita: non esiste un percorso che la disattivi.
    private readonly HttpClient remote = PlacementRules.CreateModelClient(Placement.ThirdParty);

    // Le probe una scadenza la vogliono: osservano un servizio e devono dire com'e' andata.
    private readonly HttpClient localProbes = new(PlacementRules.CreateHandler(Placement.Lan)) { Timeout = ProbeTimeout };
    private readonly HttpClient remoteProbes = new(PlacementRules.CreateHandler(Placement.ThirdParty)) { Timeout = ProbeTimeout };
    private readonly OllamaChatAdapter chat;
    private readonly OllamaProbe probe;
    private readonly ClaudeCodeProbe claudeProbe;
    private readonly CodexProbe codexProbe;
    private readonly OpenCodeProbe openCodeProbe;
    private readonly OpenAiCompatibleProbe remoteProbe;
    private readonly AnthropicProbe anthropicProbe;
    private readonly OnnxPiiProbe? piiProbe;
    private readonly IClock clock;

    /// <summary>
    /// Esiti positivi recenti, per servizio e per cio' che la sonda guarda (23 settembre 2026). Vedi <see cref="ObserveAsync"/>.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProbeResult> recent = new(StringComparer.Ordinal);

    /// <param name="clock">Orologio usato per datare le osservazioni.</param>
    /// <param name="secrets">
    /// Risolutore dei segreti per nome, per gli adapter che parlano con un servizio remoto. Senza
    /// risolutore un servizio che dichiara un segreto resta inutilizzabile e lo dichiara: non
    /// esiste una chiamata anonima di ripiego.
    /// </param>
    /// <param name="modelsDirectory">
    /// Directory dei modelli installati, quando una directory dati e' aperta. Senza di essa il
    /// rilevatore PII locale non viene osservato: non si indovina dove stiano i pesi.
    /// </param>
    public LocalAdapters(IClock clock, Func<string, string?>? secrets = null, string? modelsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        Func<string, string?> resolve = secrets ?? (_ => null);
        this.clock = clock;

        chat = new OllamaChatAdapter(client);
        probe = new OllamaProbe(localProbes, clock);
        remoteProbe = new OpenAiCompatibleProbe(remoteProbes, clock, resolve);
        anthropicProbe = new AnthropicProbe(remoteProbes, clock, resolve);
        claudeProbe = new ClaudeCodeProbe(PlatformProcessGuardFactory.Instance, clock);
        codexProbe = new CodexProbe(PlatformProcessGuardFactory.Instance, clock);
        openCodeProbe = new OpenCodeProbe(PlatformProcessGuardFactory.Instance, clock);
        piiProbe = modelsDirectory is null ? null : new OnnxPiiProbe(modelsDirectory, clock);

        Adapters = new Dictionary<AdapterKind, IExecutionAdapter>
        {
            [AdapterKind.Ollama] = chat,
            [AdapterKind.ClaudeCodeCli] = new ClaudeCodeAdapter(PlatformProcessGuardFactory.Instance),
            [AdapterKind.CodexCli] = new CodexAdapter(PlatformProcessGuardFactory.Instance),
            [AdapterKind.OpenCodeCli] = new OpenCodeAdapter(PlatformProcessGuardFactory.Instance),
            [AdapterKind.OpenAiCompatible] = new OpenAiCompatibleAdapter(remote, resolve),
            [AdapterKind.Anthropic] = new AnthropicAdapter(remote, resolve),
        };
    }

    /// <summary>Adapter registrati, per tipo.</summary>
    public IReadOnlyDictionary<AdapterKind, IExecutionAdapter> Adapters { get; }

    /// <summary>
    /// Osserva i servizi abilitati con un adapter noto. L'osservazione dei programmi ufficiali legge
    /// percorso, digest e versione: non avvia un turno e non consuma quota.
    /// </summary>
    /// <param name="services">Servizi del registro.</param>
    /// <param name="cancellationToken">Annullamento.</param>
    /// <param name="reuse">
    /// Per quanto un esito «funziona» appena osservato vale ancora; null osserva tutto adesso (diagnosi, setup). Il turno
    /// passa un minuto (23 settembre 2026): le sonde giravano a ogni turno, una dopo l'altra, e le due chiamate di rete verso
    /// i fornitori remoti costavano sette secondi a domanda. Un esito negativo si ricontrolla sempre; un servizio spento in
    /// quel minuto fa fallire il tentativo con TARGET_UNAVAILABLE, e il router riprova su un altro.
    /// </param>
    public async Task<IReadOnlyDictionary<string, ProbeResult>> ObserveAsync(
        IEnumerable<ServiceDescriptor> services,
        CancellationToken cancellationToken,
        TimeSpan? reuse = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        List<Task<(string Id, ProbeResult Result)>> pending = [];

        foreach (ServiceDescriptor service in services)
        {
            if (!service.Enabled)
            {
                continue;
            }

            ITargetProbe? selected = service.Adapter switch
            {
                AdapterKind.Ollama => probe,
                // Il modello di embedding della memoria (24 settembre 2026): senza sonda era invisibile a `doctor` (S33), e
                // un modello disinstallato si scopriva solo come ricerca per sole parole. La sonda di Ollama guarda indirizzo,
                // modello installato e non ospitato, e non gli attribuisce la chat.
                AdapterKind.OllamaEmbed => probe,
                AdapterKind.ClaudeCodeCli => claudeProbe,
                AdapterKind.CodexCli => codexProbe,
                AdapterKind.OpenCodeCli => openCodeProbe,
                AdapterKind.OpenAiCompatible => remoteProbe,
                AdapterKind.Anthropic => anthropicProbe,
                AdapterKind.OnnxLocal => piiProbe,
                _ => null,
            };

            if (selected is null)
            {
                continue;
            }

            // In parallelo: il tempo e' quello della sonda piu' lenta, non la somma.
            pending.Add(ProbeAsync(selected, service, reuse, cancellationToken));
        }

        return (await Task.WhenAll(pending).ConfigureAwait(false))
            .ToDictionary(entry => entry.Id, entry => entry.Result, StringComparer.Ordinal);
    }

    private async Task<(string Id, ProbeResult Result)> ProbeAsync(
        ITargetProbe selected,
        ServiceDescriptor service,
        TimeSpan? reuse,
        CancellationToken cancellationToken)
    {
        // La chiave comprende cio' che la sonda guarda: cambiare indirizzo, modello o programma e' un altro servizio.
        string key = service.Id + "|" + service.Adapter.ToWire() + "|" + service.Endpoint + "|" + service.Model + "|" + service.Executable;
        DateTimeOffset now = clock.UtcNow;

        if (reuse is TimeSpan window
            && recent.TryGetValue(key, out ProbeResult? known)
            && now - known.CheckedAt < window
            && now < known.ExpiresAt)
        {
            return (service.Id, known);
        }

        ProbeResult result = await selected.ProbeAsync(service, cancellationToken).ConfigureAwait(false);

        if (result.Health is ProbeHealth.Healthy) { recent[key] = result; } else { recent.TryRemove(key, out _); }

        return (service.Id, result);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        chat.Dispose();
        probe.Dispose();
        remoteProbe.Dispose();
        anthropicProbe.Dispose();
        client.Dispose();
        remote.Dispose();
        localProbes.Dispose();
        remoteProbes.Dispose();
    }
}
