using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Core;
using PA.Router.Embeddings.Ollama;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Storage;
using PA.Router.Memory;
using PA.Router.Policy;
using PA.Router.Policy.Grants;
using PA.Router.Privacy;
using PA.Router.Registry.Loading;
using PA.Router.Search.Local;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// Memoria locale montata per un comando della CLI.
///
/// Il provider di embedding esiste solo se l'operatore ha configurato modello e fingerprint: con i
/// segnaposto degli esempi la memoria resta leggibile in modo lessicale e lo dichiara, invece di
/// fingere una ricerca semantica. Il provider passa comunque dal gate ausiliario, che verifica
/// policy, classe del testo e fingerprint approvato prima di ogni chiamata.
/// </summary>
public sealed class MemoryStack : IDisposable
{
    private readonly OllamaEmbeddingProvider? provider;
    private readonly IDisposable cacheLease;

    private MemoryStack(
        SqliteMemoryStore store,
        LocalMemorySearch search,
        IDisposable cacheLease,
        OllamaEmbeddingProvider? provider,
        bool configured,
        string? fingerprint)
    {
        Store = store;
        Search = search;
        this.cacheLease = cacheLease;
        this.provider = provider;
        Configured = configured;
        Fingerprint = fingerprint;
    }

    /// <summary>Store delle osservazioni.</summary>
    public SqliteMemoryStore Store { get; }

    /// <summary>Motore di ricerca.</summary>
    public LocalMemorySearch Search { get; }

    /// <summary>Vero se un modello di embedding e' configurato e utilizzabile.</summary>
    public bool Configured { get; }

    /// <summary>Fingerprint approvato, quando configurato.</summary>
    public string? Fingerprint { get; }

    /// <summary>Reindicizza con lo stesso provider gia' autorizzato della ricerca.</summary>
    public ValueTask<(int Enqueued, int Executed)> ReindexAsync(int maxJobs, CancellationToken cancellationToken)
    {
        MemoryIndexer indexer = new(Store, Search.Embeddings);
        return indexer.ReindexAsync(maxJobs, cancellationToken);
    }

    /// <summary>Monta la memoria per l'ambito e la policy indicati.</summary>
    public static MemoryStack Create(
        RouterStack runtime,
        ConfigurationSnapshot snapshot,
        CompiledPolicy policy,
        MemoryScope scope)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(scope);

        IDisposable cacheLease = runtime.MemoryCache.AcquireLease();
        OllamaEmbeddingProvider? provider = null;
        try
        {
            SqliteMemoryStore store = runtime.MemoryStore();
            MemoryConfig memory = snapshot.Config.Memory;
            IEmbeddingProvider? gated = null;
            string? fingerprint = null;

            if (memory is { Enabled: true, IsUnconfigured: false }
            && EmbeddingServiceOf(snapshot) is ServiceDescriptor embedding
            && embedding.Endpoint is Uri endpoint
            && embedding.Model is string model
            && embedding.Dimension is int dimension
            && embedding.Fingerprint is string observed)
            {
                // Senza fingerprint osservato non si indicizza: il gate ausiliario approva un modello
                // preciso, e «un modello qualunque» non e' un'approvazione.
                provider = new OllamaEmbeddingProvider(new OllamaEmbeddingOptions(endpoint, model, dimension) { KeepAliveMinutes = embedding.KeepAliveMinutes });

                fingerprint = observed;

                gated = new MemoryEmbeddingGate(
                    provider,
                    policy,
                    runtime.Scanner(),
                    new GrantService(SystemClock.Instance),
                    runtime.Digest,
                    store,
                    scope,
                    fingerprint);
            }

            LocalMemorySearch search = new(
                store,
                store,
                gated,
                runtime.MemoryCache,
                new MemorySearchOptions());

            return new MemoryStack(store, search, cacheLease, provider, gated is not null, fingerprint);
        }
        catch
        {
            try { provider?.Dispose(); }
            finally { cacheLease.Dispose(); }
            throw;
        }
    }

    /// <summary>
    /// Servizio di embedding scelto dalla configurazione. Modello, dimensione e fingerprint
    /// appartengono al servizio, non alla memoria: due indici prodotti da modelli diversi non
    /// sono confrontabili, e il registro e' l'unico posto dove quel fatto e' dichiarato.
    /// </summary>
    private static ServiceDescriptor? EmbeddingServiceOf(ConfigurationSnapshot snapshot) =>
        snapshot.Config.Memory.EmbeddingServiceId is string serviceId ? ServiceOf(snapshot, serviceId) : null;

    /// <summary>
    /// Servizio di embedding abilitato e con un indirizzo che rispetta la propria collocazione.
    /// La regola di rete la sceglie la collocazione dichiarata, non il codice: un servizio
    /// <c>loopback</c> resta su un indirizzo numerico di loopback, uno <c>lan</c> su un indirizzo
    /// privato, e cosi' via (ADR-016).
    /// </summary>
    private static ServiceDescriptor? ServiceOf(ConfigurationSnapshot snapshot, string serviceId)
    {
        if (snapshot.FindService(serviceId) is not ServiceDescriptor service
            || service.Capability is not ServiceCapability.Embedding
            || !service.Enabled)
        {
            return null;
        }

        return PlacementRules.IsAcceptable(service.InferencePlacement, service.Endpoint) ? service : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            provider?.Dispose();
        }
        finally
        {
            cacheLease.Dispose();
        }
    }
}
