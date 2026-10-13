using System.Net;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Adapters.Ollama;

/// <summary>
/// Probe del modello locale.
///
/// Legge l'elenco dei modelli installati e, se necessario, la loro scheda: non invia un prompt, non carica file
/// dell'utente e non avvia alcuna inferenza. Un modello assente non viene scaricato: <c>doctor</c>
/// chiede all'operatore di installarlo, perche' un download durante una richiesta sarebbe un
/// egress che nessuno ha autorizzato (ROADMAP S09, «Se bloccato»).
///
/// <c>local_inference</c> non viene osservato dalla probe: endpoint numerico e modello installato
/// non dimostrano l'assenza di egress nell'ambiente. La copertura richiede l'attestazione esplicita
/// e valida dell'operatore; il processo osserva soltanto <c>closed_payload</c>.
/// </summary>
public sealed class OllamaProbe : ITargetProbe, IDisposable
{
    /// <summary>Percorso dell'elenco modelli.</summary>
    public const string TagsPath = "/api/tags";

    /// <summary>Percorso della scheda di un modello, letta solo se l'elenco non riporta le capacita'.</summary>
    public const string ShowPath = "/api/show";

    /// <summary>Capacita' che Ollama riporta per un modello che sa usare strumenti.</summary>
    public const string ToolsCapability = "tools";

    /// <summary>Durata di una osservazione.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);

    private readonly HttpClient client;
    private readonly IClock clock;
    private readonly bool ownsClient;

    public OllamaProbe(IClock clock)
        : this(new HttpClient(PlacementRules.CreateHandler(Placement.Lan)), clock, ownsClient: true)
    {
    }

    public OllamaProbe(HttpClient client, IClock clock, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(clock);

        this.client = client;
        this.clock = clock;
        this.ownsClient = ownsClient;
    }

    /// <inheritdoc />
    public async ValueTask<ProbeResult> ProbeAsync(ServiceDescriptor service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (!PlacementRules.IsAcceptable(service.InferencePlacement, service.Endpoint))
        {
            return Unavailable(service, PlacementRules.Violation(service.InferencePlacement, service.Endpoint)!);
        }

        if (service.Model is not string model || model.Length == 0 || service.HasPlaceholders)
        {
            return Unavailable(service, "model_not_configured");
        }

        if (LocalModel.IsCloudAlias(model))
        {
            // Un modello ospitato passa dallo stesso endpoint locale ma non e' inferenza locale.
            return Unavailable(service, "model_is_cloud_hosted");
        }

        IReadOnlyDictionary<string, InstalledModel>? installed = await ReadInstalledAsync(service.Endpoint!, cancellationToken)
            .ConfigureAwait(false);

        if (installed is null)
        {
            return Unavailable(service, "endpoint_unreachable");
        }

        // Ollama elenca i modelli con il tag; un nome senza tag significa `:latest`, e dire «non installato» a chi ha
        // scritto il nome corto manderebbe a completare una configurazione gia' giusta.
        if (!installed.TryGetValue(model, out InstalledModel? observed)
            && !(!model.Contains(':', StringComparison.Ordinal)
                && installed.TryGetValue(model + ":latest", out observed)))
        {
            // Presente l'endpoint, assente il modello: e' una configurazione da completare, non un
            // guasto, e non si scarica nulla.
            return Unavailable(service, "model_not_installed");
        }

        // La capacita' di usare strumenti si osserva dal modello installato (ADR-020): prima l'elenco, poi la
        // scheda del modello per le versioni che non la riportano li'. Non osservata vale no.
        IReadOnlyList<string>? capabilities = observed.Capabilities
            ?? (await ReadCardAsync(service.Endpoint!, model, cancellationToken).ConfigureAwait(false)).Capabilities;
        bool toolCalling = capabilities?.Contains(ToolsCapability, StringComparer.Ordinal) == true;

        DateTimeOffset now = clock.UtcNow;

        // local_inference non e' osservabile fino in fondo dal processo: l'osservazione copre
        // endpoint numerico, assenza di proxy e redirect, e modello installato senza alias cloud.
        // Il resto e' l'attestazione dell'operatore, cioe' il controllo dichiarato nel registro.
        // Solo cio' che questo processo ha visto davvero: endpoint numerico di loopback, nessun proxy, nessun redirect,
        // modello installato e non ospitato. `local_inference` non si osserva da qui — che nessun byte lasci la macchina
        // e' una verifica d'ambiente — e ripeterlo perche' il registro lo dichiara renderebbe l'attestazione con
        // scadenza un adempimento senza effetto (S30, INV-15).
        HashSet<Control> controls = [Control.ClosedPayload];

        return new ProbeResult(
            service.Id,
            ProbeHealth.Healthy,
            AuthStatus.Active,
            // Un modello locale non ha una quota osservabile: sconosciuta, non piena.
            QuotaState.Unknown,
            ExecutablePath: null,
            // Il digest del modello osservato prende il posto dell'hash dell'eseguibile: e' cio'
            // che identifica i pesi effettivamente installati.
            ExecutableHash: observed.Digest,
            Version: null,
            ProtocolVersion: null,
            Model: model,
            // Installation alone does not establish chat support: embedding-only and unknown models
            // must not acquire a capability that the provider did not report.
            Capabilities: capabilities?.Contains("completion", StringComparer.Ordinal) == true
                ? new HashSet<Capability> { Capability.Chat } : new HashSet<Capability>(),
            Controls: controls,
            ContextWindowTokens: null,
            CheckedAt: now,
            ExpiresAt: now + Freshness,
            FailureCode: null)
        {
            ToolCalling = toolCalling,
        };
    }

    /// <summary>Legge capacita' e lunghezza del vettore dalla scheda del modello; qualunque guasto vale «non osservata».</summary>
    private async Task<(IReadOnlyList<string>? Capabilities, int? EmbeddingLength)> ReadCardAsync(Uri endpoint, string model, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = BoundedHttpContent.Deadline(client, cancellationToken);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri(endpoint, ShowPath))
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model }), System.Text.Encoding.UTF8, "application/json"),
            };

            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            if (response.StatusCode is not HttpStatusCode.OK)
            {
                return (null, null);
            }

            using JsonDocument document = JsonDocument.Parse(
                await BoundedHttpContent.ReadUtf8Async(response.Content, BoundedHttpContent.MetadataBytes, timeout.Token).ConfigureAwait(false));

            return (Capabilities(document.RootElement), EmbeddingLength(document.RootElement));
        }
        catch (HttpRequestException)
        {
            return (null, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, null);
        }
        catch (Exception ex) when (ex is JsonException or PaRouterException or IOException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Lunghezza del vettore dichiarata dalla scheda del modello (`model_info`, chiave
    /// <c>&lt;architettura&gt;.embedding_length</c>). E' un numero che l'operatore non puo' conoscere e che il router
    /// puo' **vedere**: chiederlo a mano significava accettare un valore sbagliato, e un vettore di dimensione diversa
    /// viene rifiutato a ogni indicizzazione — la memoria restava lessicale senza che nessuno sapesse perche'.
    /// Un valore assente o non plausibile resta non osservato: non si inventa un numero.
    /// </summary>
    private static int? EmbeddingLength(JsonElement element)
    {
        if (element.ValueKind is not JsonValueKind.Object
            || !element.TryGetProperty("model_info", out JsonElement info)
            || info.ValueKind is not JsonValueKind.Object) { return null; }

        foreach (JsonProperty property in info.EnumerateObject())
        {
            if (!property.Name.EndsWith(".embedding_length", StringComparison.Ordinal)
                || property.Value.ValueKind is not JsonValueKind.Number
                || !property.Value.TryGetInt32(out int length)
                || length is <= 0 or > 65536) { continue; }
            return length;
        }

        return null;
    }

    private static IReadOnlyList<string>? Capabilities(JsonElement element)
    {
        if (element.ValueKind is not JsonValueKind.Object
            || !element.TryGetProperty("capabilities", out JsonElement capabilities)
            || capabilities.ValueKind is not JsonValueKind.Array
            || capabilities.EnumerateArray().Any(item => item.ValueKind is not JsonValueKind.String)) { return null; }
        return [.. capabilities.EnumerateArray().Select(item => item.GetString()!).Distinct(StringComparer.Ordinal)];
    }

    private async Task<IReadOnlyDictionary<string, InstalledModel>?> ReadInstalledAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = BoundedHttpContent.Deadline(client, cancellationToken);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(endpoint, TagsPath));
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            if (response.StatusCode is not HttpStatusCode.OK)
            {
                return null;
            }

            string body = await BoundedHttpContent.ReadUtf8Async(response.Content, BoundedHttpContent.MetadataBytes, timeout.Token).ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !document.RootElement.TryGetProperty("models", out JsonElement models)
                || models.ValueKind is not JsonValueKind.Array)
            {
                return null;
            }

            Dictionary<string, InstalledModel> installed = new(StringComparer.Ordinal);

            foreach (JsonElement entry in models.EnumerateArray())
            {
                if (entry.ValueKind is not JsonValueKind.Object
                    || !entry.TryGetProperty("name", out JsonElement name) || name.ValueKind is not JsonValueKind.String
                    || string.IsNullOrWhiteSpace(name.GetString()))
                {
                    continue;
                }

                installed[name.GetString()!] = new InstalledModel(
                    entry.TryGetProperty("digest", out JsonElement digest) && digest.ValueKind is JsonValueKind.String
                        ? digest.GetString()
                        : null,
                    Capabilities(entry),
                    entry.TryGetProperty("size", out JsonElement size) && size.ValueKind is JsonValueKind.Number
                        && size.TryGetInt64(out long bytes) && bytes >= 0 ? bytes : null);
            }

            return installed;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is JsonException or PaRouterException or IOException)
        {
            return null;
        }
    }

    private ProbeResult Unavailable(ServiceDescriptor service, string failureCode)
    {
        DateTimeOffset now = clock.UtcNow;

        return new ProbeResult(
            service.Id,
            ProbeHealth.Unavailable,
            AuthStatus.Unknown,
            QuotaState.Unknown,
            ExecutablePath: null,
            ExecutableHash: null,
            Version: null,
            ProtocolVersion: null,
            Model: null,
            Capabilities: new HashSet<Capability>(),
            Controls: new HashSet<Control>(),
            ContextWindowTokens: null,
            CheckedAt: now,
            ExpiresAt: now + Freshness,
            FailureCode: failureCode);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
    }

    /// <summary>Modello installato: digest dei pesi e capacita', se l'elenco le riporta.</summary>
    public sealed record InstalledModel(string? Digest, IReadOnlyList<string>? Capabilities, long? SizeBytes = null, int? EmbeddingLength = null)
    {
        /// <summary>Provider-reported purpose, not routing eligibility. Missing metadata stays unknown.</summary>
        public bool? Supports(ServiceCapability capability) => capability switch
        {
            ServiceCapability.Chat or ServiceCapability.RequestAnalysis => Capabilities?.Contains("completion", StringComparer.Ordinal),
            ServiceCapability.Embedding => Capabilities?.Contains("embedding", StringComparer.Ordinal),
            _ => null,
        };
    }

    /// <summary>Discovery richiesta dal setup; stessa lettura della probe, senza inferenza o pull.</summary>
    public Task<IReadOnlyDictionary<string, InstalledModel>?> DiscoverAsync(Placement placement, Uri endpoint, CancellationToken cancellationToken)
    {
        PlacementRules.Require(placement, endpoint);
        return ReadInstalledAsync(endpoint, cancellationToken);
    }

    /// <summary>Explicit catalogue action: enrich missing capabilities with the existing read-only show endpoint.</summary>
    public async Task<IReadOnlyDictionary<string, InstalledModel>?> DiscoverCatalogueAsync(
        Placement placement, Uri endpoint, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        IReadOnlyDictionary<string, InstalledModel>? installed;
        try { installed = await DiscoverAsync(placement, endpoint, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        if (installed is null) { return null; }
        Dictionary<string, InstalledModel> catalogue = new(StringComparer.Ordinal);
        foreach (var (name, model) in installed.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LocalModel.IsCloudAlias(name)) { continue; }
            catalogue[name] = model;
            // La scheda si legge quando manca una delle due osservazioni. La dimensione interessa i soli modelli di
            // embedding: per gli altri l'elenco basta, e una lettura in piu' per modello allungherebbe la scoperta.
            bool wantsLength = model.Supports(ServiceCapability.Embedding) is not false;
            if ((model.Capabilities is not null && !wantsLength) || deadline.IsCancellationRequested) { continue; }
            try
            {
                (IReadOnlyList<string>? capabilities, int? length) = await ReadCardAsync(endpoint, name, deadline.Token).ConfigureAwait(false);
                catalogue[name] = model with
                {
                    Capabilities = model.Capabilities ?? capabilities,
                    EmbeddingLength = length,
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Preserve the observed catalogue on an enrichment timeout; unobserved capabilities stay unknown.
            }
        }
        return catalogue;
    }
}
