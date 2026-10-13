using System.Globalization;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Extensions.Tools;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Process;
using PA.Router.Infrastructure.Storage;
using PA.Router.Policy;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>Strumenti pronti per un turno: la sessione per il coordinatore e il riscontro per l'applicazione.</summary>
public sealed class PreparedTools : IAsyncDisposable
{
    private readonly SourceGateway gateway;

    internal PreparedTools(SourceGateway gateway, ToolSession session, ToolOffer offer)
    {
        this.gateway = gateway;
        Session = session;
        Offer = offer;
    }

    /// <summary>Strumenti e limiti per il turno.</summary>
    public ToolSession Session { get; }

    /// <summary>Strumenti offerti ed esclusi, con il motivo.</summary>
    public ToolOffer Offer { get; }

    /// <summary>Chiude le sorgenti: le credenziali del turno muoiono con loro.</summary>
    public ValueTask DisposeAsync() => gateway.DisposeAsync();
}

/// <summary>
/// Preparazione degli strumenti di un turno (ADR-020), in un punto solo (INV-22).
///
/// Sorgenti dalla richiesta, oppure — se l'applicazione non ne indica — dalla configurazione del router; le
/// sorgenti stdio solo dalla configurazione. Il router legge gli strumenti da solo, ne calcola l'impronta e
/// cerca per ciascuno una dichiarazione valida: quella della richiesta, poi quella dell'operatore, poi quella
/// registrata in precedenza. Uno strumento senza dichiarazione, con un'impronta cambiata o oltre il profilo non
/// viene offerto, e il riscontro lo dice. Se non resta nulla da offrire il turno non parte.
///
/// Le credenziali restano in memoria dentro le sorgenti del turno: non vengono scritte da nessuna parte.
/// </summary>
public static class ToolPreparation
{
    /// <summary>Durata massima di una dichiarazione dell'applicazione.</summary>
    public const int MaxDeclarationDays = 366;

    // Un client per il processo: nessun proxy, nessun redirect seguito, certificato verificato. Le scadenze
    // delle chiamate le decidono i limiti del turno.
    private static readonly HttpClient Client = new(PlacementRules.CreateHandler(Placement.ThirdParty));

    /// <summary>
    /// Prepara gli strumenti del turno. Restituisce null se non ci sono strumenti da usare e la richiesta non ne
    /// chiede; lancia <see cref="ToolPreparationException"/> se la richiesta ne chiede e non si possono usare.
    /// </summary>
    /// <param name="channel">Canale del front end che riceve la richiesta (ADR-032, fase 2).</param>
    public static async Task<PreparedTools?> PrepareAsync(
        RouterStack stack,
        string profileId,
        ToolsRequest? request,
        CancellationToken cancellationToken,
        Channel channel = Channel.Chatbot)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        ConfigurationSnapshot snapshot = stack.EnsureCurrent().Snapshot;
        CompiledPolicy policy = PolicyCompiler.Compile(TurnInputs.ResolveProfile(snapshot, profileId), snapshot.Config, channel);

        if (snapshot.Config.Tools is not { Enabled: true } config)
        {
            // Strumenti spenti dall'operatore: una richiesta che li chiede lo sa, una che non li chiede non cambia.
            return request is null ? null : throw Refuse(new ToolProblem(ErrorCode.ToolSourceMissing, Reason: "tools_disabled"));
        }

        List<PlannedSource> planned = Plan(config, request);

        if (planned.Count == 0)
        {
            return request is null ? null : throw Refuse(new ToolProblem(ErrorCode.ToolSourceMissing, Reason: "no_sources"));
        }

        Dictionary<string, ToolCredential> credentials = Credentials(planned, request);
        List<IToolSource> opened = [];

        try
        {
            List<Discovered> discovered = [];

            foreach (PlannedSource source in planned)
            {
                using CancellationTokenSource listing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                listing.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, config.Limits.CallTimeoutSeconds)));

                try
                {
                    IToolSource opening = Open(source, credentials.GetValueOrDefault(source.Id));
                    opened.Add(opening);

                    discovered.Add(new Discovered(
                        opening,
                        await opening.ListAsync(listing.Token).ConfigureAwait(false),
                        source.Configured?.Id));
                }
                catch (PaRouterException exception)
                {
                    throw Refuse(new ToolProblem(exception.Code, source.Id, Reason: exception.Metadata.GetValueOrDefault("reason")));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw Refuse(new ToolProblem(ErrorCode.ToolSourceUnavailable, source.Id, Reason: "timeout"));
                }
            }

            Resolution resolution = Resolve(stack, snapshot, config, policy, request, discovered);

            if (resolution.Offered.Count == 0)
            {
                throw new ToolPreparationException(
                    resolution.Problems.Count > 0
                        ? resolution.Problems
                        : [new ToolProblem(ErrorCode.ToolSourceUnavailable, Reason: "no_tools")]);
            }

            SqliteToolAttestationStore store = stack.ToolAttestations();

            foreach (StoredToolAttestation attestation in resolution.Accepted)
            {
                // Una dichiarazione valida resta registrata come attestazione dell'applicazione: la richiesta
                // successiva puo' ometterla finche' non scade o l'impronta non cambia.
                await store.RecordAsync(snapshot.Config.Api.ProjectId, attestation, cancellationToken).ConfigureAwait(false);
            }

            SourceGateway gateway = new(opened, resolution.Offered, [.. credentials.Values.Select(credential => credential.Value)]);

            return new PreparedTools(
                gateway,
                new ToolSession(gateway, TurnRunner.ToolLimitsFor(snapshot, policy)),
                new ToolOffer([.. resolution.Offered.Select(tool => tool.ModelName)], resolution.Problems));
        }
        catch
        {
            foreach (IToolSource source in opened)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static List<PlannedSource> Plan(ToolsConfig config, ToolsRequest? request)
    {
        List<PlannedSource> planned = [];

        if (request is { Sources.Count: > 0 })
        {
            foreach (ToolSourceRequest source in request.Sources)
            {
                if (source.Kind is ToolSourceKind.McpStdio)
                {
                    // Un'applicazione esterna non fa avviare programmi su questa macchina.
                    throw Refuse(new ToolProblem(ErrorCode.ToolSourceUnavailable, source.Id, Field: "kind", Reason: "stdio_only_from_configuration"));
                }

                // Una sorgente che l'operatore ha configurato allo stesso indirizzo resta sua: ne valgono la collocazione
                // osservata e le attestazioni. Un indirizzo che l'operatore non conosce e' un terzo, anche su loopback.
                ToolSourceConfig? configured = config.Sources.FirstOrDefault(entry =>
                    entry.Kind == source.Kind
                    && entry.Url is not null
                    && string.Equals(entry.Url.AbsoluteUri, source.Url.AbsoluteUri, StringComparison.Ordinal));

                planned.Add(new PlannedSource(source.Id, source.Kind, source.Url, configured));
            }
        }
        else
        {
            planned.AddRange(config.Sources.Select(source => new PlannedSource(source.Id, source.Kind, source.Url, source)));
        }

        if (planned.GroupBy(source => source.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } duplicate)
        {
            throw Refuse(new ToolProblem(ErrorCode.ToolSourceUnavailable, duplicate.Key, Field: "id", Reason: "duplicate_source"));
        }

        return planned;
    }

    private static Dictionary<string, ToolCredential> Credentials(List<PlannedSource> planned, ToolsRequest? request)
    {
        Dictionary<string, ToolCredential> credentials = new(StringComparer.Ordinal);

        foreach (ToolCredential credential in request?.Credentials ?? [])
        {
            if (!planned.Any(source => string.Equals(source.Id, credential.SourceId, StringComparison.Ordinal)))
            {
                throw Refuse(new ToolProblem(ErrorCode.ToolSourceMissing, credential.SourceId, Field: "credentials", Reason: "credential_for_unknown_source"));
            }

            if (ToolSourceRules.HeaderViolation(credential.Header) is string reason)
            {
                throw Refuse(new ToolProblem(ErrorCode.ToolCredentialsRejected, credential.SourceId, Field: "header", Reason: reason));
            }

            if (!credentials.TryAdd(credential.SourceId, credential))
            {
                throw Refuse(new ToolProblem(ErrorCode.ToolCredentialsRejected, credential.SourceId, Field: "credentials", Reason: "duplicate_credential"));
            }
        }

        return credentials;
    }

    private static IToolSource Open(PlannedSource source, ToolCredential? credential) => source.Kind switch
    {
        ToolSourceKind.McpStdio => McpStdioSource.Start(
            source.Id,
            source.Configured?.Command ?? string.Empty,
            source.Configured?.Arguments ?? [],
            PlatformProcessGuardFactory.Instance),
        ToolSourceKind.OpenApi => new OpenApiSource(source.Id, source.Url ?? throw Missing(source), credential, Client),
        _ => new McpHttpSource(source.Id, source.Url ?? throw Missing(source), credential, Client),
    };

    /// <summary>Apre una sorgente della configurazione senza credenziali, per la diagnosi.</summary>
    internal static IToolSource OpenConfigured(ToolSourceConfig configured) =>
        Open(new PlannedSource(configured.Id, configured.Kind, configured.Url, configured), credential: null);

    private static PaRouterException Missing(PlannedSource source) =>
        new PaRouterException(ErrorCode.ToolSourceUnavailable, "The tool source declares no url.")
            .WithMetadata("reason", "url_missing")
            .WithMetadata("toolSource", source.Id);

    private static Resolution Resolve(
        RouterStack stack,
        ConfigurationSnapshot snapshot,
        ToolsConfig config,
        CompiledPolicy policy,
        ToolsRequest? request,
        List<Discovered> discovered)
    {
        DateTimeOffset now = SystemClock.Instance.UtcNow;

        Dictionary<(string, string), StoredToolAttestation> stored = stack.ToolAttestations()
            .List(snapshot.Config.Api.ProjectId)
            .ToDictionary(attestation => (attestation.SourceKey, attestation.Tool));

        IReadOnlyList<ToolDeclaration> declarations = request?.Declarations ?? [];

        // Con piu' sorgenti il nome visto dal modello porta la sorgente: due server possono avere uno strumento
        // con lo stesso nome, e il modello deve poterli distinguere.
        bool prefix = discovered.Count > 1;
        HashSet<string> names = new(StringComparer.Ordinal);
        Resolution resolution = new([], [], []);

        foreach (Discovered entry in discovered)
        {
            if (entry.Tools.Count == 0)
            {
                resolution.Problems.Add(new ToolProblem(ErrorCode.ToolSourceUnavailable, entry.Source.Id, Reason: "no_tools"));
                continue;
            }

            foreach (DiscoveredTool tool in entry.Tools)
            {
                ToolResolution decided = ToolAttestations.Resolve(
                    entry.Source, entry.ConfiguredId, tool, declarations, config, stored, policy, now);

                if (decided.Problem is ToolProblem problem)
                {
                    resolution.Problems.Add(problem);
                    continue;
                }

                if (decided.Record is StoredToolAttestation record)
                {
                    resolution.Accepted.Add(record);
                }

                resolution.Offered.Add(new OfferedTool(
                    UniqueName(prefix ? entry.Source.Id + "__" + tool.Name : tool.Name, names),
                    entry.Source,
                    tool,
                    decided.Destination!));
            }
        }

        foreach (ToolDeclaration declaration in declarations)
        {
            if (!discovered.Any(entry =>
                    string.Equals(entry.Source.Id, declaration.SourceId, StringComparison.Ordinal)
                    && entry.Tools.Any(tool => string.Equals(tool.Name, declaration.Tool, StringComparison.Ordinal))))
            {
                resolution.Problems.Add(new ToolProblem(
                    ErrorCode.ToolAttestationRejected, declaration.SourceId, declaration.Tool, Field: "tool", Reason: "tool_not_found"));
            }
        }

        return resolution;
    }

    private static string UniqueName(string candidate, HashSet<string> names)
    {
        string name = ToolNames.Sanitize(candidate);

        for (int suffix = 2; !names.Add(name); suffix++)
        {
            name = ToolNames.Sanitize(
                candidate[..Math.Min(candidate.Length, ToolNames.MaxLength - 4)] + "_" + suffix.ToString(CultureInfo.InvariantCulture));
        }

        return name;
    }

    private static ToolPreparationException Refuse(ToolProblem problem) => new([problem]);

    private sealed record PlannedSource(string Id, ToolSourceKind Kind, Uri? Url, ToolSourceConfig? Configured);

    private sealed record Discovered(IToolSource Source, IReadOnlyList<DiscoveredTool> Tools, string? ConfiguredId);

    private sealed record Resolution(List<OfferedTool> Offered, List<ToolProblem> Problems, List<StoredToolAttestation> Accepted);
}

/// <summary>Decisione su uno strumento: la destinazione attestata, oppure il problema.</summary>
/// <param name="Destination">Destinazione con cio' che e' stato attestato.</param>
/// <param name="Problem">Riscontro, se lo strumento non si puo' offrire.</param>
/// <param name="Record">Dichiarazione dell'applicazione da registrare.</param>
public sealed record ToolResolution(ToolDestination? Destination, ToolProblem? Problem, StoredToolAttestation? Record);

/// <summary>
/// Regola delle attestazioni degli strumenti (ADR-020). Ordine: revoca dell'operatore, dichiarazione della
/// richiesta, attestazione dell'operatore, attestazione registrata. Ogni attestazione vale solo per l'impronta
/// a cui si riferisce e fino alla sua scadenza, e non concede nulla oltre il profilo.
/// </summary>
public static class ToolAttestations
{
    /// <summary>Decide se lo strumento si puo' offrire e con quale destinazione.</summary>
    /// <param name="source">Sorgente dello strumento.</param>
    /// <param name="configuredId">Identificatore della sorgente nella configurazione, se l'operatore l'ha configurata.</param>
    /// <param name="tool">Strumento osservato.</param>
    /// <param name="declarations">Dichiarazioni della richiesta.</param>
    /// <param name="config">Configurazione degli strumenti.</param>
    /// <param name="stored">Dichiarazioni registrate delle applicazioni.</param>
    /// <param name="policy">Profilo della richiesta.</param>
    /// <param name="now">Istante della decisione.</param>
    public static ToolResolution Resolve(
        IToolSource source,
        string? configuredId,
        DiscoveredTool tool,
        IReadOnlyList<ToolDeclaration> declarations,
        ToolsConfig config,
        IReadOnlyDictionary<(string, string), StoredToolAttestation> stored,
        CompiledPolicy policy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(policy);

        // L'operatore nomina una sorgente per indirizzo, oppure per identificatore se e' una sua sorgente: un
        // identificatore scelto da un'applicazione non deve poter coincidere per caso con quello dell'operatore.
        bool NamedByOperator(string reference) =>
            string.Equals(reference, source.Key, StringComparison.Ordinal)
            || (configuredId is not null && string.Equals(reference, configuredId, StringComparison.Ordinal));

        ToolResolution Refused(ErrorCode code, string field, string reason) =>
            new(null, new ToolProblem(code, source.Id, tool.Name, field, reason, tool.Fingerprint), null);

        bool SameTool(string name) => string.Equals(name, tool.Name, StringComparison.Ordinal);

        ToolResolution? Invalid(ToolDeclaration entry)
        {
            if (entry.Fingerprint is null)
            {
                return Refused(ErrorCode.ToolAttestationRejected, "fingerprint", "missing");
            }

            if (!string.Equals(entry.Fingerprint, tool.Fingerprint, StringComparison.Ordinal))
            {
                // La dichiarazione parla di uno strumento diverso da quello che la sorgente espone adesso.
                return Refused(ErrorCode.ToolChanged, "fingerprint", "fingerprint_differs");
            }

            return entry switch
            {
                { Accepts: null } => Refused(ErrorCode.ToolAttestationRejected, "accepts", "missing"),
                { Returns: null } => Refused(ErrorCode.ToolAttestationRejected, "returns", "missing"),
                { ExpiresAt: null } => Refused(ErrorCode.ToolAttestationRejected, "expiresAt", "missing"),
                { Accepts: DataClass.Secret or DataClass.Unknown } => Refused(ErrorCode.ToolAttestationRejected, "accepts", "class_not_declarable"),
                { Returns: DataClass.Secret or DataClass.Unknown } => Refused(ErrorCode.ToolAttestationRejected, "returns", "class_not_declarable"),
                _ when entry.ExpiresAt <= now => Refused(ErrorCode.ToolAttestationRejected, "expiresAt", "expired"),
                _ when entry.ExpiresAt > now.AddDays(ToolPreparation.MaxDeclarationDays) => Refused(ErrorCode.ToolAttestationRejected, "expiresAt", "too_far"),
                _ => null,
            };
        }

        if (config.Revocations.Any(revocation => NamedByOperator(revocation.Source) && SameTool(revocation.Tool)))
        {
            return Refused(ErrorCode.ToolAttestationRejected, "revoked", "revoked_by_operator");
        }

        // Collocazione osservata solo per una sorgente dell'operatore. Un indirizzo indicato da un'applicazione, anche
        // di loopback, puo' essere un inoltro verso chiunque: vale come un terzo finche' l'operatore non lo configura.
        Placement observed = configuredId is not null ? source.Placement : Placement.ThirdParty;
        bool program = source.Key.StartsWith("stdio:", StringComparison.Ordinal);

        ToolAttestationConfig? attested = config.Attestations.FirstOrDefault(entry => NamedByOperator(entry.Source) && SameTool(entry.Tool));
        ToolDeclaration? declaration = declarations.FirstOrDefault(entry =>
            string.Equals(entry.SourceId, source.Id, StringComparison.Ordinal) && SameTool(entry.Tool));

        ToolDestination destination;
        StoredToolAttestation? record = null;

        if (attested is not null)
        {
            if (!string.Equals(attested.Fingerprint, tool.Fingerprint, StringComparison.Ordinal))
            {
                return Refused(ErrorCode.ToolChanged, "fingerprint", "fingerprint_differs");
            }

            if (attested.ExpiresAt <= now)
            {
                return Refused(ErrorCode.ToolAttestationRejected, "expiresAt", "expired");
            }

            DataClass accepts = attested.Accepts;
            DataClass returns = attested.Returns;
            IReadOnlySet<ProcessingRegion> regions = attested.Regions;
            DateTimeOffset expires = attested.ExpiresAt;

            if (declaration is not null)
            {
                // Decide l'operatore: una dichiarazione dell'applicazione puo' solo restringere cio' che ha attestato,
                // mai allargarlo, e non viene registrata.
                if (Invalid(declaration) is ToolResolution refused)
                {
                    return refused;
                }

                accepts = (DataClass)Math.Min((int)accepts, (int)declaration.Accepts!.Value);
                returns = DataClasses.Max(returns, declaration.Returns!.Value);
                expires = declaration.ExpiresAt!.Value < expires ? declaration.ExpiresAt.Value : expires;

                if (regions.Count > 0 && declaration.Regions.Count > 0)
                {
                    HashSet<ProcessingRegion> narrowed = [.. Regions.Expand(regions).Intersect(Regions.Expand(declaration.Regions))];

                    if (narrowed.Count == 0)
                    {
                        return Refused(ErrorCode.ToolAttestationRejected, "regions", "narrowing_empty");
                    }

                    regions = narrowed;
                }
            }

            // Solo l'operatore puo' dichiarare una collocazione diversa da quella osservata.
            destination = new ToolDestination(source.Id, tool.Name, attested.Placement ?? observed, accepts, returns, regions, expires);
        }
        else if (program)
        {
            // Un programma avviato su questa macchina lo attesta l'operatore: un'applicazione non puo' dichiarare che
            // cosa fa un processo che non controlla.
            return Refused(ErrorCode.ToolNotAttested, "operator", "operator_attestation_required");
        }
        else if (declaration is not null)
        {
            if (Invalid(declaration) is ToolResolution refused)
            {
                return refused;
            }

            destination = new ToolDestination(
                source.Id, tool.Name, observed, declaration.Accepts!.Value, declaration.Returns!.Value, declaration.Regions, declaration.ExpiresAt!.Value);

            record = new StoredToolAttestation(
                source.Key, tool.Name, tool.Fingerprint, declaration.Accepts.Value, declaration.Returns.Value, declaration.Regions, now, declaration.ExpiresAt.Value);
        }
        else if (stored.TryGetValue((source.Key, tool.Name), out StoredToolAttestation? registered))
        {
            if (!string.Equals(registered.Fingerprint, tool.Fingerprint, StringComparison.Ordinal))
            {
                return Refused(ErrorCode.ToolChanged, "fingerprint", "fingerprint_differs");
            }

            if (registered.ExpiresAt <= now)
            {
                return Refused(ErrorCode.ToolAttestationRejected, "expiresAt", "expired");
            }

            destination = new ToolDestination(
                source.Id, tool.Name, observed, registered.Accepts, registered.Returns, registered.Regions, registered.ExpiresAt);
        }
        else
        {
            return Refused(ErrorCode.ToolNotAttested, "accepts,returns,regions,expiresAt", "declaration_missing");
        }

        // Oltre il profilo: uno strumento che non puo' mai ricevere argomenti, o che sta dove il profilo non manda
        // dati, non si offre — offrirlo porterebbe il modello a una chiamata che verrebbe comunque negata.
        if ((int)destination.MaxArgumentClass < (int)policy.MinimumDataClass)
        {
            return Refused(ErrorCode.ToolAttestationRejected, "accepts", "below_profile_floor");
        }

        TrustZone sourceZone = TrustZones.OfDestination(destination.Placement, destination.Regions, destination.EvidenceExpiresAt, now);

        if (sourceZone > TrustZone.Lan
            && !Enumerable.Range((int)policy.MinimumDataClass, (int)destination.MaxArgumentClass - (int)policy.MinimumDataClass + 1)
                .Any(value => sourceZone <= policy.Zones.For((DataClass)value)))
        {
            // Nessuna classe che lo strumento puo' ricevere raggiunge la sua zona (ADR-029).
            return Refused(ErrorCode.ToolAttestationRejected, "source", "external_forbidden");
        }

        return new ToolResolution(destination, null, record);
    }
}

/// <summary>Stato di uno strumento nella diagnosi: usabile, oppure il problema che lo esclude.</summary>
public sealed record ToolDiagnosisEntry(string Name, string Fingerprint, ToolProblem? Problem);

/// <summary>Stato di una sorgente configurata nella diagnosi.</summary>
public sealed record SourceDiagnosis(string Id, ToolSourceKind Kind, ToolProblem? Problem, IReadOnlyList<ToolDiagnosisEntry> Tools);

/// <summary>
/// Diagnosi degli strumenti (ADR-020): sorgenti della configurazione lette adesso e classi di dati per cui nessun
/// modello ammesso dal profilo di servizio sa usare strumenti.
/// </summary>
public sealed record ToolDiagnosis(
    bool Enabled,
    ToolLimits Limits,
    IReadOnlyList<SourceDiagnosis> Sources,
    IReadOnlyList<DataClass> ClassesWithoutToolCapableModel);

/// <summary>Diagnosi degli strumenti per `pa doctor`, con le stesse regole della preparazione (INV-22).</summary>
public static class ToolDiagnostics
{
    /// <summary>
    /// Legge le sorgenti della configurazione senza credenziali — quelle le consegna l'applicazione — e dice per ogni
    /// strumento se sarebbe offerto; poi, per ogni classe di dati, se esiste un modello ammesso che sa usare strumenti.
    /// </summary>
    public static async Task<ToolDiagnosis?> DescribeAsync(
        ConfigurationSnapshot snapshot,
        IReadOnlyDictionary<string, ProbeResult> probes,
        IReadOnlyList<StoredToolAttestation> registered,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(registered);

        if (snapshot.Config.Tools is not ToolsConfig config)
        {
            return null;
        }

        CompiledPolicy policy = PolicyCompiler.Compile(TurnInputs.ResolveProfile(snapshot, snapshot.Config.Api.ServiceProfileId), snapshot.Config, Channel.Chatbot);
        Dictionary<(string, string), StoredToolAttestation> stored = registered.ToDictionary(attestation => (attestation.SourceKey, attestation.Tool));
        DateTimeOffset now = SystemClock.Instance.UtcNow;
        List<SourceDiagnosis> sources = [];

        foreach (ToolSourceConfig configured in config.Sources)
        {
            sources.Add(await DescribeSourceAsync(configured, config, stored, policy, now, cancellationToken).ConfigureAwait(false));
        }

        return new ToolDiagnosis(config.Enabled, config.Limits, sources, ClassesWithoutToolCapableModel(snapshot, probes, policy));
    }

    /// <summary>
    /// Classi per cui nessun servizio ammesso dal profilo sa usare strumenti: una richiesta con strumenti su dati di
    /// quella classe verrebbe negata con NO_TOOL_CAPABLE_MODEL (ADR-020, «Conseguenze»).
    /// </summary>
    public static IReadOnlyList<DataClass> ClassesWithoutToolCapableModel(
        ConfigurationSnapshot snapshot,
        IReadOnlyDictionary<string, ProbeResult> probes,
        CompiledPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(policy);

        IReadOnlyList<PolicyCandidate> candidates = StackView.CandidatesFor(snapshot, probes);
        PolicyEvaluator evaluator = new(SystemClock.Instance);
        List<DataClass> uncovered = [];

        foreach (DataClass dataClass in new[] { DataClass.Public, DataClass.Internal, DataClass.Personal, DataClass.Sensitive })
        {
            PolicyExplain explain = evaluator.Evaluate(
                policy,
                new PolicyRequest(
                    policy.Channel,
                    Intent.Chat,
                    ServiceCapability.Chat,
                    new HashSet<Capability> { Capability.Chat },
                    DataClasses.Max(dataClass, policy.MinimumDataClass),
                    ScanCertainty.Complete,
                    "diagnosis",
                    "diagnosis",
                    0,
                    0,
                    policy.RequiredObligations,
                    snapshot.Digest),
                candidates);

            HashSet<string> admissible = new(explain.Admissible.Select(target => target.TargetId), StringComparer.Ordinal);

            if (!candidates.Any(candidate => candidate.SupportsTools && admissible.Contains(candidate.TargetId)))
            {
                uncovered.Add(dataClass);
            }
        }

        return uncovered;
    }

    private static async Task<SourceDiagnosis> DescribeSourceAsync(
        ToolSourceConfig configured,
        ToolsConfig config,
        IReadOnlyDictionary<(string, string), StoredToolAttestation> stored,
        CompiledPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource listing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listing.CancelAfter(TimeSpan.FromSeconds(5));

        IToolSource? source = null;

        try
        {
            source = ToolPreparation.OpenConfigured(configured);

            IReadOnlyList<DiscoveredTool> tools = await source.ListAsync(listing.Token).ConfigureAwait(false);

            return new SourceDiagnosis(
                configured.Id,
                configured.Kind,
                null,
                [.. tools.Select(tool => new ToolDiagnosisEntry(
                    tool.Name,
                    tool.Fingerprint,
                    ToolAttestations.Resolve(source, configured.Id, tool, [], config, stored, policy, now).Problem))]);
        }
        catch (PaRouterException exception)
        {
            return new SourceDiagnosis(
                configured.Id, configured.Kind, new ToolProblem(exception.Code, configured.Id, Reason: exception.Metadata.GetValueOrDefault("reason")), []);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SourceDiagnosis(
                configured.Id, configured.Kind, new ToolProblem(ErrorCode.ToolSourceUnavailable, configured.Id, Reason: "timeout"), []);
        }
        finally
        {
            if (source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
