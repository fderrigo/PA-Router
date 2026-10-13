namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Identita' completa del modello di embedding.
///
/// Include provider, modello, digest osservato, dimensione e versione del preprocessing: due
/// vettori con fingerprint diversi non sono confrontabili, e mescolarli produrrebbe distanze
/// prive di significato (03-specs/SEMANTIC-SEARCH.md).
/// </summary>
/// <param name="Provider">Provider che ha prodotto i vettori.</param>
/// <param name="Model">Modello locale usato.</param>
/// <param name="ModelDigest">Digest del modello osservato, quando disponibile.</param>
/// <param name="Dimension">Dimensione dei vettori.</param>
/// <param name="PreprocessingVersion">Versione della normalizzazione applicata.</param>
public readonly record struct EmbeddingFingerprint(
    string Provider,
    string Model,
    string? ModelDigest,
    int Dimension,
    int PreprocessingVersion)
{
    /// <summary>Versione corrente del preprocessing: normalizzazione L2 su float32.</summary>
    public const int CurrentPreprocessingVersion = 1;

    /// <summary>Forma testuale stabile, usata come chiave di indice e di cache.</summary>
    public override string ToString() =>
        Provider + "|" + Model + "|" + (ModelDigest ?? "unknown")
        + "|" + Dimension.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "|v" + PreprocessingVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Vettori prodotti per un batch, con l'identita' del modello che li ha generati.</summary>
/// <param name="Fingerprint">Identita' del modello.</param>
/// <param name="Vectors">Vettori normalizzati, nello stesso ordine degli ingressi.</param>
public sealed record EmbeddingBatch(EmbeddingFingerprint Fingerprint, IReadOnlyList<float[]> Vectors)
{
    /// <summary>Nessun vettore nella resa testuale: sono dati protetti come il testo.</summary>
    public override string ToString() =>
        "EmbeddingBatch { " + Fingerprint + ", Vectors = "
        + Vectors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " }";
}

/// <summary>
/// Provider di embedding locali.
///
/// E' un target come gli altri: la richiesta di embedding passa dal gate ausiliario della policy
/// prima di arrivare qui, e non esiste alcun ripiego su un servizio cloud se il modello locale
/// manca (03-specs/POLICY.md, «gate delle elaborazioni ausiliarie»).
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Numero massimo di testi in un solo batch.</summary>
    int MaxBatchSize { get; }

    /// <summary>Identita' del modello configurato, senza contattarlo.</summary>
    EmbeddingFingerprint Fingerprint { get; }

    /// <summary>Produce i vettori dei testi indicati.</summary>
    ValueTask<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken);
}
