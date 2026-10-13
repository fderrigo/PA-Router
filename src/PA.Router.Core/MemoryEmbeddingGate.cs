using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Policy;
using PA.Router.Policy.Grants;
using PA.Router.Privacy;

namespace PA.Router.Core;

/// <summary>
/// Gate delle elaborazioni ausiliarie: nulla viene embeddato senza passare di qui.
///
/// L'ordine e' quello della specifica (03-specs/POLICY.md): la privacy analizza il testo, la policy
/// verifica <c>allowLocalMemoryProcessing</c>, il fingerprint deve essere quello approvato, viene
/// emesso un grant interno con scopo <c>memory_embed</c> e registrato un evento di metadati — e
/// solo allora si chiama il modello locale.
///
/// Il grant e' separato da quello foreground e non e' riutilizzabile al suo posto: un permesso a
/// calcolare un vettore non e' un permesso a inviare un payload a un provider.
///
/// Testo <c>secret</c> o di classe non determinata non viene embeddato nella beta: un vettore e'
/// una conservazione, e conservare cio' che non si e' saputo classificare e' esattamente il modo in
/// cui un segreto sopravvive a una cancellazione.
/// </summary>
public sealed class MemoryEmbeddingGate : IEmbeddingProvider
{
    private readonly IEmbeddingProvider inner;
    private readonly CompiledPolicy policy;
    private readonly PrivacyScanner scanner;
    private readonly GrantService grants;
    private readonly IMetadataDigest digest;
    private readonly IMemoryAudit audit;
    private readonly MemoryScope scope;
    private readonly string approvedFingerprint;
    private readonly DataClass maximumClass;

    /// <param name="inner">Provider locale effettivo.</param>
    /// <param name="policy">Policy compilata del profilo corrente.</param>
    /// <param name="scanner">Analisi privacy del testo da embeddare.</param>
    /// <param name="grants">Servizio dei grant a uso singolo.</param>
    /// <param name="digest">Digest con chiave locale, per il payload del grant.</param>
    /// <param name="audit">Registro degli eventi di memoria.</param>
    /// <param name="scope">Ambito autorizzato della richiesta.</param>
    /// <param name="approvedFingerprint">Fingerprint approvato dall'operatore nella configurazione.</param>
    /// <param name="maximumClass">
    /// Classe massima che questo provider puo' vedere. Per il servizio locale e' la piu' alta
    /// conservabile; per un servizio che sta fuori dalla macchina e' <c>public</c>, e il gate
    /// rifiuta prima della chiamata tutto cio' che la supera. E' l'ultima barriera prima
    /// dell'uscita, e non si fida della destinazione gia' decisa a monte.
    /// </param>
    public MemoryEmbeddingGate(
        IEmbeddingProvider inner,
        CompiledPolicy policy,
        PrivacyScanner scanner,
        GrantService grants,
        IMetadataDigest digest,
        IMemoryAudit audit,
        MemoryScope scope,
        string approvedFingerprint,
        DataClass maximumClass = DataClass.Sensitive)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedFingerprint);

        this.inner = inner;
        this.policy = policy;
        this.scanner = scanner;
        this.grants = grants;
        this.digest = digest;
        this.audit = audit;
        this.scope = scope;
        this.approvedFingerprint = approvedFingerprint;
        this.maximumClass = maximumClass;
    }

    /// <inheritdoc />
    public int MaxBatchSize => inner.MaxBatchSize;

    /// <inheritdoc />
    public EmbeddingFingerprint Fingerprint => inner.Fingerprint;

    /// <inheritdoc />
    public async ValueTask<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (inputs.Count == 0)
        {
            return new EmbeddingBatch(Fingerprint, []);
        }

        if (!policy.AllowLocalMemoryProcessing)
        {
            await DenyAsync("local_memory_processing_forbidden", cancellationToken).ConfigureAwait(false);

            throw new PaRouterException(
                ErrorCode.MemoryScopeDenied,
                "The active profile does not allow local memory processing.")
                .WithMetadata("reason", "local_memory_processing_forbidden");
        }

        string fingerprint = inner.Fingerprint.ToString();

        if (!string.Equals(fingerprint, approvedFingerprint, StringComparison.Ordinal))
        {
            // Un modello diverso da quello approvato produrrebbe vettori non confrontabili con
            // l'indice esistente, e non e' stato osservato da nessuno.
            await DenyAsync("fingerprint_not_approved", cancellationToken).ConfigureAwait(false);

            throw new PaRouterException(
                ErrorCode.EmbeddingVersionMismatch,
                "The embedding model is not the one approved for this project.")
                .WithMetadata("reason", "fingerprint_not_approved");
        }

        foreach (string input in inputs)
        {
            PrivacyReport report = scanner.Scan(input, DataClass.Public);

            if (report.AggregateClass is DataClass.Secret)
            {
                await DenyAsync("secret_not_embeddable", cancellationToken).ConfigureAwait(false);

                throw new PaRouterException(ErrorCode.MemoryScopeDenied, "A secret is never embedded.")
                    .WithMetadata("reason", "secret_not_embeddable");
            }

            if (report.Certainty is not ScanCertainty.Complete)
            {
                // Classe non determinata: nella beta non si embedda cio' che non si e' potuto
                // classificare.
                await DenyAsync("unknown_not_embeddable", cancellationToken).ConfigureAwait(false);

                throw new PaRouterException(ErrorCode.MemoryScopeDenied, "Text of undetermined class is not embedded.")
                    .WithMetadata("reason", "unknown_not_embeddable");
            }

            if (report.AggregateClass > maximumClass)
            {
                // Il testo supera la classe che questo servizio puo' vedere. Succede quando un
                // ricordo instradato verso la memoria potenziata contiene, adesso, piu' di quanto
                // conteneva quando e' stato classificato: si ferma qui, prima della chiamata.
                // La classe osservata entra nell'evento: un rifiuto che non dice quale classe ha
                // trovato non si corregge, e questa e' esattamente la riga che serve per capire
                // perche' un ricordo non e' uscito.
                await DenyAsync("class_above_service_maximum", cancellationToken, report.AggregateClass)
                    .ConfigureAwait(false);

                throw new PaRouterException(
                    ErrorCode.MemoryScopeDenied,
                    "That class of text cannot reach this embedding service.")
                    .WithMetadata("reason", "class_above_service_maximum")
                    .WithMetadata("maxClass", maximumClass.ToWire());
            }
        }

        // Digest dell'insieme che sta per partire: il grant vale per questi testi e non per
        // altri, ed e' cio' che impedisce di riusarlo per un secondo batch.
        string payloadDigest = digest.Compute(
            System.Text.Encoding.UTF8.GetBytes(string.Concat(inputs)));

        ExecutionGrant grant = grants.IssueMemoryEmbed(
            scope.SessionId,
            fingerprint,
            payloadDigest,
            policy.ProfileId);

        // L'evento precede la chiamata: se il processo muore durante l'inferenza resta comunque la
        // traccia che quel modello e' stato invocato.
        await audit.RecordAsync(
            MemoryAuditCodes.EmbedIntent,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["purpose"] = GrantPurpose.MemoryEmbed.ToWire(),
                ["fingerprint"] = fingerprint,
                ["projectId"] = scope.ProjectId.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
                ["sessionId"] = scope.SessionId,
                ["inputs"] = inputs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["nonceDigest"] = digest.Compute(System.Text.Encoding.UTF8.GetBytes(grant.Nonce))[..16],
            },
            cancellationToken).ConfigureAwait(false);

        GrantRejection rejection = grants.Redeem(
            grant,
            fingerprint,
            GrantPurpose.MemoryEmbed,
            payloadDigest,
            manifestDigest: fingerprint,
            policyDigest: policy.ProfileId,
            registryDigest: fingerprint);

        if (rejection is not GrantRejection.None)
        {
            throw GrantService.ToException(rejection);
        }

        return await inner.EmbedAsync(inputs, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask DenyAsync(string reason, CancellationToken cancellationToken, DataClass? observed = null)
    {
        Dictionary<string, string> metadata = new(StringComparer.Ordinal)
        {
            ["reason"] = reason,
            ["projectId"] = scope.ProjectId.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
            ["sessionId"] = scope.SessionId,
        };

        if (observed is DataClass classification)
        {
            metadata["classification"] = classification.ToWire();
            metadata["maxClass"] = maximumClass.ToWire();
        }

        return audit.RecordAsync(MemoryAuditCodes.EmbedDenied, metadata, cancellationToken);
    }
}
