namespace PA.Router.Abstractions;

/// <summary>
/// Archivio dei contenuti. Il contenuto in chiaro non tocca mai il disco: la cifratura avviene
/// prima della scrittura e la scrittura e' atomica (file temporaneo, fsync, rename).
/// </summary>
public interface IContentStore
{
    /// <summary>Modo di conservazione servito da questa istanza.</summary>
    ContentMode Mode { get; }

    /// <summary>Scrive il contenuto cifrato e restituisce i riferimenti da salvare nel DB.</summary>
    ValueTask<ContentBlobReceipt> WriteAsync(
        ContentBlobRequest request,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken);

    /// <summary>
    /// Rilegge e decifra un blob. Verifica prima il digest del file: un blob manomesso o mancante
    /// produce un errore tipizzato, non un contenuto parziale.
    /// </summary>
    ValueTask<byte[]> ReadAsync(
        ContentBlobRequest request,
        ContentBlobReceipt receipt,
        CancellationToken cancellationToken);

    /// <summary>Elimina il blob. Idempotente: un blob gia' assente non e' un errore.</summary>
    ValueTask DeleteAsync(ContentBlobReceipt receipt, CancellationToken cancellationToken);
}
