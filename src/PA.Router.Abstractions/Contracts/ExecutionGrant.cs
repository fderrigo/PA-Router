namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Autorizzazione effimera a un singolo dispatch.
///
/// Il grant lega insieme esecuzione, tentativo, target, digest del payload, del manifest, della
/// policy e del registro. Se uno solo di questi cambia, il grant non vale piu': e' cio' che
/// impedisce di riusare un'autorizzazione ottenuta per un payload diverso o sotto una
/// configurazione diversa (INV-05).
///
/// Il nonce e' monouso. La verifica in memoria che sta qui e' la prima barriera; quella durevole
/// e' il vincolo UNIQUE su <c>attempts.grant_nonce_hmac</c>, che regge anche a un riavvio.
/// </summary>
/// <param name="ExecutionId">Esecuzione a cui appartiene.</param>
/// <param name="AttemptId">Tentativo autorizzato.</param>
/// <param name="TargetId">Target autorizzato.</param>
/// <param name="Purpose">Scopo del grant.</param>
/// <param name="PayloadDigest">Digest del payload autorizzato.</param>
/// <param name="ManifestDigest">Digest del manifest del contesto.</param>
/// <param name="PolicyDigest">Digest della policy applicata.</param>
/// <param name="RegistryDigest">Digest del registro applicato.</param>
/// <param name="Nonce">Nonce monouso.</param>
/// <param name="IssuedAt">Istante di emissione.</param>
/// <param name="ExpiresAt">Scadenza; la beta usa 30 secondi.</param>
public sealed record ExecutionGrant(
    string ExecutionId,
    string AttemptId,
    string TargetId,
    GrantPurpose Purpose,
    string PayloadDigest,
    string ManifestDigest,
    string PolicyDigest,
    string RegistryDigest,
    string Nonce,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Vero se il grant e' ancora entro la sua finestra.</summary>
    public bool IsFreshAt(DateTimeOffset instant) => instant < ExpiresAt;

    /// <summary>Resa compatta: nessun digest completo e nessun nonce nel log.</summary>
    public override string ToString() =>
        "ExecutionGrant { Execution = " + ExecutionId + ", Attempt = " + AttemptId
        + ", Target = " + TargetId + ", Purpose = " + Purpose.ToWire() + " }";
}

/// <summary>Motivo per cui un grant non e' utilizzabile.</summary>
public enum GrantRejection
{
    [WireValue("none")]
    None,

    [WireValue("expired")]
    Expired,

    [WireValue("already_used")]
    AlreadyUsed,

    [WireValue("payload_mismatch")]
    PayloadMismatch,

    [WireValue("manifest_mismatch")]
    ManifestMismatch,

    [WireValue("configuration_changed")]
    ConfigurationChanged,

    [WireValue("purpose_mismatch")]
    PurposeMismatch,

    [WireValue("target_mismatch")]
    TargetMismatch,
}
