using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Memory;

namespace PA.Router.Core;

/// <summary>Esito dell'auto-injection, da mostrare a chi ha chiesto il turno.</summary>
/// <param name="Items">Elementi da aggiungere al payload.</param>
/// <param name="Selected">Ricordi scelti.</param>
/// <param name="Excluded">Ricordi rimasti fuori dal budget.</param>
/// <param name="UsedBytes">Byte occupati.</param>
/// <param name="Status">Stato della memoria.</param>
/// <param name="Mode">Modo con cui sono stati trovati.</param>
public sealed record MemoryAugmentationResult(
    IReadOnlyList<ContextItem> Items,
    IReadOnlyList<string> Selected,
    IReadOnlyList<string> Excluded,
    int UsedBytes,
    MemoryIndexStatus Status,
    MemorySearchMode Mode)
{
    /// <summary>Nessuna memoria disponibile, con il motivo.</summary>
    public static MemoryAugmentationResult Unavailable(MemorySearchMode mode) =>
        new([], [], [], 0, MemoryIndexStatus.Unavailable, mode);
}

/// <summary>
/// Auto-injection della memoria prima del routing (03-specs/MEMORY.md).
///
/// L'ordine e' quello della specifica: si cerca nello scope autorizzato, si scelgono al massimo
/// topK ricordi entro il budget, si aggiungono al payload come elementi di contesto con la loro
/// provenienza — e solo dopo il payload completo torna alla privacy e alla policy. Il ricordo
/// entra quindi nella decisione come dato dell'utente, non come istruzione privilegiata.
///
/// Se la memoria non si puo' leggere e il profilo la richiede, il turno non parte: e' preferibile a
/// una risposta costruita senza il contesto che qualcuno riteneva necessario. Se non la richiede, il
/// turno prosegue e lo stato viene dichiarato. Un modello di embedding assente non rende la memoria
/// illeggibile: la ricerca risponde per parole e lo dichiara, con o senza <c>memory.required</c>.
/// </summary>
public sealed class MemoryAugmentation
{
    private readonly IMemorySearch search;
    private readonly bool required;
    private readonly int topK;

    /// <param name="search">Motore di ricerca gia' costruito con l'ambito dell'host.</param>
    /// <param name="required">Vero se il profilo richiede la memoria.</param>
    /// <param name="topK">Ricordi massimi da iniettare.</param>
    public MemoryAugmentation(IMemorySearch search, bool required, int topK = MemoryLimits.DefaultSearchLimit)
    {
        ArgumentNullException.ThrowIfNull(search);

        if (topK is < 1 or > MemoryLimits.MaxSearchLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(topK));
        }

        this.search = search;
        this.required = required;
        this.topK = topK;
    }

    /// <summary>Costruisce il contesto di memoria per la richiesta indicata.</summary>
    /// <param name="scope">Ambito autorizzato della ricerca.</param>
    /// <param name="input">Testo dell'operatore.</param>
    /// <param name="cancellationToken">Annullamento.</param>
    /// <param name="alreadyInContext">
    /// Esecuzioni gia' presenti nel payload come cronologia: i ricordi che ne sono nati non si
    /// iniettano una seconda volta.
    /// </param>
    public async ValueTask<MemoryAugmentationResult> AugmentAsync(
        MemoryScope scope,
        string input,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? alreadyInContext = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(input);

        MemorySearchMode mode = MemorySearchMode.LexicalOnly;

        try
        {
            // topK conta i ricordi iniettati, non i candidati prima di cronologia, deduplica e budget.
            MemorySearchResult result = await search.SearchAsync(scope, input, MemoryLimits.MaxSearchLimit, cancellationToken).ConfigureAwait(false);
            mode = result.Mode;

            if (result.Hits.Count == 0)
            {
                return new MemoryAugmentationResult([], [], [], 0, result.IndexStatus, result.Mode);
            }

            MemoryContext context = await BuildContextAsync(scope, result, alreadyInContext, cancellationToken).ConfigureAwait(false);

            return new MemoryAugmentationResult(
                context.Items,
                context.Selected,
                context.Excluded,
                context.UsedBytes,
                context.Status,
                context.Mode);
        }
        catch (PaRouterException ex) when (required && ex.Code is ErrorCode.ContentUnavailable or ErrorCode.StoreUnavailable)
        {
            // Ricerca e rilettura condividono il contratto: un archivio illeggibile blocca il turno richiesto.
            // Deny e cancellazione mantengono il proprio significato; nessun messaggio del contenuto viene propagato.
            throw new PaRouterException(ErrorCode.MemoryUnavailable, "The memory contents are unavailable.")
                .WithMetadata("cause", ex.Code.ToWire());
        }
        catch (PaRouterException) when (!required)
        {
            // Anche un guasto durante la rilettura rende la memoria indisponibile: nessun risultato parziale entra.
            return MemoryAugmentationResult.Unavailable(mode);
        }
    }

    private async ValueTask<MemoryContext> BuildContextAsync(
        MemoryScope scope,
        MemorySearchResult result,
        IReadOnlySet<string>? alreadyInContext,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> ids = [.. result.Hits.Select(hit => hit.ObservationId)];
        IReadOnlyList<ObservationRecord> bodies;
        if (search is IMemoryContextReader reader)
        {
            bodies = await reader.GetForContextAsync(scope, ids, cancellationToken).ConfigureAwait(false);
        }
        else if (ids.Count <= MemoryLimits.MaxFetchIds)
        {
            // Compatibilita' con implementazioni che espongono solo get: una sola rilettura entro il loro contratto.
            bodies = await search.GetAsync(scope, ids, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Non ricomporre batch: una revoca durante una lettura successiva renderebbe vecchi i corpi precedenti.
            throw new PaRouterException(ErrorCode.MemoryUnavailable, "The memory search cannot revalidate the context candidates in one read.");
        }

        // Nessun altro await fra la rilettura dello scope e la costruzione del contesto.
        return MemoryContextBuilder.Build(result, bodies, topK, MemoryContextBuilder.BudgetBytes, alreadyInContext);
    }
}
