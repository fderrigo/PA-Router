namespace PA.Router.Abstractions;

/// <summary>
/// Mappatura esplicita fra <see cref="ErrorCode"/> e la stringa wire. Nessuna conversione
/// implicita da numero: aggiungere un codice senza registrarlo qui fa fallire i test.
/// </summary>
public static class ErrorCodes
{
    private static readonly (ErrorCode Code, string Wire)[] Pairs =
    [
        (ErrorCode.PolicyInvalid, "POLICY_INVALID"),
        (ErrorCode.InputInvalid, "INPUT_INVALID"),
        (ErrorCode.NoAdmissibleTarget, "NO_ADMISSIBLE_TARGET"),
        (ErrorCode.ControlUnavailable, "CONTROL_UNAVAILABLE"),
        (ErrorCode.Busy, "BUSY"),
        (ErrorCode.TargetUnavailable, "TARGET_UNAVAILABLE"),
        (ErrorCode.AuthUnavailable, "AUTH_UNAVAILABLE"),
        (ErrorCode.QuotaExhausted, "QUOTA_EXHAUSTED"),
        (ErrorCode.ProviderTimeout, "PROVIDER_TIMEOUT"),
        (ErrorCode.ProtocolError, "PROTOCOL_ERROR"),
        (ErrorCode.OutputLimit, "OUTPUT_LIMIT"),
        (ErrorCode.OutputBlocked, "OUTPUT_BLOCKED"),
        (ErrorCode.ContextLimit, "CONTEXT_LIMIT"),
        (ErrorCode.ContextChanged, "CONTEXT_CHANGED"),
        (ErrorCode.ContextUnavailable, "CONTEXT_UNAVAILABLE"),
        (ErrorCode.StoreUnavailable, "STORE_UNAVAILABLE"),
        (ErrorCode.StoreInUse, "STORE_IN_USE"),
        (ErrorCode.ContentUnavailable, "CONTENT_UNAVAILABLE"),
        (ErrorCode.CryptoUnavailable, "CRYPTO_UNAVAILABLE"),
        (ErrorCode.Cancelled, "CANCELLED"),
        (ErrorCode.SideEffectUncertain, "SIDE_EFFECT_UNCERTAIN"),
        (ErrorCode.MemoryUnavailable, "MEMORY_UNAVAILABLE"),
        (ErrorCode.EmbeddingUnavailable, "EMBEDDING_UNAVAILABLE"),
        (ErrorCode.MemoryScopeDenied, "MEMORY_SCOPE_DENIED"),
        (ErrorCode.MemoryLimit, "MEMORY_LIMIT"),
        (ErrorCode.MemoryIndexFailed, "MEMORY_INDEX_FAILED"),
        (ErrorCode.EmbeddingVersionMismatch, "EMBEDDING_VERSION_MISMATCH"),
        (ErrorCode.ExtensionPermissionDenied, "EXTENSION_PERMISSION_DENIED"),
        (ErrorCode.WorkspaceBusy, "WORKSPACE_BUSY"),
        (ErrorCode.SessionBusy, "SESSION_BUSY"),
        (ErrorCode.RuntimeVersionMismatch, "RUNTIME_VERSION_MISMATCH"),
        (ErrorCode.WorkspaceDirty, "WORKSPACE_DIRTY"),
        (ErrorCode.ToolSourceMissing, "TOOL_SOURCE_MISSING"),
        (ErrorCode.ToolSourceUnavailable, "TOOL_SOURCE_UNAVAILABLE"),
        (ErrorCode.ToolNotAttested, "TOOL_NOT_ATTESTED"),
        (ErrorCode.ToolAttestationRejected, "TOOL_ATTESTATION_REJECTED"),
        (ErrorCode.ToolChanged, "TOOL_CHANGED"),
        (ErrorCode.ToolCredentialsMissing, "TOOL_CREDENTIALS_MISSING"),
        (ErrorCode.ToolCredentialsRejected, "TOOL_CREDENTIALS_REJECTED"),
        (ErrorCode.ToolArgumentsDenied, "TOOL_ARGUMENTS_DENIED"),
        (ErrorCode.ToolResultDenied, "TOOL_RESULT_DENIED"),
        (ErrorCode.NoToolCapableModel, "NO_TOOL_CAPABLE_MODEL"),
        (ErrorCode.ToolLimit, "TOOL_LIMIT"),
    ];

    private static readonly Dictionary<ErrorCode, string> ToWireMap =
        Pairs.ToDictionary(pair => pair.Code, pair => pair.Wire);

    private static readonly Dictionary<string, ErrorCode> FromWireMap =
        Pairs.ToDictionary(pair => pair.Wire, pair => pair.Code, StringComparer.Ordinal);

    /// <summary>Tutti i codici registrati, per i test di contratto.</summary>
    public static IReadOnlyList<ErrorCode> All => [.. Pairs.Select(pair => pair.Code)];

    /// <summary>Stringa wire del codice.</summary>
    public static string ToWire(this ErrorCode code) =>
        ToWireMap.TryGetValue(code, out string? wire)
            ? wire
            : throw new ArgumentOutOfRangeException(nameof(code), "Error code not registered.");

    /// <summary>Codice corrispondente alla stringa wire, confronto ordinale.</summary>
    public static bool TryParseWire(string? wire, out ErrorCode code)
    {
        if (wire is null)
        {
            code = default;
            return false;
        }

        return FromWireMap.TryGetValue(wire, out code);
    }
}
