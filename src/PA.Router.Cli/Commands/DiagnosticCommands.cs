using System.CommandLine;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Output;
using PA.Router.Cli.Repl;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Crypto;
using PA.Router.Infrastructure.Storage;
using PA.Router.Registry.Loading;
using PA.Router.Registry.Probing;

namespace PA.Router.Cli.Commands;

/// <summary>
/// Comandi diagnostici: <c>doctor</c>, <c>registry list</c>, <c>policy validate</c> e
/// <c>audit show</c>.
///
/// Nessuno di questi comandi esegue un turno, emette un grant o contatta un provider. Quota
/// sconosciuta viene mostrata come sconosciuta e il costo non noto come non disponibile: non si
/// inventa un numero per riempire una colonna (03-specs/CLI-TUI.md).
/// </summary>
internal static class DiagnosticCommands
{
    internal static Command CreateDoctor(Option<DirectoryInfo?> dataOption)
    {
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("doctor", Ui.Text("Check the local setup: versions, protector, schema, targets."))
        {
            dataOption,
            jsonOption,
        };

        command.SetAction((parseResult, cancellationToken) => DoctorAsync(parseResult, dataOption, jsonOption, cancellationToken));

        return command;
    }

    internal static Command CreateRegistry(Option<DirectoryInfo?> dataOption)
    {
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command list = new("list", Ui.Text("List the declared targets and what is known about them."))
        {
            dataOption,
            jsonOption,
        };

        list.SetAction(parseResult => RegistryList(parseResult, dataOption, jsonOption));

        Command registry = new("registry", Ui.Text("Inspect the target registry."))
        {
            list,
            CalibrateCommand.Create(dataOption),
            RegistryAttestCommand.Create(dataOption),
        };

        registry.SetAction(parseResult =>
        {
            parseResult.InvocationConfiguration.Output.WriteLine(Ui.Text("Run 'pa registry --help' for the available subcommands."));
            return ExitCodes.UsageError;
        });

        return registry;
    }

    internal static Command CreatePolicy(Option<DirectoryInfo?> dataOption)
    {
        Argument<FileInfo> pathArgument = new("path") { Description = Ui.Text("Policy file to validate.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command validate = new("validate", Ui.Text("Validate a policy file against schema and semantics."))
        {
            pathArgument,
            dataOption,
            jsonOption,
        };

        validate.SetAction(parseResult => PolicyValidate(parseResult, pathArgument, jsonOption));

        Command policy = new("policy", Ui.Text("Work with policy profiles.")) { validate };

        policy.SetAction(parseResult =>
        {
            parseResult.InvocationConfiguration.Output.WriteLine(Ui.Text("Run 'pa policy --help' for the available subcommands."));
            return ExitCodes.UsageError;
        });

        return policy;
    }

    internal static Command CreateAudit(Option<DirectoryInfo?> dataOption)
    {
        Argument<string?> executionArgument = new("execution-id") { Description = Ui.Text("audit.executionArgument"), Arity = ArgumentArity.ZeroOrOne };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command show = new("show", Ui.Text("Show the audit metadata of one execution."))
        {
            executionArgument,
            dataOption,
            jsonOption,
        };

        show.SetAction(parseResult => AuditShow(parseResult, dataOption, executionArgument, jsonOption));

        Command audit = new("audit", Ui.Text("Inspect the local audit trail.")) { show };

        audit.SetAction(parseResult =>
        {
            parseResult.InvocationConfiguration.Output.WriteLine(Ui.Text("Run 'pa audit --help' for the available subcommands."));
            return ExitCodes.UsageError;
        });

        return audit;
    }

    private static JsonObject? ToolsJson(ToolDiagnosis? tools) =>
        tools is null
            ? null
            : new JsonObject
            {
                ["enabled"] = tools.Enabled,
                ["limits"] = new JsonObject
                {
                    ["maxRounds"] = tools.Limits.MaxRounds,
                    ["maxCallsPerRound"] = tools.Limits.MaxCallsPerRound,
                    ["maxCalls"] = tools.Limits.MaxCalls,
                    ["callTimeoutSeconds"] = tools.Limits.CallTimeoutSeconds,
                    ["maxResultBytes"] = tools.Limits.MaxResultBytes,
                },
                ["sources"] = new JsonArray([.. tools.Sources.Select(source => (JsonNode?)new JsonObject
                {
                    ["id"] = source.Id,
                    ["kind"] = source.Kind.ToWire(),
                    ["errorCode"] = source.Problem?.Code.ToWire(),
                    ["reason"] = source.Problem?.Reason,
                    ["tools"] = new JsonArray([.. source.Tools.Select(tool => (JsonNode?)new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["fingerprint"] = tool.Fingerprint,
                        ["usable"] = tool.Problem is null,
                        ["errorCode"] = tool.Problem?.Code.ToWire(),
                        ["reason"] = tool.Problem?.Reason,
                    })]),
                })]),
                ["classesWithoutToolCapableModel"] = new JsonArray(
                    [.. tools.ClassesWithoutToolCapableModel.Select(dataClass => (JsonNode?)dataClass.ToWire())]),
            };

    private static async Task<int> DoctorAsync(
        ParseResult parseResult,
        Option<DirectoryInfo?> dataOption,
        Option<bool> jsonOption,
        CancellationToken cancellationToken)
    {
        bool json = parseResult.GetValue(jsonOption);
        TextWriter output = parseResult.InvocationConfiguration.Output;

        // Le osservazioni sono un'operazione condivisa (S63, `LocalDiagnosis`): qui resta la sola
        // resa. Prima l'ordine delle osservazioni viveva dentro questo comando, e il terminale
        // interattivo non poteva mostrarle senza ricostruirne una copia — che decide diversamente
        // senza fallire (INV-22).
        DiagnosisReport report = await LocalDiagnosis
            .RunAsync(CommandContext.ResolveDataDirectory(parseResult, dataOption), cancellationToken)
            .ConfigureAwait(false);

        if (json)
        {
            CliJson.Write(output, CliJson.Ok("doctor", new JsonObject
            {
                ["dataDirectory"] = report.DataDirectory,
                ["configurationValid"] = report.ConfigurationValid,
                ["schemaVersion"] = report.SchemaVersion,
                ["keyProtector"] = report.KeyProtector,
                // Il contenimento dell'albero dei processi decide se un agente puo' partire:
                // senza, l'adapter rifiuta. Su Linux dipende dalla delega del cgroup, quindi e'
                // un fatto della macchina e va detto in diagnosi, non scoperto al primo turno.
                ["processContainment"] = report.ProcessContainment,
                ["contentMode"] = report.ContentMode,
                ["enabledTargets"] = report.EnabledServices,
                // Il modello locale e' l'unico percorso certificato in questa build.
                // Registrati non significa certificati: le CLI ufficiali hanno controlli ancora
                // non osservati, e i loro target restano da abilitare esplicitamente.
                ["registeredAdapters"] = new JsonArray(
                    AdapterKind.Ollama.ToWire(),
                    AdapterKind.OnnxLocal.ToWire(),
                    AdapterKind.ClaudeCodeCli.ToWire(),
                    AdapterKind.CodexCli.ToWire()),
                ["certifiedAdapters"] = new JsonArray(AdapterKind.Ollama.ToWire()),
                ["localTargets"] = LocalTargets(report),
                ["memoryConfigured"] = report.MemoryConfigured,
                // Memoria attiva senza modello di embedding: i turni proseguono e la ricerca e'
                // solo per parole, anche con memory.required.
                ["memoryLexicalOnly"] = report.MemoryLexicalOnly,
                // Il profilo tratta ogni richiesta come un segreto: nessun turno lascera' mai un ricordo.
                ["memoryNeverRecords"] = report.MemoryNeverRecords,
                // Ogni ricordo resta nella conversazione che lo ha prodotto: la memoria funziona e non attraversa.
                ["memorySessionScopedOnly"] = report.MemorySessionScopedOnly,
                // Analizzatore della richiesta e regola dell'art. 9, entrambi in ombra: dicono, non decidono.
                ["analyzer"] = report.Analyzer,
                // Modelli distinti su Ollama locale: oltre tre, senza GPU, Ollama li alterna e ricarica a freddo (ADR-031).
                ["localOllamaModels"] = report.LocalOllamaModels,
                // Tetto di spesa per richiesta e valuta del registro (ADR-032, fase 5); null senza tetto.
                ["maxCostPerRequest"] = report.MaxCostPerRequest,
                ["currency"] = report.Currency,
                // Zone e gradazione di ogni profilo, compilate come le compila un turno (ADR-029).
                ["profiles"] = ProfilesJson(report),
                ["memoryJobsPending"] = report.MemoryJobsPending,
                ["memoryJobsFailed"] = report.MemoryJobsFailed,
                // Un disallineamento del modello non si risolve aspettando: si dichiara prima del
                // turno e si chiude con `pa memory reindex` (AC-26).
                ["embeddingMismatch"] = report.EmbeddingMismatch,
                // Il rilevamento con modello e' acceso solo se un servizio pii-detection e'
                // abilitato; «installato» e' un fatto osservato adesso, non una dichiarazione.
                ["piiModel"] = PiiModelJson(report.PiiModel),
                ["tools"] = ToolsJson(report.Tools),
            }));

            return report.ConfigurationValid ? ExitCodes.Success : ExitCodes.InvalidInput;
        }

        DoctorText.Write(report, output);

        if (!report.ConfigurationValid)
        {
            return CommandContext.ReportIssues(parseResult, report.Issues);
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// Stato del rilevatore PII con modello, nella forma del documento. Null quando nessun servizio
    /// <c>pii-detection</c> con adapter <c>onnx-local</c> e' abilitato: in quel caso la scansione
    /// resta quella deterministica, che non e' un ripiego ma l'insieme di base.
    /// </summary>
    private static JsonObject? PiiModelJson(PiiModelDiagnosis? pii) =>
        pii is null
            ? null
            : new JsonObject
            {
                ["serviceId"] = pii.ServiceId,
                ["model"] = pii.Model,
                ["installed"] = pii.Installed,
                ["failureCode"] = pii.FailureCode,
                ["weightsDigest"] = pii.WeightsDigest,
            };

    /// <summary>
    /// Servizi osservati, nella forma del documento. La collocazione e' una dichiarazione
    /// dell'operatore, verificata dove possibile: <c>endpointSatisfiesPlacement</c> dice che
    /// l'indirizzo rispetta la regola della collocazione, non che nessun byte sia uscito. Quella
    /// resta una verifica d'ambiente, e <c>local_inference</c> compare come attestazione, mai come
    /// prova (ADR-016).
    /// </summary>
    private static JsonArray ProfilesJson(DiagnosisReport report) =>
    [
        .. report.Profiles.Select(profile => (JsonNode)new JsonObject
        {
            ["profileId"] = profile.ProfileId,
            ["personalDataGrading"] = profile.Grading.ToWire(),
            ["maxZone"] = new JsonObject(profile.MaxZone
                .OrderBy(entry => entry.Key)
                .Select(entry => KeyValuePair.Create(entry.Key.ToWire(), (JsonNode?)JsonValue.Create(entry.Value.ToWire())))),
            ["perimeter"] = new JsonArray([.. profile.Perimeter.Select(zone => (JsonNode)JsonValue.Create(zone.ToWire())!)]),
            ["confirmOutside"] = new JsonArray([.. profile.ConfirmOutside.Select(dataClass => (JsonNode)JsonValue.Create(dataClass.ToWire())!)]),
            ["attachmentsOutsidePerimeter"] = profile.AttachmentsOutsidePerimeter,
        }),
    ];

    private static JsonArray LocalTargets(DiagnosisReport report)
    {
        JsonArray targets = [];

        foreach (ServiceDiagnosis service in report.Services)
        {
            targets.Add(new JsonObject
            {
                ["id"] = service.Id,
                ["health"] = service.Probe.Health.ToWire(),
                ["model"] = service.Probe.Model,
                ["modelDigest"] = service.Probe.ExecutableHash,
                ["failureCode"] = service.Probe.FailureCode,
                ["inferencePlacement"] = service.Service?.InferencePlacement.ToWire(),
                ["endpointSatisfiesPlacement"] = service.EndpointSatisfiesPlacement,
                // Vero solo con un'attestazione dell'operatore valida adesso: la dichiarazione del registro non basta.
                ["localInferenceAttested"] = service.LocalInferenceAttested,
                // Zona di fiducia dai controlli effettivi adesso; assente per i servizi ausiliari.
                ["zone"] = service.Zone?.ToWire(),
                ["checkedAt"] = service.Probe.CheckedAt.ToString("O", Formats.Invariant),

                // «privacy ready» e' calcolato adesso, mai letto dal file: se e' falso si dice
                // quale condizione e' caduta, perche' un verdetto senza motivo non si corregge
                // (ADR-017).
                ["privacyReady"] = PrivacyReadyJson(service),
            });
        }

        return targets;
    }

    /// <summary>
    /// Verdetto «privacy ready» di un servizio agentico, con le condizioni cadute e il grado di
    /// conoscenza di ciascun controllo richiesto.
    ///
    /// I tre gradi non si confondono mai in una spunta: <c>osservato</c> lo ha verificato il
    /// router adesso, <c>attestato</c> lo ha dichiarato l'operatore con una scadenza,
    /// <c>ignoto</c> non lo ha affermato nessuno.
    /// </summary>
    private static JsonObject? PrivacyReadyJson(ServiceDiagnosis service)
    {
        // Il verdetto riguarda gli agenti: un modello raggiunto da un adapter non ha una
        // configurazione propria da isolare, e per quelli il rapporto non lo calcola.
        if (service.PrivacyReady is not PrivacyReadyVerdict verdict)
        {
            return null;
        }

        JsonArray failures = [];

        foreach (PrivacyReadyFailure failure in verdict.Failures)
        {
            failures.Add(new JsonObject
            {
                ["condition"] = failure.Condition.ToWire(),
                ["detail"] = failure.Detail,
            });
        }

        JsonObject grades = [];

        foreach ((string control, string grade) in service.ControlGrades ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            grades[control] = grade;
        }

        return new JsonObject
        {
            ["ready"] = verdict.Ready,
            ["failures"] = failures,
            ["controls"] = grades,
        };
    }

    private static int RegistryList(ParseResult parseResult, Option<DirectoryInfo?> dataOption, Option<bool> jsonOption)
    {
        bool json = parseResult.GetValue(jsonOption);
        TextWriter output = parseResult.InvocationConfiguration.Output;

        ConfigurationLoadResult configuration = new ConfigurationLoader(SystemClock.Instance)
            .Load(CommandContext.ResolveDataDirectory(parseResult, dataOption));

        if (!configuration.IsValid)
        {
            if (json)
            {
                CliJson.Write(output, CliJson.Invalid("registry list", configuration.Issues));
                return ExitCodes.InvalidInput;
            }

            return CommandContext.ReportIssues(parseResult, configuration.Issues);
        }

        ConfigurationSnapshot snapshot = configuration.Require();

        if (json)
        {
            JsonArray services = [];

            IReadOnlyDictionary<string, OrderPosition> order = ServiceOrder.Positions(snapshot);

            foreach (ServiceDescriptor service in snapshot.Registry.Services)
            {
                services.Add(new JsonObject
                {
                    ["id"] = service.Id,
                    ["capability"] = service.Capability.ToWire(),
                    ["adapter"] = service.Adapter.ToWire(),
                    ["inferencePlacement"] = service.InferencePlacement.ToWire(),
                    ["authMode"] = service.AuthMode.ToWire(),
                    ["enabled"] = service.Enabled,
                    ["capabilities"] = new JsonArray([.. service.Capabilities.Select(item => JsonValue.Create(item.ToWire()))]),
                    ["controls"] = new JsonArray([.. service.Controls.Select(item => JsonValue.Create(item.ToWire()))]),
                    // Quota e costo non osservati restano sconosciuti: nessuna stima.
                    ["quotaState"] = QuotaState.Unknown.ToWire(),
                    ["observed"] = false,
                    // Posto nell'ordine dei modelli (ADR-032, fase 3): l'unica scala della qualita', meta' del peso
                    // del ranking. Conta fra i servizi accesi che rispondono; senza posto vale null.
                    ["order"] = OrderText.Json(order.TryGetValue(service.Id, out OrderPosition place) ? place : null),
                    ["pricing"] = PricingText.Json(service),
                    // Origine della calibrazione di latenza, l'unica che si scrive nel registro: un valore
                    // dichiarato e' una preferenza dell'operatore, non una misura. Costo e quota non si
                    // dichiarano (ADR-032): vengono dal prezzo e dalla sonda.
                    ["calibrationSources"] = new JsonObject
                    {
                        ["latency"] = SourceOf(service.Latency),
                    },
                });
            }

            CliJson.Write(output, CliJson.Ok("registry list", new JsonObject
            {
                ["revision"] = snapshot.Registry.Revision,
                ["services"] = services,
            }));

            return ExitCodes.Success;
        }

        RegistryText.Write(snapshot, output.WriteLine, profileId: null);

        return ExitCodes.Success;
    }

    /// <summary>
    /// Origine di un valore di calibrazione. Un valore assente non e' zero e non e' una misura:
    /// vale neutro e viene detto tale.
    /// </summary>
    private static string SourceOf(CalibrationValue? value) =>
        value is CalibrationValue calibration ? calibration.Source.ToWire() : Ui.Text("assente");

    private static int PolicyValidate(ParseResult parseResult, Argument<FileInfo> pathArgument, Option<bool> jsonOption)
    {
        bool json = parseResult.GetValue(jsonOption);
        TextWriter output = parseResult.InvocationConfiguration.Output;
        FileInfo file = parseResult.GetValue(pathArgument)!;

        if (!file.Exists)
        {
            ValidationIssue missing = new(file.Name, "", null, ValidationCodes.MissingFile, Ui.Text("The file does not exist."));

            if (json)
            {
                CliJson.Write(output, CliJson.Invalid("policy validate", [missing]));
                return ExitCodes.InvalidInput;
            }

            return CommandContext.ReportIssues(parseResult, [missing]);
        }

        List<ValidationIssue> issues = [];

        if (RestrictedYamlReader.TryRead(file.Name, File.ReadAllText(file.FullName), issues, out ParsedDocument? document))
        {
            JsonSchemaValidator.Validate(ContractSchema.Policy, file.Name, document!, issues);
        }

        if (issues.Count > 0)
        {
            if (json)
            {
                CliJson.Write(output, CliJson.Invalid("policy validate", issues));
                return ExitCodes.InvalidInput;
            }

            return CommandContext.ReportIssues(parseResult, issues);
        }

        PolicyDocument profile = ContractDocuments.ReadPolicy(document!.Root);

        if (json)
        {
            CliJson.Write(output, CliJson.Ok("policy validate", new JsonObject
            {
                ["profileId"] = profile.ProfileId,
                ["revision"] = profile.Revision,
                // Dal 25 settembre 2026 (ADR-032, fase 4) dove vanno i dati lo dicono le sole zone.
                ["maxZone"] = new JsonObject(PolicyZones.DeclaredClasses.Select(dataClass =>
                    KeyValuePair.Create(dataClass.ToWire(), (JsonNode?)JsonValue.Create(profile.Constraints.Zones.For(dataClass).ToWire())))),
                ["minimumDataClass"] = profile.Constraints.MinimumDataClass.ToWire(),
            }));

            return ExitCodes.Success;
        }

        output.WriteLine(Ui.Text("Profilo valido: ") + profile.ProfileId);
        output.WriteLine(Ui.Text("La validita' strutturale non significa che i target siano utilizzabili."));

        return ExitCodes.Success;
    }

    private static int AuditShow(
        ParseResult parseResult,
        Option<DirectoryInfo?> dataOption,
        Argument<string?> executionArgument,
        Option<bool> jsonOption)
    {
        bool json = parseResult.GetValue(jsonOption);
        TextWriter output = parseResult.InvocationConfiguration.Output;
        TextWriter error = parseResult.InvocationConfiguration.Error;
        StorePaths auditPaths = StorePaths.For(CommandContext.ResolveDataDirectory(parseResult, dataOption));
        string? chosen = parseResult.GetValue(executionArgument);

        if (string.IsNullOrWhiteSpace(chosen))
        {
            // Senza identificatore, l'ultima esecuzione: e' quella che si vuole guardare quasi sempre.
            if (AuditQueries.Latest(auditPaths) is not string latest)
            {
                if (json)
                {
                    CliJson.Write(output, CliJson.Error("audit show", ErrorCode.InputInvalid, Ui.Text("audit.none")));
                }
                else
                {
                    error.WriteLine(Ui.Text("audit.none"));
                }

                return ExitCodes.InvalidInput;
            }

            chosen = latest;
        }

        string executionId = chosen;

        AuditTrail? trail;

        try
        {
            // La lettura e' un'operazione condivisa (S63, `AuditQueries`): il terminale interattivo
            // mostra la stessa traccia senza riscrivere le due query, che divergendo racconterebbero
            // due storie diverse dello stesso turno.
            trail = AuditQueries.Read(
                StorePaths.For(CommandContext.ResolveDataDirectory(parseResult, dataOption)),
                executionId);
        }
        catch (PaRouterException exception) when (exception.Code is ErrorCode.StoreUnavailable)
        {
            if (json)
            {
                CliJson.Write(output, CliJson.Error("audit show", ErrorCode.StoreUnavailable, Ui.Text("The store does not exist yet.")));
                return ExitCodes.StoreUnavailable;
            }

            error.WriteLine(Ui.Text("STORE_UNAVAILABLE: lo store non esiste ancora."));
            return ExitCodes.StoreUnavailable;
        }

        if (trail is null)
        {
            if (json)
            {
                CliJson.Write(output, CliJson.Error("audit show", ErrorCode.InputInvalid, Ui.Text("No audit entry for this execution.")));
                return ExitCodes.InvalidInput;
            }

            error.WriteLine(Ui.Text("Nessuna traccia di audit per questa esecuzione."));
            return ExitCodes.InvalidInput;
        }

        JsonArray events = [.. trail.Events.Select(entry => (JsonNode?)new JsonObject
        {
            ["sequence"] = entry.Sequence,
            ["code"] = entry.Code,
            ["metadata"] = JsonNode.Parse(entry.MetadataJson),
            ["createdAtMs"] = entry.CreatedAtMs,
        })];

        JsonArray attempts = [.. trail.Attempts.Select(attempt => (JsonNode?)new JsonObject
        {
            ["ordinal"] = attempt.Ordinal,
            ["targetId"] = attempt.TargetId,
            ["state"] = attempt.State,
            ["sideEffectState"] = attempt.SideEffectState,
            ["errorCode"] = attempt.ErrorCode,
        })];

        // Le valutazioni dei candidati: la tabella era scritta e non letta da nessuno, quindi
        // l'audit diceva quale servizio fosse stato scelto e non perche' avesse vinto (S75).
        JsonArray decisions = [.. trail.Decisions.Select(decision => (JsonNode?)new JsonObject
        {
            ["ordinal"] = decision.Ordinal,
            ["targetId"] = decision.TargetId,
            ["allowed"] = decision.Allowed,
            ["reasons"] = JsonNode.Parse(decision.ReasonsJson),
            ["score"] = decision.ScoreJson is string score ? JsonNode.Parse(score) : null,
        })];

        if (json)
        {
            CliJson.Write(output, CliJson.Ok("audit show", new JsonObject
            {
                ["executionId"] = executionId,
                ["events"] = events,
                ["attempts"] = attempts,
                ["decisions"] = decisions,
            }));

            return ExitCodes.Success;
        }

        // I nomi dei servizi vengono dalla configurazione; se non si legge, restano gli identificatori.
        ConfigurationLoadResult configuration = new ConfigurationLoader(SystemClock.Instance)
            .Load(CommandContext.ResolveDataDirectory(parseResult, dataOption));

        AuditText.Write(trail, configuration.IsValid ? configuration.Require() : null, output.WriteLine);

        return ExitCodes.Success;
    }
}
