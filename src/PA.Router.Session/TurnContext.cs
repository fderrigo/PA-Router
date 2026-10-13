using System.Text;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Session;

/// <summary>Contesto di un turno dopo il taglio per budget.</summary>
/// <param name="Items">Elementi che restano, nell'ordine di ingresso.</param>
/// <param name="Bytes">Byte occupati dagli elementi rimasti.</param>
/// <param name="OmittedMemoryIds">Ricordi lasciati fuori, in ordine di identificatore.</param>
/// <param name="OmittedTurns">Turni piu' vecchi lasciati fuori.</param>
public sealed record TrimmedContext(
    IReadOnlyList<ContextItem> Items,
    int Bytes,
    IReadOnlyList<string> OmittedMemoryIds,
    int OmittedTurns);

/// <summary>
/// Taglio del contesto di un turno quando il budget del profilo non basta
/// (03-specs/SESSION-HANDOVER.md, «Costruzione contesto»).
///
/// L'ordine di rimozione e' fissato: prima i ricordi, in ordine di identificatore, poi i turni piu'
/// vecchi. L'input e i vincoli non si tagliano mai: tagliare un vincolo per far entrare un ricordo
/// significherebbe eseguire una richiesta diversa da quella autorizzata. Se non basta neppure
/// togliere tutto il rimovibile, la richiesta non entra e l'errore e' del chiamante.
///
/// E' il complemento di <see cref="ContextBuilder"/>, che costruisce il testo di un handover: qui
/// gli elementi restano separati, perche' privacy e policy li classificano uno per uno e un
/// payload gia' appiattito perderebbe la provenienza.
/// </summary>
public static class TurnContext
{
    /// <summary>
    /// Riduce il contesto entro il budget indicato. Un contesto che ci sta gia' non viene toccato.
    /// </summary>
    /// <param name="items">Elementi nell'ordine in cui sono stati assemblati.</param>
    /// <param name="budgetBytes">Byte ammessi dal profilo per il contesto allegato.</param>
    public static TrimmedContext Trim(IReadOnlyList<ContextItem> items, int budgetBytes)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);

        int total = items.Sum(SizeOf);

        if (total <= budgetBytes)
        {
            return new TrimmedContext(items, total, [], 0);
        }

        List<ContextItem> kept = [.. items];
        List<string> omittedMemory = [];
        int omittedTurns = 0;

        // I ricordi escono per primi, in ordine di identificatore: l'ordine dev'essere lo stesso su
        // macchine diverse, altrimenti due installazioni identiche costruirebbero contesti diversi.
        foreach (ContextItem memory in items
            .Where(item => item.Provenance is Provenance.Memory)
            .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            if (total <= budgetBytes)
            {
                break;
            }

            kept.Remove(memory);
            omittedMemory.Add(memory.Id);
            total -= SizeOf(memory);
        }

        // Poi i turni, dal piu' vecchio: la cronologia perde la coda, non la testa.
        foreach (ContextItem turn in items.Where(IsHistory))
        {
            if (total <= budgetBytes)
            {
                break;
            }

            if (!kept.Remove(turn))
            {
                continue;
            }

            omittedTurns++;
            total -= SizeOf(turn);
        }

        return new TrimmedContext(kept, total, omittedMemory, omittedTurns);
    }

    /// <summary>
    /// Elemento di cronologia. Gli identificatori li assegna chi costruisce il turno, e sono in
    /// ordine cronologico: togliere dalla testa dell'elenco significa togliere i piu' vecchi.
    /// </summary>
    private static bool IsHistory(ContextItem item) => HistoryItems.Is(item.Id);

    private static int SizeOf(ContextItem item) => Encoding.UTF8.GetByteCount(item.Text);
}
