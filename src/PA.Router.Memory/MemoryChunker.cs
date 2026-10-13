using System.Text;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Memory;

/// <summary>
/// Divisione deterministica del testo di un'osservazione (03-specs/SEMANTIC-SEARCH.md).
///
/// Deterministica significa che lo stesso testo produce sempre gli stessi confini: un reindex non
/// deve spostare i chunk, altrimenti gli HMAC cambierebbero e i vecchi vettori non
/// corrisponderebbero piu' a nulla.
///
/// I tagli cadono su confini Unicode — mai in mezzo a una sequenza UTF-8 o a una coppia surrogata
/// — e preferiscono i confini di paragrafo, poi di riga, poi di spazio: un chunk tagliato a meta'
/// di una parola perde significato proprio dove serve.
/// </summary>
public static class MemoryChunker
{
    /// <summary>
    /// Divide il testo in chunk. Gli offset sono in byte UTF-8, come li vuole
    /// <c>memory_chunks.start_byte</c>.
    /// </summary>
    /// <param name="observationId">Osservazione di origine.</param>
    /// <param name="text">Testo sintetico dell'osservazione.</param>
    /// <param name="digest">Calcolo dell'HMAC del contenuto.</param>
    /// <param name="ids">Generatore degli identificatori dei chunk.</param>
    public static IReadOnlyList<MemoryChunk> Split(
        string observationId,
        string text,
        IMetadataDigest digest,
        IIdGenerator ids)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationId);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(ids);

        byte[] bytes = Encoding.UTF8.GetBytes(text);

        if (bytes.Length == 0)
        {
            throw new PaRouterException(ErrorCode.InputInvalid, "An observation cannot be empty.");
        }

        if (bytes.Length > MemoryLimits.MaxObservationBytes)
        {
            throw new PaRouterException(ErrorCode.MemoryLimit, "The observation text exceeds the declared limit.")
                .WithMetadata("bytes", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithMetadata("maxBytes", MemoryLimits.MaxObservationBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        List<MemoryChunk> chunks = [];
        int start = 0;
        int ordinal = 0;

        while (start < bytes.Length)
        {
            int end = ChooseEnd(bytes, start);
            int length = end - start;

            string chunkText = Encoding.UTF8.GetString(bytes, start, length);

            chunks.Add(new MemoryChunk(
                ids.NewId(),
                observationId,
                ordinal++,
                digest.Compute(bytes.AsSpan(start, length)),
                start,
                length,
                chunkText));

            if (end >= bytes.Length)
            {
                break;
            }

            start = NextStart(bytes, start, end);
        }

        return chunks;
    }

    /// <summary>
    /// Fine del chunk che comincia a <paramref name="start"/>: il massimo consentito, arretrato
    /// fino al confine piu' significativo disponibile.
    /// </summary>
    private static int ChooseEnd(byte[] bytes, int start)
    {
        int limit = Math.Min(start + MemoryLimits.MaxChunkBytes, bytes.Length);

        if (limit >= bytes.Length)
        {
            return bytes.Length;
        }

        // Meta' del chunk e' il punto oltre il quale arretrare costerebbe piu' di quanto rende:
        // un confine trovato prima produrrebbe chunk molto piu' corti del necessario.
        int floor = start + (MemoryLimits.MaxChunkBytes / 2);

        int paragraph = LastIndexOf(bytes, start, limit, (byte)'\n', doubled: true);

        if (paragraph > floor)
        {
            return paragraph;
        }

        int line = LastIndexOf(bytes, start, limit, (byte)'\n', doubled: false);

        if (line > floor)
        {
            return line;
        }

        int space = LastIndexOf(bytes, start, limit, (byte)' ', doubled: false);

        if (space > floor)
        {
            return space;
        }

        // Nessun confine utile: si taglia al limite, arretrando fino a un confine di carattere.
        return AlignToCharacterBoundary(bytes, limit);
    }

    /// <summary>
    /// Inizio del chunk successivo: arretra fino a <see cref="MemoryLimits.MaxChunkOverlapBytes"/>
    /// per non perdere una frase a cavallo del taglio, senza mai tornare prima dell'inizio del
    /// chunk corrente — un overlap che non avanza girerebbe all'infinito.
    /// </summary>
    private static int NextStart(byte[] bytes, int start, int end)
    {
        int overlap = Math.Min(MemoryLimits.MaxChunkOverlapBytes, (end - start) / 2);
        int candidate = AlignToCharacterBoundary(bytes, end - overlap);

        return candidate > start ? candidate : end;
    }

    /// <summary>Ultima occorrenza del separatore, restituita come indice successivo al confine.</summary>
    private static int LastIndexOf(byte[] bytes, int start, int limit, byte separator, bool doubled)
    {
        for (int index = limit - 1; index > start; index--)
        {
            if (bytes[index] != separator)
            {
                continue;
            }

            if (doubled && (index == start || bytes[index - 1] != separator))
            {
                continue;
            }

            return index + 1;
        }

        return -1;
    }

    /// <summary>
    /// Arretra fino all'inizio di un carattere UTF-8. I byte di continuazione hanno la forma
    /// <c>10xxxxxx</c>: tagliarci in mezzo produrrebbe byte non decodificabili.
    /// </summary>
    private static int AlignToCharacterBoundary(byte[] bytes, int index)
    {
        int aligned = Math.Min(index, bytes.Length);

        while (aligned > 0 && aligned < bytes.Length && (bytes[aligned] & 0xC0) == 0x80)
        {
            aligned--;
        }

        return aligned;
    }
}
