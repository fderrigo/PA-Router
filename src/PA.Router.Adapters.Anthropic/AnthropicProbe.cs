using System.Net;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Adapters.Anthropic;

/// <summary>
/// Probe di un servizio Anthropic.
///
/// Chiede soltanto l'elenco dei modelli: <c>GET /v1/models</c> non invia un prompt, non avvia
/// alcuna inferenza e **non consuma quota di generazione**.
///
/// Risponde alle tre domande che altrimenti si scoprono al primo turno: l'endpoint risponde, la
/// chiave viene accettata, il modello dichiarato esiste presso quel fornitore. In piu' verifica il
/// tetto di uscita, che questa API pretende nella richiesta: un servizio senza
/// <c>maxOutputTokens</c> non e' avviabile e va detto adesso, non a meta' di un turno.
///
/// Non dichiara alcun controllo sulla riservatezza oltre a <c>closed_payload</c>: regione,
/// assenza di addestramento e conservazione stanno dall'altra parte del filo e restano
/// attestazioni dell'operatore con scadenza.
/// </summary>
public sealed class AnthropicProbe : ITargetProbe, IDisposable
{
    /// <summary>Percorso dell'elenco dei modelli offerti.</summary>
    public const string ModelsPath = "/v1/models";

    /// <summary>
    /// Durata di una osservazione: chiave, quota e catalogo di un fornitore cambiano senza
    /// preavviso e senza che la macchina se ne accorga.
    /// </summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);

    private readonly HttpClient client;
    private readonly IClock clock;
    private readonly Func<string, string?> secrets;
    private readonly bool ownsClient;

    /// <param name="clock">Orologio del runtime.</param>
    /// <param name="secrets">Risolutore dei segreti per nome; riceve un nome, mai un valore.</param>
    public AnthropicProbe(IClock clock, Func<string, string?> secrets)
        : this(new HttpClient(PlacementRules.CreateHandler(Placement.ThirdParty)), clock, secrets, ownsClient: true)
    {
    }

    /// <param name="client">Client gia' configurato; i test ne passano uno con handler locale.</param>
    /// <param name="clock">Orologio del runtime.</param>
    /// <param name="secrets">Risolutore dei segreti per nome.</param>
    /// <param name="ownsClient">Vero se questa probe deve rilasciare il client.</param>
    public AnthropicProbe(HttpClient client, IClock clock, Func<string, string?> secrets, bool ownsClient = false)
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
        if (key is null)
        {
            return (null, "secret_not_stored");
        }
        Catalogue catalogue = await ReadCatalogueAsync(service.Endpoint!, key!, cancellationToken).ConfigureAwait(false);
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

        if (service.MaxOutputTokens <= 0)
        {
            // Questa API pretende il tetto nella richiesta: senza, l'adapter non potrebbe partire.
            return Unavailable(service, "output_ceiling_not_declared");
        }

        if (service.SecretRef is not string reference)
        {
            return Unavailable(service, "secret_not_declared", AuthStatus.Inactive);
        }

        if (secrets(reference) is not string key)
        {
            return Unavailable(service, "secret_not_stored", AuthStatus.Inactive);
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
            Version: AnthropicAdapter.VersionValue,
            ProtocolVersion: AnthropicAdapter.VersionValue,
            Model: model,
            Capabilities: new HashSet<Capability> { Capability.Chat },
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

    private async Task<Catalogue> ReadCatalogueAsync(Uri endpoint, string key, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = BoundedHttpContent.Deadline(client, cancellationToken);
        HttpResponseMessage response;

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(endpoint, ModelsPath.TrimStart('/')));

            request.Headers.Add(AnthropicAdapter.KeyHeader, key);
            request.Headers.Add(AnthropicAdapter.VersionHeader, AnthropicAdapter.VersionValue);

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
                return new Catalogue(null, "endpoint_redirected", AuthStatus.Unknown, QuotaExhausted: false);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new Catalogue(null, "credentials_refused", AuthStatus.Inactive, QuotaExhausted: false);
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests)
            {
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
