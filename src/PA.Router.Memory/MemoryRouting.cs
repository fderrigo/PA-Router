using PA.Router.Abstractions.Contracts;

namespace PA.Router.Memory;

/// <summary>
/// Destinazione dell'indice di un ricordo.
///
/// Fino al 24 settembre 2026 un ricordo pubblico poteva andare a un indice potenziato fuori dalla macchina
/// (<c>memory.enhancedEmbeddingServiceId</c>). ADR-032 l'ha tolto per decisione dell'operatore: ogni ricordo nuovo va
/// all'indice locale. La destinazione <c>enhanced</c> resta nello schema dello store per i ricordi nati prima, che si
/// cercano per testo e non hanno piu' un indice vettoriale proprio.
/// </summary>
public static class MemoryRouting
{
    /// <summary>
    /// Vero se la destinazione indicata e' ammissibile per la classe indicata. Serve a chi riceve
    /// una destinazione gia' decisa — un lavoro in coda, una riga letta dallo store — e non deve
    /// fidarsene senza verificarla: fra la decisione e l'uso puo' essere passato di tutto.
    /// </summary>
    public static bool IsAdmissible(MemoryDestination destination, DataClass classification) =>
        destination is MemoryDestination.Local || classification is DataClass.Public;
}
