using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Search.Local;

/// <summary>
/// Confronto vettoriale esatto in .NET.
///
/// Esatto e non approssimato: su un corpus personale limitato a 20.000 chunk il costo e'
/// misurabile, e in cambio non entra nella beta un'altra libreria nativa da distribuire. Non e'
/// una promessa di scalabilita' illimitata (03-specs/SEMANTIC-SEARCH.md).
///
/// I vettori arrivano gia' normalizzati L2, quindi il coseno e' il prodotto scalare; il risultato
/// viene comunque limitato a [-1,1] perche' l'aritmetica in virgola mobile puo' uscire di un
/// epsilon dal dominio, e un valore fuori dominio sporcherebbe i confronti.
/// </summary>
public static class VectorSearch
{
    /// <summary>Coseno fra due vettori normalizzati, limitato al dominio.</summary>
    public static double Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length)
        {
            throw new PaRouterException(
                ErrorCode.EmbeddingVersionMismatch,
                "Vectors of different dimensions are not comparable.");
        }

        double sum = 0;

        for (int index = 0; index < left.Length; index++)
        {
            sum += (double)left[index] * right[index];
        }

        return Math.Clamp(sum, -1.0, 1.0);
    }

    /// <summary>
    /// Chunk piu' vicini alla query, ordinati per coseno decrescente e, a parita', per identificatore
    /// crescente: l'ordine deve essere lo stesso a ogni esecuzione.
    /// </summary>
    /// <param name="entries">Frammenti dell'ambito.</param>
    /// <param name="query">Vettore della domanda.</param>
    /// <param name="take">Quanti restituirne al massimo.</param>
    /// <param name="minimum">
    /// Coseno minimo perche' un frammento sia un risultato. Zero significa «nessun pavimento», che e'
    /// il comportamento di chi chiama senza chiederne uno; la ricerca della memoria lo passa dalla
    /// configurazione, perche' «il piu' vicino» e «pertinente» non sono la stessa cosa.
    /// </param>
    public static IReadOnlyList<string> Rank(
        IReadOnlyList<MemoryIndexEntry> entries,
        float[] query,
        int take,
        double minimum = 0)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);
        List<(string ChunkId, double Cosine)> scored = [];
        foreach (MemoryIndexEntry entry in entries)
        {
            if (entry.Vector is not float[] vector) { continue; }
            double similarity = Cosine(vector, query);
            if (!(similarity < minimum)) { scored.Add((entry.ChunkId, similarity)); }
        }

        // Ordinary search does not allocate diagnostic evidence for every rejected chunk.
        return [.. scored.OrderByDescending(item => item.Cosine)
            .ThenBy(item => item.ChunkId, StringComparer.Ordinal)
            .Take(take)
            .Select(item => item.ChunkId)];
    }

    /// <summary>
    /// Scores the authorized snapshot once, retaining cosine before any relevance floor.
    /// Public ranking keeps its existing floor and limit; diagnostics can inspect what it loses.
    /// </summary>
    internal static IReadOnlyList<VectorChunkScore> Score(
        IReadOnlyList<MemoryIndexEntry> entries,
        float[] query)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);

        List<VectorChunkScore> scored = [];

        foreach (MemoryIndexEntry entry in entries)
        {
            if (entry.Vector is not float[] vector)
            {
                // Chunk non ancora indicizzato: non partecipa, e la ricerca lo dichiara come
                // indice incompleto invece di fingere che non esista.
                continue;
            }

            double similarity = Cosine(vector, query);

            scored.Add(new VectorChunkScore(entry.ChunkId, similarity));
        }

        return [.. scored
            .OrderByDescending(item => item.Cosine)
            .ThenBy(item => item.ChunkId, StringComparer.Ordinal)];
    }
}
