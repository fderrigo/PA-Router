using PA.Router.Abstractions;

namespace PA.Router.Cli;

/// <summary>
/// Exit code stabili della CLI, allineati alla tabella di 03-specs/ERRORS.md. Restano
/// contrattuali per script e CI; la mappatura completa dei casi di esecuzione arriva con S08.
/// </summary>
internal static class ExitCodes
{
    /// <summary>Comando completato.</summary>
    internal const int Success = 0;

    /// <summary>Configurazione o input non validi.</summary>
    internal const int InvalidInput = 2;

    /// <summary>La policy ha negato ogni candidato.</summary>
    internal const int PolicyDenied = 3;

    /// <summary>Risorsa occupata.</summary>
    internal const int Busy = 4;

    /// <summary>Errore di trasporto o di protocollo.</summary>
    internal const int TransportError = 5;

    /// <summary>Esito incerto: richiede recovery dell'operatore.</summary>
    internal const int Uncertain = 6;

    /// <summary>Store o crypto non disponibili prima di qualunque effetto.</summary>
    internal const int StoreUnavailable = 7;

    /// <summary>Argomenti mancanti o non validi.</summary>
    internal const int UsageError = 64;

    /// <summary>Interruzione richiesta dall'operatore.</summary>
    internal const int Cancelled = 130;

    /// <summary>Exit code per gli errori che la CLI puo' incontrare prima di ogni dispatch.</summary>
    internal static int ForStorage(ErrorCode code) => code switch
    {
        ErrorCode.StoreUnavailable => StoreUnavailable,
        ErrorCode.StoreInUse => StoreUnavailable,
        ErrorCode.CryptoUnavailable => StoreUnavailable,
        ErrorCode.ContentUnavailable => TransportError,
        ErrorCode.ProtocolError => TransportError,
        ErrorCode.RuntimeVersionMismatch => InvalidInput,
        ErrorCode.InputInvalid => InvalidInput,
        ErrorCode.WorkspaceDirty => InvalidInput,
        ErrorCode.SessionBusy => Busy,
        ErrorCode.WorkspaceBusy => Busy,
        ErrorCode.Busy => Busy,
        ErrorCode.Cancelled => Cancelled,
        ErrorCode.NoAdmissibleTarget => PolicyDenied,
        ErrorCode.ControlUnavailable => PolicyDenied,
        ErrorCode.TargetUnavailable => Busy,
        ErrorCode.AuthUnavailable => Busy,
        ErrorCode.QuotaExhausted => Busy,
        ErrorCode.PolicyInvalid => InvalidInput,
        ErrorCode.SideEffectUncertain => Uncertain,

        // Un permesso mancante e' un rifiuto della policy, non un guasto di trasporto: senza questi casi finivano nel
        // ramo generico e uno script non poteva distinguerli.
        ErrorCode.MemoryScopeDenied => PolicyDenied,
        ErrorCode.ExtensionPermissionDenied => PolicyDenied,
        ErrorCode.ToolSourceMissing => InvalidInput,
        ErrorCode.ToolCredentialsMissing => InvalidInput,
        ErrorCode.ToolNotAttested => PolicyDenied,
        ErrorCode.ToolAttestationRejected => PolicyDenied,
        ErrorCode.ToolChanged => PolicyDenied,
        ErrorCode.ToolArgumentsDenied => PolicyDenied,
        ErrorCode.ToolResultDenied => PolicyDenied,
        ErrorCode.NoToolCapableModel => PolicyDenied,
        ErrorCode.ToolSourceUnavailable => Busy,
        ErrorCode.ToolCredentialsRejected => Busy,
        ErrorCode.ToolLimit => TransportError,
        _ => TransportError,
    };
}
