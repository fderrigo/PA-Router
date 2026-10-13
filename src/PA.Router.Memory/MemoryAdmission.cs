using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Memory;

/// <summary>Esito dell'esame di ammissibilita' di un ricordo.</summary>
/// <param name="Admitted">Vero se l'osservazione puo' essere conservata.</param>
/// <param name="Indexable">Vero se puo' anche entrare nell'indice persistente.</param>
/// <param name="Scope">Ampiezza consentita, che puo' essere piu' stretta di quella richiesta.</param>
/// <param name="Reason">Motivo del rifiuto o della restrizione, in forma stabile.</param>
public readonly record struct MemoryAdmissionResult(
    bool Admitted,
    bool Indexable,
    MemoryScopeKind Scope,
    string? Reason);

/// <summary>
/// Regole di ingresso della memoria (03-specs/MEMORY.md, «Flusso in ingresso»).
///
/// La classificazione decide prima dello scope richiesto: un dato personale non diventa memoria di
/// progetto perche' qualcuno l'ha chiesto, e un segreto non entra affatto. La restrizione avviene
/// qui, prima della scrittura, non al momento della lettura: un ricordo scritto troppo largo
/// resterebbe largo anche dopo un cambio di policy.
/// </summary>
public static class MemoryAdmission
{
    /// <summary>Motivo: la classe secret non entra nella memoria della beta.</summary>
    public const string SecretExcluded = "secret_excluded";

    /// <summary>Motivo: classe incerta, utilizzabile nel turno ma non indicizzabile.</summary>
    public const string UnknownNotIndexable = "unknown_not_indexable";

    /// <summary>Motivo: classe sensibile ristretta alla sola sessione.</summary>
    public const string RestrictedToSession = "restricted_to_session";

    /// <summary>Motivo: la pubblicazione al chatbot richiede lo scope di progetto.</summary>
    public const string ChatbotRequiresProjectScope = "chatbot_requires_project_scope";

    /// <summary>
    /// Decide se e come conservare un'osservazione.
    /// </summary>
    /// <param name="classification">Classe osservata dalla privacy, non dichiarata dall'agente.</param>
    /// <param name="requestedScope">Ampiezza richiesta da chi produce il ricordo.</param>
    /// <param name="publishedForChatbot">Vero se l'operatore vuole pubblicarla per il chatbot.</param>
    public static MemoryAdmissionResult Evaluate(
        DataClass classification,
        MemoryScopeKind requestedScope,
        bool publishedForChatbot = false)
    {
        if (classification is DataClass.Secret)
        {
            // Un segreto non viene conservato in nessuna forma: non e' una questione di ampiezza.
            return new MemoryAdmissionResult(false, false, MemoryScopeKind.Session, SecretExcluded);
        }

        if (classification is DataClass.Unknown)
        {
            // Puo' servire nel turno corrente, ma indicizzarlo significherebbe conservare per
            // sempre un dato di cui non si conosce la classe.
            return new MemoryAdmissionResult(true, false, MemoryScopeKind.Session, UnknownNotIndexable);
        }

        bool shareable = classification is DataClass.Public or DataClass.Internal;

        if (!shareable)
        {
            if (publishedForChatbot)
            {
                return new MemoryAdmissionResult(false, false, MemoryScopeKind.Session, ChatbotRequiresProjectScope);
            }

            // Personale o sensibile: resta nella sessione che l'ha prodotto, anche se e' stata
            // chiesta la memoria di progetto.
            return new MemoryAdmissionResult(
                true,
                true,
                MemoryScopeKind.Session,
                requestedScope is MemoryScopeKind.Project ? RestrictedToSession : null);
        }

        if (publishedForChatbot && requestedScope is not MemoryScopeKind.Project)
        {
            return new MemoryAdmissionResult(false, false, MemoryScopeKind.Session, ChatbotRequiresProjectScope);
        }

        return new MemoryAdmissionResult(true, true, requestedScope, null);
    }

    /// <summary>
    /// Verifica che un'osservazione gia' costruita rispetti le regole, e lancia altrimenti. Serve
    /// allo store come ultima difesa: le stesse condizioni sono CHECK dello schema, e arrivare
    /// fin la' con una riga non valida significherebbe scoprirlo come errore SQLite.
    /// </summary>
    public static void EnsureConsistent(ObservationRecord observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        MemoryAdmissionResult admission = Evaluate(
            observation.Classification,
            observation.Scope,
            observation.PublishedForChatbot);

        if (!admission.Admitted)
        {
            throw new PaRouterException(ErrorCode.MemoryScopeDenied, "This observation cannot be stored.")
                .WithMetadata("reason", admission.Reason ?? "not_admitted");
        }

        if (admission.Scope != observation.Scope)
        {
            throw new PaRouterException(ErrorCode.MemoryScopeDenied, "This observation asks for a wider scope than its class allows.")
                .WithMetadata("reason", admission.Reason ?? RestrictedToSession);
        }
    }
}
