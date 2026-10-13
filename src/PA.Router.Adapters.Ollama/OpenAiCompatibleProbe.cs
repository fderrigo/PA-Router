using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Adapters.Ollama;

/// <summary>
/// Probe di un servizio HTTP compatibile OpenAI.
///
/// Chiede soltanto l'elenco dei modelli offerti: <c>GET /v1/models</c> non invia un prompt, non
/// avvia alcuna inferenza e **non consuma quota di generazione**. E' l'unica osservazione
/// possibile su un servizio remoto senza pagarla.
///
/// Risponde alle tre domande che altrimenti si scoprono al primo turno: l'endpoint risponde, la
/// chiave viene accettata, il modello dichiarato esiste davvero presso quel fornitore.
///
/// Non dichiara alcun controllo sulla riservatezza. <c>closed_payload</c> e' l'unica proprieta'
/// che questo processo osserva, perche' il corpo della richiesta lo costruisce il router. Regione,
/// assenza di addestramento e conservazione stanno dall'altra parte del filo: restano attestazioni
/// dell'operatore con scadenza, mai osservazioni.
/// </summary>
public sealed class OpenAiCompatibleProbe : ITargetProbe, IDisposable
{
    /// <summary>Percorso dell'elenco dei modelli offerti.</summary>
    public const string ModelsPath = "/v1/models";

    /// <summary>
    /// Durata di una osservazione. Piu' corta di quella del modello locale: chiave, quota e
    /// catalogo di un fornitore cambiano senza preavviso e senza che la macchina se ne accorga.
    /// </summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    private readonly HttpClient client;
    private readonly IClock clock;
    private readonly Func<string, string?> secrets;
    private readonly bool ownsClient;

    /// <param name="clock">Orologio del runtime.</param>
    /// <param name="secrets">Risolutore dei segreti per nome; riceve un nome, mai un valore.</param>
    public OpenAiCompatibleProbe(IClock clock, Func<string, string?> secrets)
        : this(new HttpClient(PlacementRules.CreateHandler(Placement.ThirdParty)), clock, secrets, ownsClient: true)
    {
    }

    /// <param name="client">Client gia' configurato; i test ne passano uno con handler locale.</param>
    /// <param name="clock">Orologio del runtime.</param>
    /// <param name="secrets">Risolutore dei segreti per nome.</param>
    /// <param name="ownsClient">Vero se questa probe deve rilasciare il client.</param>
    public OpenAiCompatibleProbe(
        HttpClient client,
        IClock clock,
        Func<string, string?> secrets,
        bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(secrets);

        this.client = client;
        this.clock = clock;
        this.secrets = secrets;
        this.ownsClient = ownsClient;
    }

    /// <summary>Reads the existing catalogue without requiring a model to be configured first.</summary>
    public async Task<(IReadOnlyList<string>? Models, string? FailureCode)> DiscoverAsync(
        ServiceDescriptor service, CancellationToken cancellationToken)
    {
        if (!PlacementRules.IsAcceptable(service.InferencePlacement, service.Endpoint))
        {
            return (null, "endpoint_invalid");
        }
        string? key = service.SecretRef is string reference ? secrets(reference) : null;
        if (service.SecretRef is not null && key is null)
        {
            return (null, "secret_not_stored");
        }
        Catalogue catalogue = await ReadCatalogueAsync(service.Endpoint!, key, cancellationToken).ConfigureAwait(false);
        return (catalogue.Models is null ? null : [.. catalogue.Models.Order(StringComparer.Ordinal)], catalogue.FailureCode);
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

        string? key = null;

        if (service.SecretRef is string reference)
        {
            key = secrets(reference);

            if (key is null)
            {
                // Dichiarato e assente: il servizio e' inutilizzabile finche' l'operatore non lo
                // scrive nel vault. Non esiste una chiamata anonima di ripiego.
                return Unavailable(service, "secret_not_stored", AuthStatus.Inactive);
            }
        }

        Catalogue catalogue = await ReadCatalogueAsync(service.Endpoint!, key, cancellationToken).ConfigureAwait(false);

        if (catalogue.FailureCode is string failure)
        {
            return catalogue.QuotaExhausted
                ? Degraded(service, failure)
                : Unavailable(service, failure, catalogue.AuthStatus);
        }

        if (!catalogue.Models!.Contains(model))
        {
            // L'endpoint risponde e la chiave e' accettata, ma quel modello non e' fra quelli
            // offerti: e' una configurazione da correggere, e si scopre qui invece che a meta' di
            // un turno.
            return Unavailable(service, "model_not_offered", AuthStatus.Active);
        }

        DateTimeOffset now = clock.UtcNow;

        return new ProbeResult(
            service.Id,
            ProbeHealth.Healthy,
            AuthStatus.Active,
            // L'elenco dei modelli non dice nulla sulla quota di generazione: sconosciuta, non piena.
            QuotaState.Unknown,
            ExecutablePath: null,
            ExecutableHash: null,
            Version: null,
            ProtocolVersion: null,
            Model: model,
            Capabilities: new HashSet<Capability> { Capability.Chat },
            // Solo cio' che questo processo vede davvero: il corpo della richiesta lo compone il
            // router. Tutto il resto e' dall'altra parte del filo.
            Controls: new HashSet<Control> { Control.ClosedPayload },
            ContextWindowTokens: null,
            CheckedAt: now,
            ExpiresAt: now + Freshness,
            FailureCode: null);
    }

    /// <summary>Esito della lettura del catalogo: i modelli, oppure la ragione per cui manca.</summary>
    private readonly record struct Catalogue(
        IReadOnlySet<string>? Models,
        string? FailureCode,
        AuthStatus AuthStatus,
        bool QuotaExhausted);

    private async Task<Catalogue> ReadCatalogueAsync(Uri endpoint, string? key, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = BoundedHttpContent.Deadline(client, cancellationToken);
        HttpResponseMessage response;

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(endpoint, ModelsPath.TrimStart('/')));

            if (key is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return new Catalogue(null, "endpoint_unreachable", AuthStatus.Unknown, QuotaExhausted: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Catalogue(null, "endpoint_timeout", AuthStatus.Unknown, QuotaExhausted: false);
        }

        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                // Un redirect sposterebbe l'osservazione su un host che nessuno ha dichiarato.
                return new Catalogue(null, "endpoint_redirected", AuthStatus.Unknown, QuotaExhausted: false);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new Catalogue(null, "credentials_refused", AuthStatus.Inactive, QuotaExhausted: false);
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests)
            {
                // La chiave funziona, il fornitore sta limitando: e' degradato, non guasto.
                return new Catalogue(null, "quota_exhausted", AuthStatus.Active, QuotaExhausted: true);
            }

            if (response.StatusCode is not HttpStatusCode.OK)
            {
                return new Catalogue(null, "endpoint_error", AuthStatus.Unknown, QuotaExhausted: false);
            }

            try
            {
                string body = await BoundedHttpContent.ReadUtf8Async(response.Content, BoundedHttpContent.MetadataBytes, timeout.Token).ConfigureAwait(false);
                return Parse(body);
            }
            catch (Exception ex) when (ex is PaRouterException or IOException or HttpRequestException)
            {
                return new Catalogue(null, "protocol_unrecognized", AuthStatus.Unknown, QuotaExhausted: false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new Catalogue(null, "endpoint_timeout", AuthStatus.Unknown, QuotaExhausted: false);
            }
        }
    }

    /// <summary>
    /// Legge <c>data[].id</c>. Un documento che non ha quella forma non e' un catalogo vuoto: e'
    /// una risposta che non si e' capita, e dichiararla vuota farebbe fallire il servizio con la
    /// ragione sbagliata.
    /// </summary>
    private static Catalogue Parse(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind is not JsonValueKind.Array)
            {
                return new Catalogue(null, "protocol_unrecognized", AuthStatus.Active, QuotaExhausted: false);
            }

            HashSet<string> offered = new(StringComparer.Ordinal);

            foreach (JsonElement entry in data.EnumerateArray())
            {
                if (entry.ValueKind is JsonValueKind.Object && entry.TryGetProperty("id", out JsonElement id) && id.ValueKind is JsonValueKind.String)
                {
                    offered.Add(id.GetString()!);
                }
            }

            return new Catalogue(offered, null, AuthStatus.Active, QuotaExhausted: false);
        }
        catch (JsonException)
        {
            return new Catalogue(null, "protocol_unrecognized", AuthStatus.Active, QuotaExhausted: false);
        }
    }

    private ProbeResult Degraded(ServiceDescriptor service, string failureCode)
    {
        DateTimeOffset now = clock.UtcNow;

        return new ProbeResult(
            service.Id,
            ProbeHealth.Degraded,
            AuthStatus.Active,
            QuotaState.Exhausted,
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

    private ProbeResult Unavailable(
        ServiceDescriptor service,
        string failureCode,
        AuthStatus authStatus = AuthStatus.Unknown)
    {
        DateTimeOffset now = clock.UtcNow;

        return new ProbeResult(
            service.Id,
            ProbeHealth.Unavailable,
            authStatus,
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
}
