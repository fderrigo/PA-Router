using PA.Router.Abstractions.Contracts;

namespace PA.Router.Search.Local;

/// <summary>
/// Reciprocal Rank Fusion delle due liste (03-specs/SEMANTIC-SEARCH.md).
///
/// <c>score(d) = 1/(60 + rank_vettoriale) + w/(60 + rank_lessicale)</c>, con i rank che partono da
/// 1 e una lista assente che contribuisce zero. La fusione usa le posizioni, non i punteggi:
/// coseno e BM25 vivono su scale diverse e sommarli direttamente non significherebbe nulla.
///
/// Il peso <c>w</c> della lista lessicale esiste perche' la pari dignita' presuppone due
/// recuperatori di qualita' confrontabile, e misurando si e' visto che non lo sono: fondere alla
/// pari peggiorava il recupero rispetto ai soli vettori. Vedere
/// <see cref="MemoryLimits.HybridLexicalWeight"/> per i numeri.
///
/// Questo ranking non contiene pesi di privacy: l'ambito e' gia' stato imposto prima, e non si
/// recupera con un punteggio piu' basso cio' che non era autorizzato.
/// </summary>
public static class RankFusion
{
    /// <summary>Fonde le due liste ordinate e restituisce gli identificatori con il loro punteggio.</summary>
    /// <param name="vector">Lista dei vettori, vuota quando l'embedding non e' disponibile.</param>
    /// <param name="lexical">Lista lessicale.</param>
    /// <param name="lexicalWeight">
    /// Peso della lista lessicale. Vale 1 quando e' l'unica lista disponibile: in quel caso non c'e'
    /// nulla con cui fondere, e ridurne il peso sposterebbe soltanto tutti i punteggi.
    /// </param>
    public static IReadOnlyList<(string Id, double Score)> Fuse(
        IReadOnlyList<string> vector,
        IReadOnlyList<string> lexical,
        double lexicalWeight = 1.0)
    {
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(lexical);

        if (lexicalWeight is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lexicalWeight), "The lexical weight must stay in (0, 1].");
        }

        Dictionary<string, double> scores = new(StringComparer.Ordinal);

        Accumulate(scores, vector, 1.0);

        Accumulate(scores, lexical, lexicalWeight);

        return [.. scores
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => (entry.Key, entry.Value))];
    }

    private static void Accumulate(Dictionary<string, double> scores, IReadOnlyList<string> ranked, double weight)
    {
        for (int index = 0; index < ranked.Count; index++)
        {
            string id = ranked[index];
            double contribution = weight / (MemoryLimits.FusionConstant + index + 1);

            scores[id] = scores.TryGetValue(id, out double existing) ? existing + contribution : contribution;
        }
    }

    /// <summary>
    /// Retains each list's rank while using exactly the public fusion calculation and tie-break.
    /// These positions explain ordering; they are not calibrated relevance probabilities.
    /// </summary>
    internal static IReadOnlyList<ChunkFusionEvidence> FuseWithEvidence(
        IReadOnlyList<string> vector,
        IReadOnlyList<string> lexical,
        double lexicalWeight)
    {
        IReadOnlyList<(string Id, double Score)> fused = Fuse(vector, lexical, lexicalWeight);
        Dictionary<string, int> vectorRanks = Ranks(vector);
        Dictionary<string, int> lexicalRanks = Ranks(lexical);

        return [.. fused.Select(item => new ChunkFusionEvidence(
            item.Id,
            item.Score,
            vectorRanks.TryGetValue(item.Id, out int vectorRank) ? vectorRank : null,
            lexicalRanks.TryGetValue(item.Id, out int lexicalRank) ? lexicalRank : null))];
    }

    private static Dictionary<string, int> Ranks(IReadOnlyList<string> ranked)
    {
        Dictionary<string, int> result = new(StringComparer.Ordinal);
        for (int index = 0; index < ranked.Count; index++)
        {
            result.TryAdd(ranked[index], index + 1);
        }

        return result;
    }
}
