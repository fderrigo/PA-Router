using System.Security.Cryptography;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Policy.Grants;

/// <summary>
/// Emissione e verifica dei grant.
///
/// Un grant si emette solo da un <see cref="AdmissibleTarget"/>: non esiste una via per
/// autorizzare un target che la policy non ha ammesso, perche' quel tipo non e' costruibile fuori
/// da questo modulo.
///
/// La verifica confronta ogni digest e consuma il nonce. Un retry non eredita il grant
/// precedente: serve una nuova valutazione e un nuovo nonce (INV-04).
/// </summary>
public sealed class GrantService
{
    /// <summary>Durata di un grant nella beta.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly IClock clock;
    private readonly HashSet<string> consumed = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    public GrantService(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
    }

    /// <summary>Emette un grant per un target gia' ammesso dalla policy.</summary>
    /// <param name="target">Target ammissibile, prodotto dalla valutazione.</param>
    /// <param name="executionId">Esecuzione corrente.</param>
    /// <param name="attemptId">Tentativo corrente; ogni tentativo ha il proprio grant.</param>
    /// <param name="purpose">Scopo del grant.</param>
    /// <param name="manifestDigest">Digest del manifest del contesto.</param>
    /// <param name="policyDigest">Digest della policy applicata.</param>
    /// <param name="registryDigest">Digest del registro applicato.</param>
    public ExecutionGrant Issue(
        AdmissibleTarget target,
        string executionId,
        string attemptId,
        GrantPurpose purpose,
        string manifestDigest,
        string policyDigest,
        string registryDigest)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);

        DateTimeOffset now = clock.UtcNow;

        return new ExecutionGrant(
            executionId,
            attemptId,
            target.TargetId,
            purpose,
            target.PayloadDigest,
            manifestDigest,
            policyDigest,
            registryDigest,
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            now,
            now + Lifetime);
    }

    /// <summary>
    /// Emette il grant interno di una elaborazione ausiliaria di memoria.
    ///
    /// Non passa da un <see cref="AdmissibleTarget"/> perche' l'autorizzazione ausiliaria e'
    /// separata da quella foreground: l'allowlist dei target di primo piano non concede questo
    /// permesso, e questo permesso non concede un dispatch. Lo scopo <c>memory_embed</c> e' cio'
    /// che impedisce il riuso nell'uno o nell'altro verso (03-specs/POLICY.md).
    /// </summary>
    /// <param name="sessionId">Sessione che chiede l'elaborazione.</param>
    /// <param name="fingerprint">Fingerprint del modello locale, usato come identita' del target.</param>
    /// <param name="payloadDigest">Digest del testo da elaborare.</param>
    /// <param name="policyDigest">Digest della policy applicata.</param>
    public ExecutionGrant IssueMemoryEmbed(
        string sessionId,
        string fingerprint,
        string payloadDigest,
        string policyDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDigest);

        DateTimeOffset now = clock.UtcNow;

        return new ExecutionGrant(
            sessionId,
            fingerprint,
            fingerprint,
            GrantPurpose.MemoryEmbed,
            payloadDigest,
            fingerprint,
            policyDigest,
            fingerprint,
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            now,
            now + Lifetime);
    }

    /// <summary>
    /// Verifica e consuma il grant. Il nonce viene registrato prima di restituire successo: una
    /// seconda presentazione dello stesso grant fallisce anche se arriva subito dopo.
    /// </summary>
    /// <param name="grant">Grant presentato.</param>
    /// <param name="targetId">Target che si sta per invocare.</param>
    /// <param name="purpose">Scopo dell'invocazione.</param>
    /// <param name="payloadDigest">Digest del payload che sta per partire.</param>
    /// <param name="manifestDigest">Digest del manifest corrente.</param>
    /// <param name="policyDigest">Digest della policy corrente.</param>
    /// <param name="registryDigest">Digest del registro corrente.</param>
    public GrantRejection Redeem(
        ExecutionGrant grant,
        string targetId,
        GrantPurpose purpose,
        string payloadDigest,
        string manifestDigest,
        string policyDigest,
        string registryDigest)
    {
        ArgumentNullException.ThrowIfNull(grant);

        if (!string.Equals(grant.TargetId, targetId, StringComparison.Ordinal))
        {
            return GrantRejection.TargetMismatch;
        }

        if (grant.Purpose != purpose)
        {
            // Un grant di memoria non autorizza un dispatch foreground, e viceversa.
            return GrantRejection.PurposeMismatch;
        }

        if (!FixedTimeEquals(grant.PayloadDigest, payloadDigest))
        {
            return GrantRejection.PayloadMismatch;
        }

        if (!FixedTimeEquals(grant.ManifestDigest, manifestDigest))
        {
            return GrantRejection.ManifestMismatch;
        }

        if (!FixedTimeEquals(grant.PolicyDigest, policyDigest) || !FixedTimeEquals(grant.RegistryDigest, registryDigest))
        {
            // La configurazione e' cambiata fra la decisione e il dispatch: serve rivalutare.
            return GrantRejection.ConfigurationChanged;
        }

        if (!grant.IsFreshAt(clock.UtcNow))
        {
            return GrantRejection.Expired;
        }

        lock (gate)
        {
            if (!consumed.Add(grant.Nonce))
            {
                return GrantRejection.AlreadyUsed;
            }
        }

        return GrantRejection.None;
    }

    /// <summary>Traduce un rifiuto in errore tipizzato per il confine dell'host.</summary>
    public static PaRouterException ToException(GrantRejection rejection) =>
        new PaRouterException(
            ErrorCode.ControlUnavailable,
            "The execution grant is not valid for this dispatch.")
            .WithMetadata("rejection", rejection.ToWire());

    /// <summary>
    /// Confronto a tempo costante fra digest. Non protegge un segreto, ma evita di introdurre un
    /// canale laterale in un controllo di autorizzazione.
    /// </summary>
    private static bool FixedTimeEquals(string first, string second)
    {
        byte[] left = System.Text.Encoding.UTF8.GetBytes(first);
        byte[] right = System.Text.Encoding.UTF8.GetBytes(second);

        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
