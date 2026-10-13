using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Adapters.Ollama;
using PA.Router.Policy;
using PA.Router.Privacy;
using PA.Router.Registry.Loading;
using PA.Router.Routing.Demand;

namespace PA.Router.Hosting.Runtime;

/// <summary>Il profilo di domanda di un turno, con cio' che serve a raccontarlo.</summary>
/// <param name="Profile">Il profilo.</param>
/// <param name="Check">L'esito del pre-check con i segnali letti.</param>
/// <param name="Unavailable">Perche' l'analizzatore non ha dato segnali, se non li ha dati.</param>
/// <param name="Elapsed">Tempo speso.</param>
public sealed record DemandOutcome(DemandProfile Profile, PreCheckResult Check, string? Unavailable, TimeSpan Elapsed);

/// <summary>
/// L'analizzatore della richiesta (ADR-026) e il profilo di domanda che ne segue (ADR-025).
///
/// In ombra il profilo si calcola, si registra e si racconta, e non decide nulla. Nel modo decisivo (ADR-031) fissa il
/// livello richiesto e il minimo sufficiente sceglie; vale solo per un modello e un prompt con un esito del benchmark
/// consegnato (<see cref="MeasuredFor"/>).
///
/// L'analizzatore estrae **segnali** — tipo di risposta e livelli — e non sceglie nulla: la scelta resta la
/// trasformazione deterministica di <see cref="DemandProfiler"/>. Gli stessi gate del classificatore (S42): servizio su
/// loopback, profilo che ammette l'elaborazione locale del testo, nessun segreto trovato dai rilevatori deterministici.
/// Legge solo l'inizio della richiesta dell'operatore; mai il contenuto di allegati, ricordi o cronologia, di cui usa
/// solo le dimensioni.
/// </summary>
public static class RequestAnalyzerModel
{
    /// <summary>Versione del prompt di sistema e dello schema dell'uscita.</summary>
    public const string PromptVersion = "analyzer-v2";

    /// <summary>Caratteri della richiesta passati al modello (budget fisso, ADR-026).</summary>
    public const int MaxInputCharacters = 2000;

    /// <summary>Finestra chiesta a Ollama: prompt di sistema piu' il budget della richiesta.</summary>
    public const int ContextTokens = 2048;

    /// <summary>Tempo massimo di un'analisi in ombra: oltre, niente segnali, e il profilo sale.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Tempo massimo di un'analisi decisiva (ADR-031): il turno la aspetta. A freddo, sulla macchina di riferimento, costa
    /// 19 s (15 per leggere il prompt di sistema senza GPU), a caldo 1,3 s; oltre questo limite vale la prudenza.
    /// </summary>
    public static readonly TimeSpan DecisiveTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Un esito del benchmark consegnato (ADR-031 B).</summary>
    public sealed record BenchmarkEntry(string Model, string Prompt, string Machine, string MeasuredOn, double UnderRouting, double RulesUnderRouting);

    private static readonly Lazy<IReadOnlyList<BenchmarkEntry>> Benchmarks = new(() =>
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PA.Router.Hosting.Analyzer.analyzer-benchmarks.json")
            ?? throw new InvalidOperationException("The analyzer benchmark resource is missing.");
        using JsonDocument document = JsonDocument.Parse(stream);

        return [.. document.RootElement.GetProperty("entries").EnumerateArray().Select(entry => new BenchmarkEntry(
            entry.GetProperty("model").GetString()!,
            entry.GetProperty("prompt").GetString()!,
            entry.GetProperty("machine").GetString()!,
            entry.GetProperty("measuredOn").GetString()!,
            entry.GetProperty("underRouting").GetDouble(),
            entry.GetProperty("rulesUnderRouting").GetDouble()))];
    });

    /// <summary>
    /// L'esito che sblocca il modo decisivo per questo modello e questo prompt, se c'e': sotto-stima migliore delle regole
    /// (INV-21). Un nome senza tag e' <c>:latest</c>, come per Ollama (S51).
    /// </summary>
    public static BenchmarkEntry? MeasuredFor(string? model)
    {
        if (model is null) { return null; }

        string Tagged(string name) => name.Contains(':', StringComparison.Ordinal) ? name : name + ":latest";

        return Benchmarks.Value.FirstOrDefault(entry =>
            string.Equals(Tagged(entry.Model), Tagged(model), StringComparison.Ordinal)
            && entry.Prompt == PromptVersion
            && entry.UnderRouting < entry.RulesUnderRouting);
    }

    /// <summary>
    /// Il modo con cui gira davvero l'analizzatore. Dal 24 settembre 2026 (ADR-032, fase 2) non si sceglie: senza
    /// <c>routing.analyzer</c> e' spento; configurato, decide se il modello e il prompt hanno un esito del benchmark
    /// (ADR-031 B), altrimenti gira in ombra da solo, e <see cref="Describe"/> lo dice.
    /// </summary>
    public static AnalyzerMode EffectiveMode(ConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Config.Routing.Analyzer is null)
        {
            return AnalyzerMode.Off;
        }

        return MeasuredFor(UsableService(snapshot)?.Model) is not null ? AnalyzerMode.Decisive : AnalyzerMode.Shadow;
    }

    /// <summary>Vero se il profilo di domanda decide in questo turno.</summary>
    public static bool Decisive(ConfigurationSnapshot snapshot) => EffectiveMode(snapshot) is AnalyzerMode.Decisive;

    private static readonly Lazy<string> SystemPrompt = new(() =>
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PA.Router.Hosting.Analyzer.analyzer-v2.txt")
            ?? throw new InvalidOperationException("The request analyzer prompt is missing.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    });

    /// <summary>
    /// Uscita imposta: un solo campo, il tipo di risposta seguito da cinque cifre (ragionamento, conoscenza, sintesi,
    /// contesto, dato sensibile). Un oggetto con sei chiavi faceva scrivere al modello sessanta token; cosi' sono
    /// sedici, e il tempo dell'analisi e' quasi tutto scrittura (S120).
    /// </summary>
    public static readonly string Schema = JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new { a = new { type = "string", pattern = "^(" + string.Join('|', Enum.GetValues<TaskType>().Select(task => task.ToWire())) + ")[123]{5}$" } },
        required = new[] { "a" },
    });

    /// <summary>Descrizione per la diagnosi.</summary>
    public static string Describe(ConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Config.Routing.Analyzer is not AnalyzerConfig analyzer)
        {
            return "off";
        }

        if (UsableService(snapshot) is not ServiceDescriptor service)
        {
            return "shadow (service " + analyzer.ServiceId + " not usable)";
        }

        return EffectiveMode(snapshot) is not AnalyzerMode.Decisive
            ? "shadow (" + analyzer.ServiceId + "; decisive needs a benchmark result for " + service.Model + " with " + PromptVersion + ")"
            : EffectiveMode(snapshot).ToWire() + " (" + analyzer.ServiceId + ")";
    }

    /// <summary>Vero se il profilo di domanda va calcolato in questo turno, in ombra o per decidere.</summary>
    public static bool Enabled(ConfigurationSnapshot snapshot) => snapshot.Config.Routing.Analyzer is not null;

    /// <summary>
    /// Scalda l'analizzatore: una richiesta breve che fa leggere a Ollama il prompt di sistema e lo lascia in cache. A freddo
    /// quella lettura costava 15 dei 19 secondi del primo turno (ADR-031 D). Non decide nulla e non registra nulla; un
    /// guasto qui non conta, il turno fara' la sua prova.
    /// </summary>
    public static async Task WarmUpAsync(ConfigurationSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!Enabled(snapshot) || UsableService(snapshot) is not ServiceDescriptor service)
        {
            return;
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        try
        {
            using OllamaAnalysisClient client = new();
            _ = await client.AnalyzeAsync(service.Endpoint!, service.Model!, SystemPrompt.Value, "ciao", Schema, ContextTokens, deadline.Token, service.KeepAliveMinutes)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PaRouterException or OperationCanceledException)
        {
            // Riscaldare e' un favore, non un requisito.
        }
    }

    /// <summary>
    /// Il profilo di domanda: pre-check, poi l'analizzatore se serve e se e' ammesso, poi la trasformazione. Non
    /// fallisce mai: un analizzatore che non risponde e' un profilo prudente, con il motivo.
    /// </summary>
    public static async Task<DemandOutcome> ProfileAsync(
        ConfigurationSnapshot snapshot,
        CompiledPolicy policy,
        string input,
        int contextBytes,
        int attachments,
        CancellationToken cancellationToken,
        Task<(AnalyzerSignals? Signals, string? Unavailable)>? started = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(input);

        long start = Stopwatch.GetTimestamp();
        PreCheckResult check = DemandProfiler.PreCheck(input, contextBytes, attachments);

        if (check.Outcome is PreCheckOutcome.Trivial)
        {
            return new DemandOutcome(DemandProfiler.Transform(check, null), check, null, Stopwatch.GetElapsedTime(start));
        }

        // Se l'etichetta ammette solo servizi gratuiti su questo computer o nella rete, il giudizio dell'analizzatore non
        // sposta la domanda altrove: non si paga (24 settembre 2026, 2,5 secondi per turno su un processore).
        if (Decisive(snapshot) && !CanChangeRoute(snapshot, policy))
        {
            return new DemandOutcome(DemandProfiler.Transform(check, null), check, "local_only", Stopwatch.GetElapsedTime(start));
        }

        // Il tempo che la fase dichiara e' quello che il turno ha aspettato qui: una chiamata partita prima, mentre
        // la memoria cercava, non lo fa aspettare due volte.
        (AnalyzerSignals? signals, string? unavailable) = started is not null
            ? await started.ConfigureAwait(false)
            : await SignalsAsync(snapshot, policy, input, cancellationToken).ConfigureAwait(false);

        return new DemandOutcome(DemandProfiler.Transform(check, signals), check, unavailable, Stopwatch.GetElapsedTime(start));
    }

    /// <summary>
    /// Fa partire la chiamata all'analizzatore prima del resto del turno (24 settembre 2026): legge solo il testo della
    /// domanda, quindi non aspetta ne' la memoria ne' le sonde. Su un processore l'analizzatore costava 2,5 secondi a
    /// ogni domanda, uno dopo l'altro con la ricerca in memoria. Null se la domanda e' gia' banale senza contesto.
    /// </summary>
    public static Task<(AnalyzerSignals? Signals, string? Unavailable)>? Start(
        ConfigurationSnapshot snapshot,
        CompiledPolicy policy,
        string input,
        int attachments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(input);

        return !Enabled(snapshot) || (Decisive(snapshot) && !CanChangeRoute(snapshot, policy)) || DemandProfiler.PreCheck(input, 0, attachments).Outcome is PreCheckOutcome.Trivial
            ? null
            : SignalsAsync(snapshot, policy, input, cancellationToken);
    }

    /// <summary>
    /// Vero se fra i servizi accesi che l'etichetta ammette ce n'e' uno a pagamento o fuori dalle macchine
    /// dell'operatore: e' l'unico caso in cui il livello della domanda decide se uscire. Senza, tutto resta comunque
    /// in casa, e l'analizzatore costerebbe secondi per non cambiare niente. Vale nel modo decisivo: in ombra
    /// l'analizzatore c'e' per essere osservato.
    /// </summary>
    public static bool CanChangeRoute(ConfigurationSnapshot snapshot, CompiledPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(policy);

        return snapshot.Registry.Services.Any(service =>
            service.Enabled
            && ServiceSetupCatalog.AnswersRequests(service.Capability)
            && policy.AllowsService(service.Id)
            && (service.Pricing is not null
                || service.InferencePlacement is not (Placement.Loopback or Placement.Lan)));
    }

    private static async Task<(AnalyzerSignals? Signals, string? Unavailable)> SignalsAsync(
        ConfigurationSnapshot snapshot,
        CompiledPolicy policy,
        string input,
        CancellationToken cancellationToken)
    {
        if (UsableService(snapshot) is not ServiceDescriptor service)
        {
            return (null, "service");
        }

        if (!policy.AllowLocalMemoryProcessing)
        {
            return (null, "profile");
        }

        // Un segreto non va a nessun modello, nemmeno locale: lo dicono i rilevatori deterministici.
        PrivacyReport report = new PrivacyScanner().Scan(input, DataClass.Public);

        if (report.AggregateClass is DataClass.Secret or DataClass.Unknown || report.Certainty is not ScanCertainty.Complete)
        {
            return (null, "credential");
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Decisive(snapshot) ? DecisiveTimeout : Timeout);

        try
        {
            using OllamaAnalysisClient client = new();
            AnalysisReply reply = await client.AnalyzeAsync(
                service.Endpoint!,
                service.Model!,
                SystemPrompt.Value,
                input.Length > MaxInputCharacters ? input[..MaxInputCharacters] : input,
                Schema,
                ContextTokens,
                deadline.Token,
                service.KeepAliveMinutes).ConfigureAwait(false);

            return Decode(reply) is AnalyzerSignals signals ? (signals, null) : (null, "output");
        }
        catch (PaRouterException)
        {
            return (null, "error");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "timeout");
        }
    }

    /// <summary>Dall'uscita compatta ai segnali; null se non rispetta la forma.</summary>
    public static AnalyzerSignals? Decode(AnalysisReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        try
        {
            using JsonDocument document = JsonDocument.Parse(reply.Content);

            if (!document.RootElement.TryGetProperty("a", out JsonElement value) || value.GetString() is not string code || code.Length < 6)
            {
                return null;
            }

            string digits = code[^5..];

            if (!digits.All(digit => digit is >= '1' and <= '3') || !WireEnum.TryParse(code[..^5], out TaskType task))
            {
                return null;
            }

            return new AnalyzerSignals(task, digits[0] - '0', digits[1] - '0', digits[2] - '0', digits[3] - '0', digits[4] - '0', reply.Confidence);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Servizio dell'analizzatore se utilizzabile adesso: abilitato, della capacita' giusta, su loopback.</summary>
    public static ServiceDescriptor? UsableService(ConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Config.Routing.Analyzer is not AnalyzerConfig analyzer || snapshot.FindService(analyzer.ServiceId) is not ServiceDescriptor service)
        {
            return null;
        }

        return service is { Enabled: true, Capability: ServiceCapability.RequestAnalysis, Adapter: AdapterKind.Ollama, InferencePlacement: Placement.Loopback }
            && service.Endpoint is Uri endpoint
            && service.Model is not null
            && PlacementRules.IsAcceptable(service.InferencePlacement, endpoint)
                ? service
                : null;
    }
}
