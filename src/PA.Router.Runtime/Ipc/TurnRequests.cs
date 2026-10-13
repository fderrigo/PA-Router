using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Hosting.Runtime;

namespace PA.Router.Runtime.Ipc;

/// <summary>
/// Richiesta di turno per il canale del worker, costruita da un <see cref="TurnRequest"/> intero.
///
/// Prima ogni front end componeva il payload a mano, con i campi che ricordava: `pa run` ricordava
/// testo, profilo, servizio e sessione, e la skill restava fuori. Qui il payload nasce dalla stessa
/// richiesta che userebbe il turno locale, quindi un campo non puo' essere dimenticato da chi la
/// invia (INV-22).
/// </summary>
public static class TurnRequests
{
    /// <summary>
    /// Traduce la richiesta per il worker.
    ///
    /// Contesto del chiamante, istruzioni dell'applicazione, chiave di idempotenza e identificatore
    /// preassegnato appartengono al
    /// percorso dell'host API e il canale non li trasporta. Una richiesta che li porta viene
    /// **rifiutata** invece di essere spedita senza: perderli in silenzio sarebbe esattamente il
    /// difetto che questa classe esiste per impedire.
    /// </summary>
    public static RunRequestPayload ToPayload(TurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.IdempotencyKey is not null
            || request.ExecutionId is not null
            || request.Instructions is not null
            || request.Tools is not null)
        {
            throw new PaRouterException(
                    ErrorCode.InputInvalid,
                    "This turn carries fields that the worker channel does not transport.")
                .WithMetadata("reason", "turn_not_transportable");
        }

        // I contenuti allegati dall'operatore viaggiano dal protocollo 3 (S63). Restano rifiutati i
        // campi del percorso dell'host API — istruzioni dell'applicazione, chiave di idempotenza,
        // identificatore preassegnato, strumenti — che il canale non trasporta e che perdere in
        // silenzio sarebbe il difetto che questa classe esiste per impedire.
        return new RunRequestPayload(
            request.Input,
            request.Profile.ProfileId,
            request.RequestedServiceId,
            request.SessionId,
            null,
            request.SkillId,
            request.RequestedCapabilities is null ? null : TurnInputs.ToWire(request.RequestedCapabilities),
            request.Workspace?.RootPath,
            request.Workspace?.Writable ?? false,
            request.ContextItems is { Count: > 0 }
                ? [.. request.ContextItems.Select(item => new ContextItemPayload(item.Id, item.Text, item.Provenance.ToWire()))]
                : null,
            request.ExternalConfirmed);
    }
}
