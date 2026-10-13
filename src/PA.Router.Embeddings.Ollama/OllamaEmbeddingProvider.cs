using System.Net;
using System.Text;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Embeddings.Ollama;

/// <summary>Parametri del provider di embedding locale.</summary>
/// <param name="Endpoint">Endpoint numerico di loopback.</param>
/// <param name="Model">Modello di embedding, distinto da quello di chat.</param>
/// <param name="Dimension">Dimensione attesa dei vettori.</param>
/// <param name="ModelDigest">Digest osservato del modello, quando noto.</param>
public sealed record OllamaEmbeddingOptions(Uri Endpoint, string Model, int Dimension, string? ModelDigest = null)
{
    /// <summary>Minuti di permanenza in memoria scelti dall'operatore (-1 sempre); null lascia decidere a Ollama.</summary>
    public int? KeepAliveMinutes { get; init; }

    /// <summary>Batch massimo previsto dalla specifica.</summary>
    public const int MaxBatch = 8;

    /// <summary>Timeout di una chiamata di embedding.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Attese fra i tentativi per errori transitori.</summary>
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)];

    /// <summary>
    /// L'impronta di queste opzioni. Sta qui perche' non richiede un client: il setup la compila nel registro con la
    /// stessa forma che il provider dichiarera', senza aprire una connessione per calcolare una stringa.
    /// </summary>
    public EmbeddingFingerprint Fingerprint => new(
        "ollama",
        Model,
        ModelDigest,
        Dimension,
        EmbeddingFingerprint.CurrentPreprocessingVersion);
}

/// <summary>
/// Embedding locali tramite <c>/api/embed</c>.
///
/// E' un provider distinto da quello di chat, con modello, fingerprint e dimensione propri:
/// confondere i due significherebbe indicizzare con un modello e cercare con un altro. I vettori
/// vengono validati prima di uscire — niente NaN, niente infiniti, niente vettore nullo, niente
/// dimensione diversa da quella attesa — e normalizzati L2, cosi' il coseno si riduce a un
/// prodotto scalare (03-specs/SEMANTIC-SEARCH.md).
///
/// Se il modello locale manca non esiste alcun ripiego cloud: l'errore e' EMBEDDING_UNAVAILABLE.
/// </summary>
public sealed class OllamaEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    /// <summary>Percorso degli embedding nell'API locale.</summary>
    public const string EmbedPath = "/api/embed";

    private readonly HttpClient client;
    private readonly OllamaEmbeddingOptions options;
    private readonly bool ownsClient;

    // Un solo batch attivo per provider: il modello locale usa la memoria della macchina, e due
    // batch in parallelo la fanno solo oscillare (03-specs/SEMANTIC-SEARCH.md).
    private readonly SemaphoreSlim batchGate = new(1, 1);

    public OllamaEmbeddingProvider(OllamaEmbeddingOptions options)
        : this(options, new HttpClient(LocalEndpoint.CreateHandler()), ownsClient: true)
    {
    }

    public OllamaEmbeddingProvider(OllamaEmbeddingOptions options, HttpClient client, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);

        LocalEndpoint.Require(options.Endpoint);

        if (LocalModel.IsCloudAlias(options.Model))
        {
            throw new PaRouterException(
                ErrorCode.ControlUnavailable,
                "A hosted model alias cannot be used for local embeddings.")
                .WithMetadata("reason", "model_is_cloud_hosted");
        }

        // Il tetto strutturale, non quello dell'installazione: qui si verifica cio' che il codice
        // puo' rappresentare, mentre quanto questa installazione ammette lo decide la validazione
        // della configurazione, che e' l'unica a sapere che cosa l'operatore ha dichiarato.
        if (options.Dimension is < 1 or > MemoryLimits.AbsoluteMaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The embedding dimension must stay between 1 and "
                + MemoryLimits.AbsoluteMaxDimension.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        this.options = options;
        this.client = client;
        this.ownsClient = ownsClient;
    }

    /// <inheritdoc />
    public int MaxBatchSize => OllamaEmbeddingOptions.MaxBatch;

    /// <inheritdoc />
    public EmbeddingFingerprint Fingerprint => options.Fingerprint;

    /// <inheritdoc />
    public async ValueTask<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (inputs.Count == 0)
        {
            return new EmbeddingBatch(Fingerprint, []);
        }

        if (inputs.Count > MaxBatchSize)
        {
            throw new PaRouterException(
                ErrorCode.InputInvalid,
                "The embedding batch exceeds the configured limit.")
                .WithMetadata("batch", inputs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithMetadata("maxBatch", MaxBatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (inputs.Any(string.IsNullOrEmpty))
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "An embedding input cannot be empty.");
        }

        await batchGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await EmbedBatchAsync(inputs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            batchGate.Release();
        }
    }

    private async Task<EmbeddingBatch> EmbedBatchAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        // Tre tentativi in tutto, con le attese della specifica: un guasto transitorio del
        // servizio locale non deve far fallire un job di indicizzazione al primo colpo.
        for (int attempt = 0; ; attempt++)
        {
            EmbeddingBatch? batch = await TryEmbedAsync(inputs, cancellationToken).ConfigureAwait(false);

            if (batch is not null)
            {
                return batch;
            }

            if (attempt >= OllamaEmbeddingOptions.Backoff.Length)
            {
                throw new PaRouterException(
                    ErrorCode.EmbeddingUnavailable,
                    "The local embedding model did not answer.")
                    .WithMetadata("model", options.Model);
            }

            await Task.Delay(OllamaEmbeddingOptions.Backoff[attempt], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<EmbeddingBatch?> TryEmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OllamaEmbeddingOptions.Timeout);

        // La permanenza in memoria la sceglie l'operatore (il setup la propone dalla memoria del computer). Conta: Ollama
        // scarica prima chi ha la permanenza piu' breve e, a pari permanenza, per nome, e con la stessa durata delle chat
        // «embeddinggemma» usciva a ogni turno e rientrando buttava fuori il modello che doveva rispondere.
        string body = options.KeepAliveMinutes is int keep
            ? JsonSerializer.Serialize(new { model = options.Model, input = inputs, keep_alive = OllamaKeepAlive.Format(keep) })
            : JsonSerializer.Serialize(new { model = options.Model, input = inputs });

        HttpResponseMessage response;

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri(options.Endpoint, EmbedPath))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                // Come per la chat: un redirect non viene seguito ed e' un controllo mancante.
                throw new PaRouterException(
                    ErrorCode.ControlUnavailable,
                    "The local embedding endpoint answered with a redirect, which is not followed.");
            }

            if (response.StatusCode is not HttpStatusCode.OK)
            {
                return null;
            }

            try
            {
                int limit = checked(inputs.Count * options.Dimension * 32 + BoundedHttpContent.ErrorBytes);
                string payload = await BoundedHttpContent.ReadUtf8Async(response.Content, limit, timeout.Token).ConfigureAwait(false);
                return Parse(payload, inputs.Count);
            }
            catch (PaRouterException ex) when (ex.Code == ErrorCode.ProtocolError)
            {
                throw Invalid("malformed_response");
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    private EmbeddingBatch Parse(string payload, int expectedCount)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            throw Invalid("malformed_response");
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !document.RootElement.TryGetProperty("embeddings", out JsonElement embeddings)
                || embeddings.ValueKind is not JsonValueKind.Array
                || embeddings.GetArrayLength() != expectedCount)
            {
                throw Invalid("unexpected_shape");
            }

            List<float[]> vectors = new(expectedCount);

            foreach (JsonElement embedding in embeddings.EnumerateArray())
            {
                vectors.Add(ReadVector(embedding));
            }

            return new EmbeddingBatch(Fingerprint, vectors);
        }
    }

    private float[] ReadVector(JsonElement embedding)
    {
        if (embedding.ValueKind is not JsonValueKind.Array || embedding.GetArrayLength() != options.Dimension)
        {
            throw Invalid("dimension_mismatch");
        }

        float[] vector = new float[options.Dimension];
        double[] components = new double[options.Dimension];
        double sumOfSquares = 0;
        int index = 0;

        foreach (JsonElement component in embedding.EnumerateArray())
        {
            if (component.ValueKind is not JsonValueKind.Number)
            {
                throw Invalid("non_numeric_component");
            }

            double value = component.GetDouble();

            if (!double.IsFinite(value) || !float.IsFinite((float)value))
            {
                // NaN o infinito renderebbero il coseno privo di significato.
                throw Invalid("non_finite_component");
            }
            if (value != 0 && (float)value == 0) { throw Invalid("unrepresentable_component"); }

            components[index++] = value;
            sumOfSquares += value * value;
        }

        double norm = Math.Sqrt(sumOfSquares);

        if (norm <= 0 || !double.IsFinite(norm))
        {
            // Un vettore nullo non ha direzione: il coseno con qualunque altro non e' definito.
            throw Invalid("zero_vector");
        }

        for (int component = 0; component < vector.Length; component++)
        {
            vector[component] = (float)(components[component] / norm);
        }

        return vector;
    }

    private static PaRouterException Invalid(string reason) =>
        new PaRouterException(ErrorCode.EmbeddingUnavailable, "The local embedding response is not usable.")
            .WithMetadata("reason", reason);

    /// <inheritdoc />
    public void Dispose()
    {
        batchGate.Dispose();

        if (ownsClient)
        {
            client.Dispose();
        }
    }
}
