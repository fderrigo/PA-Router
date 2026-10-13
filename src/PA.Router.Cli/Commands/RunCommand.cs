using System.CommandLine;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Output;
using PA.Router.Core;
using PA.Router.Extensions;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Storage;
using PA.Router.Memory;
using PA.Router.Policy;
using PA.Router.Privacy;
using PA.Router.Registry.Loading;
using PA.Router.Routing;
using PA.Router.Runtime.Ipc;
using PA.Router.Session;

namespace PA.Router.Cli.Commands;

/// <summary>
/// <c>pa run</c> e <c>pa explain</c>.
///
/// Il prompt arriva solo da standard input: passarlo come argomento lo lascerebbe nella cronologia
/// della shell. Con <c>--json</c> lo standard output contiene un solo documento; l'avanzamento va
/// su standard error. <c>--target</c> restringe la scelta, non aggira la policy
/// (03-specs/CLI-TUI.md).
/// </summary>
internal static class RunCommand
{
    /// <summary>
    /// Turni di cronologia riportati nel contesto. Il budget completo lo impone la policy con
    /// <c>maxContextBytes</c>; questo e' il tetto sul numero, per non far crescere il payload a
    /// ogni turno.
    /// </summary>
    private const int HistoryTurns = 8;

    internal static Command CreateRun(Option<DirectoryInfo?> dataOption)
    {
        Option<bool> stdinOption = new("--stdin") { Description = Ui.Text("Read the prompt from standard input.") };
        Option<string?> profileOption = new("--profile") { Description = Ui.Text("Profile to apply. Defaults to the configured one.") };
        Option<string?> sessionOption = new("--session") { Description = Ui.Text("Session to continue.") };
        Option<string?> targetOption = new("--target") { Description = Ui.Text("Restrict the choice to this target.") };
        Option<string?> skillOption = new("--skill") { Description = Ui.Text("Add the instructions of an enabled skill to the payload.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };
        (Option<DirectoryInfo?> workspaceOption, Option<bool> writableOption, Option<bool> allowShellOption) = FenceOptions();
        Option<bool> confirmWriteOption = new("--confirm-write")
        {
            Description = Ui.Text("Confirm that the agent may modify files in the workspace for this turn."),
        };
        Option<bool> confirmShellOption = new("--confirm-shell")
        {
            Description = Ui.Text("Confirm that the agent may run system commands for this turn."),
        };
        Option<bool> confirmExternalOption = new("--confirm-external")
        {
            Description = Ui.Text("Confirm, for this turn, sending data outside the perimeter when the profile asks for it."),
        };

        // Equivalente non interattivo della menzione `@percorso` del terminale (S63). Il contenuto
        // e' un dato: entra nel payload con la sua provenienza e riattraversa privacy e policy.
        Option<string[]> attachOption = new("--attach")
        {
            Description = Ui.Text("Attach the contents of a text file to the request; repeatable."),
            AllowMultipleArgumentsPerToken = true,
        };

        // Superficie per gli script (S64). Gli eventi sono gli stessi che il terminale disegna,
        // resi con nomi stabili: un consumatore automatico non deve leggere italiano per sapere
        // cos'e' successo.
        Option<string?> eventsOption = new("--events")
        {
            Description = Ui.Text("Stream turn events on standard error, one JSON object per line: jsonl."),
        };

        Option<FileInfo?> lastMessageOption = new("--output-last-message")
        {
            Description = Ui.Text("Write the final answer to this file, without progress or decoration."),
        };

        Option<string?> colorOption = new("--color")
        {
            Description = Ui.Text("Colour and borders: always, never or auto. Auto follows the terminal and NO_COLOR."),
        };

        Command command = new("run", Ui.Text("Run one turn against the selected target."))
        {
            dataOption,
            stdinOption,
            profileOption,
            sessionOption,
            targetOption,
            skillOption,
            attachOption,
            workspaceOption,
            writableOption,
            allowShellOption,
            confirmWriteOption,
            confirmShellOption,
            confirmExternalOption,
            eventsOption,
            lastMessageOption,
            colorOption,
            jsonOption,
        };

        // `pa run "ciao"` e' il modo in cui lo scrive chiunque: invece dell'aiuto intero, una frase che dice perche' la
        // domanda arriva da standard input e come mandarla (24 settembre 2026).
        command.TreatUnmatchedTokensAsErrors = false;

        command.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            parseResult, dataOption, stdinOption, profileOption, sessionOption, targetOption, skillOption,
            attachOption, workspaceOption, writableOption, allowShellOption, confirmWriteOption, confirmShellOption,
            eventsOption, lastMessageOption, colorOption,
            jsonOption, explainOnly: false, cancellationToken, confirmExternalOption));

        return command;
    }

    internal static Command CreateExplain(Option<DirectoryInfo?> dataOption)
    {
        Option<bool> stdinOption = new("--stdin") { Description = Ui.Text("Read the prompt from standard input.") };
        Option<string?> profileOption = new("--profile") { Description = Ui.Text("Profile to apply. Defaults to the configured one.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };
        (Option<DirectoryInfo?> workspaceOption, Option<bool> writableOption, Option<bool> allowShellOption) = FenceOptions();

        Option<string?> colorOption = new("--color")
        {
            Description = Ui.Text("Colour and borders: always, never or auto. Auto follows the terminal and NO_COLOR."),
        };

        Command command = new("explain", Ui.Text("Analyse a request locally, without dispatching it."))
        {
            dataOption,
            stdinOption,
            profileOption,
            workspaceOption,
            writableOption,
            allowShellOption,
            jsonOption,
        };

        command.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            parseResult, dataOption, stdinOption, profileOption, null, null, null, null,
            workspaceOption, writableOption, allowShellOption, null, null,
            // L'analisi locale non esegue: non ha eventi da trasmettere ne' una risposta da salvare.
            null, null, colorOption,
            jsonOption, explainOnly: true, cancellationToken));

        return command;
    }

    /// <summary>
    /// Contenuti allegati dall'operatore (S63).
    ///
    /// La lettura e' quella del terminale (<c>TurnAttachments</c>): se <c>--attach</c> accettasse un
    /// file che <c>@percorso</c> rifiuta, la stessa richiesta darebbe due esiti (INV-22). Il budget
    /// e' quello di contesto del profilo, non un numero di questo comando.
    /// </summary>
    private static IReadOnlyList<ContextItem>? Attachments(string[]? paths, PolicyDocument profile, ConfigurationSnapshot snapshot)
    {
        if (paths is not { Length: > 0 })
        {
            return null;
        }

        int budget = PolicyCompiler.Compile(profile, snapshot.Config).MaxContextBytes;

        return [.. paths.Select(path => TurnAttachments.Read(path, budget))];
    }

    /// <summary>
    /// Il recinto del turno (ADR-019): la cartella, la scrittura, i comandi di sistema. Le capacita'
    /// non si dichiarano: il router classifica la richiesta e sceglie il minimo dentro il recinto.
    /// </summary>
    private static (Option<DirectoryInfo?> Workspace, Option<bool> Writable, Option<bool> AllowShell) FenceOptions() =>
        (
            new Option<DirectoryInfo?>("--workspace")
            {
                Description = Ui.Text("Fence: the directory an agent may work in. Without it every request stays a chat."),
            },
            new Option<bool>("--writable")
            {
                Description = Ui.Text("Fence: an agent may modify files in the workspace, which becomes a git repository."),
            },
            new Option<bool>("--allow-shell")
            {
                Description = Ui.Text("Fence: an agent may run system commands; every turn still needs --confirm-shell."),
            });

    private static async Task<int> ExecuteAsync(
        ParseResult parseResult,
        Option<DirectoryInfo?> dataOption,
        Option<bool> stdinOption,
        Option<string?> profileOption,
        Option<string?>? sessionOption,
        Option<string?>? targetOption,
        Option<string?>? skillOption,
        Option<string[]>? attachOption,
        Option<DirectoryInfo?> workspaceOption,
        Option<bool> writableOption,
        Option<bool> allowShellOption,
        Option<bool>? confirmWriteOption,
        Option<bool>? confirmShellOption,
        Option<string?>? eventsOption,
        Option<FileInfo?>? lastMessageOption,
        Option<string?>? colorOption,
        Option<bool> jsonOption,
        bool explainOnly,
        CancellationToken cancellationToken,
        Option<bool>? confirmExternalOption = null)
    {
        bool json = parseResult.GetValue(jsonOption);
        string command = explainOnly ? "explain" : "run";
        TextWriter output = parseResult.InvocationConfiguration.Output;
        TextWriter error = parseResult.InvocationConfiguration.Error;

        // Il colore si decide **prima** di qualunque resa, e in un punto solo (S64). Un valore non
        // riconosciuto e' un errore dell'operatore, non un caso da ignorare: e' la stessa regola
        // degli override d'ambiente (S52).
        if (colorOption is not null && !TryUseColor(parseResult.GetValue(colorOption)))
        {
            return Report(output, error, json, command, ErrorCode.InputInvalid,
                Ui.Text("The --color value must be always, never or auto."), ExitCodes.UsageError);
        }

        if (!TryObserver(eventsOption is null ? null : parseResult.GetValue(eventsOption), error, out ITurnObserver? observer))
        {
            return Report(output, error, json, command, ErrorCode.InputInvalid,
                Ui.Text("The --events value must be jsonl."), ExitCodes.UsageError);
        }

        EventsOnError.Value = observer is not null;

        // Senza eventi, l'unica fase che `pa run` dice e' la pausa di un servizio per quota finita: e' un cambiamento che
        // vale anche per i turni dopo, e chi guarda il terminale deve saperlo (24 settembre 2026).
        observer ??= new QuotaNoticeObserver(error);

        FileInfo? lastMessage = lastMessageOption is null ? null : parseResult.GetValue(lastMessageOption);

        if (parseResult.UnmatchedTokens.Count > 0)
        {
            string first = parseResult.UnmatchedTokens[0];

            return Report(output, error, json, command, ErrorCode.InputInvalid,
                first.StartsWith('-')
                    ? Ui.Format("run.unknownOption {0}", first)
                    : Ui.Text("run.promptArgument"),
                ExitCodes.UsageError);
        }

        if (!parseResult.GetValue(stdinOption))
        {
            return Report(output, error, json, command, ErrorCode.InputInvalid,
                Ui.Text("The prompt must come from standard input: pass --stdin."), ExitCodes.UsageError);
        }

        string input = await ReadPromptAsync(parseResult, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(input))
        {
            return Report(output, error, json, command, ErrorCode.InputInvalid,
                Ui.Text("The prompt is empty."), ExitCodes.InvalidInput);
        }

        string dataDirectory = CommandContext.ResolveDataDirectory(parseResult, dataOption);
        ConfigurationLoadResult configuration = new ConfigurationLoader(SystemClock.Instance).Load(dataDirectory);

        if (!configuration.IsValid)
        {
            if (json)
            {
                CliJson.Write(output, CliJson.Invalid(command, configuration.Issues));
                return ExitCodes.InvalidInput;
            }

            return CommandContext.ReportIssues(parseResult, configuration.Issues);
        }

        ConfigurationSnapshot snapshot = configuration.Require();
        string profileId = parseResult.GetValue(profileOption) ?? snapshot.Config.DefaultProfileId;

        if (!snapshot.Profiles.TryGetValue(profileId, out PolicyDocument? profile))
        {
            return Report(output, error, json, command, ErrorCode.PolicyInvalid,
                Ui.Text("The requested profile is not defined by any policy file."), ExitCodes.InvalidInput);
        }

        // Il recinto lo dichiara l'operatore; dentro il recinto il router classifica la richiesta e
        // sceglie il minimo necessario, con la stessa funzione del REPL (ADR-019, INV-22).
        if (!SessionFence.TryCreate(
                parseResult.GetValue(workspaceOption)?.FullName,
                parseResult.GetValue(writableOption),
                parseResult.GetValue(allowShellOption),
                out SessionFence? fence))
        {
            return Report(output, error, json, command, ErrorCode.InputInvalid,
                Ui.Text("The workspace directory does not exist."), ExitCodes.InvalidInput);
        }

        bool confirmWrite = confirmWriteOption is not null && parseResult.GetValue(confirmWriteOption);
        bool confirmShell = confirmShellOption is not null && parseResult.GetValue(confirmShellOption);
        bool confirmExternal = confirmExternalOption is not null && parseResult.GetValue(confirmExternalOption);

        TurnPlan plan = await TurnPlanning
            .PlanAsync(snapshot, profileId, input, fence, confirmWrite, cancellationToken)
            .ConfigureAwait(false);

        // Senza terminale non si fanno domande: la conferma e' un'opzione dell'invocazione, e senza
        // la modifica non avviene.
        if (!explainOnly && plan.NeedsWriteConfirmation)
        {
            return Report(output, error, json, command, ErrorCode.InputInvalid,
                Ui.Text("The request would modify files in ") + plan.Workspace!.RootPath + Ui.Text(": add --confirm-write to allow it."),
                ExitCodes.InvalidInput);
        }

        if (!explainOnly && plan.NeedsShellConfirmation && !confirmShell)
        {
            return Report(output, error, json, command, ErrorCode.InputInvalid,
                Ui.Text("The request would run system commands in ") + plan.Workspace!.RootPath + Ui.Text(": add --confirm-shell to allow it."),
                ExitCodes.InvalidInput);
        }

        IReadOnlyList<ContextItem>? attachments;

        try
        {
            // Gli allegati si leggono **prima** del turno, con il budget di contesto del profilo: un
            // file che non si puo' allegare ferma la richiesta dove l'operatore lo puo' correggere,
            // invece di produrre una domanda diversa da quella scritta.
            attachments = Attachments(attachOption is null ? null : parseResult.GetValue(attachOption), profile, snapshot);
        }
        catch (PaRouterException exception)
        {
            return Report(output, error, json, command, exception.Code, exception.Message, ExitCodes.InvalidInput);
        }

        try
        {
            return await RunTurnAsync(
                parseResult, snapshot, profile, input, command, json, explainOnly,
                sessionOption is null ? null : parseResult.GetValue(sessionOption),
                targetOption is null ? null : parseResult.GetValue(targetOption),
                skillOption is null ? null : parseResult.GetValue(skillOption),
                attachments,
                observer,
                lastMessage,
                plan,
                confirmExternal,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Report(output, error, json, command, ErrorCode.Cancelled,
                Ui.Text("The turn was cancelled."), ExitCodes.Cancelled);
        }
        catch (PaRouterException ex)
        {
            return Report(output, error, json, command, ex.Code, ex.Message, ExitCodes.ForStorage(ex.Code));
        }
    }

    private static async Task<int> RunTurnAsync(
        ParseResult parseResult,
        ConfigurationSnapshot snapshot,
        PolicyDocument profile,
        string input,
        string command,
        bool json,
        bool explainOnly,
        string? sessionId,
        string? targetId,
        string? skillId,
        IReadOnlyList<ContextItem>? attachments,
        ITurnObserver? observer,
        FileInfo? lastMessage,
        TurnPlan plan,
        bool confirmExternal,
        CancellationToken cancellationToken)
    {
        TextWriter output = parseResult.InvocationConfiguration.Output;
        TextWriter error = parseResult.InvocationConfiguration.Error;

        if (explainOnly)
        {
            // L'analisi locale non emette grant, non tocca alcun adapter e non scrive: si apre
            // senza prendere la proprieta' dello store, cosi' convive con un worker vivo.
            await using RouterStack readOnly = RouterStack.Open(snapshot, ownsStore: false);

            CompiledPolicy explainPolicy = PolicyCompiler.Compile(profile, snapshot.Config);

            return Explain(
                output,
                error,
                json,
                snapshot,
                explainPolicy,
                LimitsOf(explainPolicy),
                input,
                readOnly,
                targetId,
                plan);
        }

        Progress(error, json, Ui.Text("Analisi locale -> Policy -> Selezione target -> Esecuzione -> Controllo risposta"));
        Progress(error, json, Ui.Text("Classificazione: ") + PlanText.Describe(plan));

        foreach (string limit in plan.Limits)
        {
            Progress(error, json, "  " + limit);
        }

        if (skillId is not null)
        {
            // La skill e' una scelta esplicita dell'operatore, dichiarata prima del turno: entra
            // nel payload come testo selezionato e non concede permessi.
            Progress(error, json, "Skill: " + skillId + Ui.Text(" selezionata dall'operatore"));
        }

        // Una sola richiesta, costruita una volta: il percorso del worker e quello locale la
        // ricevono identica, invece di ricostruirla ciascuno con i campi che ricorda. Con il
        // payload composto a mano, la skill non arrivava al worker.
        TurnRequest request = new(
            profile, input, sessionId, targetId, skillId, plan.Capabilities, plan.Workspace, attachments)
        {
            ExternalConfirmed = confirmExternal,
        };

        // Quando un worker possiede gia' la directory dati, il turno gira li'. E' l'unico modo di
        // avere piu' client sullo stesso store, ed e' cio' che la specifica del runtime locale
        // descrive: CLI e API sono client del worker.
        if (await TryConnectAsync(snapshot.DataDirectory, cancellationToken).ConfigureAwait(false) is IpcClient client)
        {
            await using (client)
            {
                RunResultPayload remote = await client.RunAsync(TurnRequests.ToPayload(request), cancellationToken)
                    .ConfigureAwait(false);

                // Con un worker vivo il canale non trasporta eventi (S65): `--events` non produce
                // righe, e dirlo e' meglio che lasciarle mancare in silenzio.
                if (observer is not null)
                {
                    error.WriteLine(
                        Ui.Text("Eventi non disponibili: il turno gira nel worker e il canale non li trasporta ancora."));
                }

                WriteLastMessage(lastMessage, remote.Output);

                if (!confirmExternal && remote.Reasons.Contains(PolicyReason.ExternalConfirmationRequired.ToWire()))
                {
                    ConfirmationHint(error);
                }

                return WriteRemote(output, error, json, remote, snapshot);
            }
        }

        await using RouterStack runtime = RouterStack.Open(snapshot);

        // Gli eventi del turno sono gli stessi che il terminale disegna: qui li rende `--events`
        // (S64). L'evento e' l'operazione, scritta una volta; il disegno e' di ciascun front end.
        TurnOutcome outcome = await TurnRunner
            .RunAsync(runtime, observer is null ? request : request with { Observer = Named(observer, snapshot) }, cancellationToken)
            .ConfigureAwait(false);

        if (outcome.SkillMissing)
        {
            return Report(output, error, json, command, ErrorCode.ExtensionPermissionDenied,
                Ui.Text("That skill is not available: install and enable its package first."), ExitCodes.PolicyDenied);
        }

        ExecutionResult result = outcome.Result!;

        if (outcome.Memory.Items.Count > 0)
        {
            Progress(
                error,
                json,
                outcome.Memory.Selected.Count == 1
                    ? Ui.Format("memory.injectedOne {0}", outcome.Memory.UsedBytes.ToString(Formats.Invariant))
                    : Ui.Format("memory.injected {0} {1}", outcome.Memory.Selected.Count.ToString(Formats.Invariant), outcome.Memory.UsedBytes.ToString(Formats.Invariant)));
        }

        // Senza worker il turno lascerebbe il proprio lavoro di indicizzazione in coda e il turno
        // successivo non troverebbe nulla (difetto D1). Chi possiede lo store lo svuota prima di
        // uscire; un errore qui non cambia l'esito del turno, che e' gia' concluso.
        await DrainMemoryAsync(runtime, snapshot, profile, outcome.SessionId, error, json, cancellationToken)
            .ConfigureAwait(false);

        WriteLastMessage(lastMessage, result.Output);

        if (!confirmExternal
            && result.Decisions.Any(decision => decision.Reasons.Contains(PolicyReason.ExternalConfirmationRequired)))
        {
            ConfirmationHint(error);
        }

        return json ? WriteRunJson(output, result, outcome.Memory) : WriteRunText(output, error, result, snapshot);
    }

    /// <summary>
    /// Senza terminale non si fanno domande (ADR-029, decisione I): la conferma dell'uscita dal perimetro e' un'opzione
    /// dell'invocazione, e senza il turno resta negato. Lo si dice, invece di lasciare solo il codice.
    /// </summary>
    private static void ConfirmationHint(TextWriter error) =>
        error.WriteLine(Ui.Text("The profile asks for confirmation before sending these data outside its perimeter: add --confirm-external to allow it."));

    /// <summary>Limiti di privacy derivati dal profilo compilato.</summary>
    private static PrivacyLimits LimitsOf(CompiledPolicy policy) => PrivacyLimits.Default with
    {
        MaxInputBytes = policy.MaxInputBytes,
        MaxContextBytes = policy.MaxContextBytes,
        MaxOutputBytes = policy.MaxOutputBytes,
    };

    /// <summary>
    /// Client del worker se ce n'e' uno in ascolto. Non ne avvia uno: avviare un processo in
    /// sottofondo e' una decisione di `pa runtime start`, non l'effetto collaterale di un turno.
    /// </summary>
    private static async Task<IpcClient?> TryConnectAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        try
        {
            return await IpcClient.ConnectAsync(dataDirectory, TimeSpan.FromMilliseconds(300), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PaRouterException)
        {
            // Nessun worker in ascolto: il turno gira qui.
            return null;
        }
    }

    /// <summary>
    /// Svuota la coda lasciata dal turno, con l'unica implementazione che esiste.
    /// </summary>
    private static async Task DrainMemoryAsync(
        RouterStack runtime,
        ConfigurationSnapshot snapshot,
        PolicyDocument profile,
        string sessionId,
        TextWriter error,
        bool json,
        CancellationToken cancellationToken)
    {
        _ = snapshot;

        int? executed = await TurnMemory
            .DrainAsync(runtime, profile, sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (executed is null)
        {
            Progress(error, json, Ui.Text("Memoria: indicizzazione rinviata"));
            return;
        }

        if (executed > 0)
        {
            Progress(error, json, executed == 1
                ? Ui.Text("memory.saved.one")
                : Ui.Format("memory.saved.many {0}", executed.Value.ToString(Formats.Invariant)));
        }
    }

    /// <summary>Stampa l'esito di un turno eseguito dal worker.</summary>
    private static int WriteRemote(TextWriter output, TextWriter error, bool json, RunResultPayload result, ConfigurationSnapshot snapshot)
    {
        if (json)
        {
            JsonObject document = CliJson.Ok("run", new JsonObject
            {
                ["executionId"] = result.ExecutionId,
                ["state"] = result.Status,
                ["targetId"] = result.ServiceId,
                // Gli stessi due campi dell'esito locale (S77): la resa del worker non li aveva, e
                // uno script che legge il JSON non poteva sapere che un agente puo' aver lasciato
                // effetti. Le due uscite ora rispondono alle stesse domande.
                ["sideEffects"] = result.SideEffects,
                ["attemptsUsed"] = result.AttemptsUsed,
                ["output"] = result.Output,
                ["errorCode"] = result.ErrorCode,
                ["reasons"] = new JsonArray([.. result.Reasons.Select(reason => JsonValue.Create(reason))]),
                ["memoryPending"] = result.MemoryPending,
                ["executedBy"] = "worker",
                ["weakPerimeter"] = result.WeakPerimeter ? true : null,
                ["belowDemand"] = result.BelowDemand ? true : null,
            });

            // Come l'esito locale: `ok` e' vero solo per un turno completato. Dal worker un turno fallito usciva con
            // `"ok": true` e uno script che leggeva solo quel campo lo prendeva per riuscito (23 settembre 2026).
            document["ok"] = result.Status is "completed";
            CliJson.Write(output, document);

            return ExitCodeFor(result.Status);
        }

        if (result.Output is string text)
        {
            output.WriteLine(text);

            if (result.WeakPerimeter)
            {
                error.WriteLine(Ui.Text("turn.weakPerimeter"));
            }

            if (result.BelowDemand)
            {
                error.WriteLine(Ui.Text("turn.belowDemand"));
            }
        }
        else
        {
            error.WriteLine(Ui.Text("Nessuna risposta: ") + (result.ErrorCode ?? result.Status));

            Explain(error, result.Denial, result.ErrorCode, snapshot);

            foreach (string reason in result.Reasons)
            {
                error.WriteLine("  " + reason);
            }
        }

        return ExitCodeFor(result.Status);
    }

    private static int ExitCodeFor(string state) => state switch
    {
        "completed" => ExitCodes.Success,
        "denied" => ExitCodes.PolicyDenied,
        "cancelled" => ExitCodes.Cancelled,
        "uncertain" => ExitCodes.Uncertain,
        _ => ExitCodes.TransportError,
    };

    private static int Explain(
        TextWriter output,
        TextWriter error,
        bool json,
        ConfigurationSnapshot snapshot,
        CompiledPolicy policy,
        PrivacyLimits limits,
        string input,
        RouterStack runtime,
        string? targetId,
        TurnPlan plan)
    {
        LocalAnalysisResult local = LocalAnalysis.Evaluate(
            policy.ProfileId, input, runtime, targetId, plan.Capabilities);

        RequestProfile profile = local.Profile;
        BandRequirements band = local.Band;
        PrivacyAnalysis analysis = local.Analysis;
        PolicyExplain explain = local.Explain;
        IReadOnlySet<Capability> required = local.RequiredCapabilities;

        if (json)
        {
            JsonArray decisions = [];

            foreach (PolicyDecision decision in explain.Decisions)
            {
                decisions.Add(new JsonObject
                {
                    ["targetId"] = decision.TargetId,
                    ["allowed"] = decision.Allowed,
                    ["reasons"] = new JsonArray([.. decision.Reasons.Select(reason => JsonValue.Create(reason.ToWire()))]),
                });
            }

            CliJson.Write(output, CliJson.Ok("explain", new JsonObject
            {
                ["snapshotDigest"] = snapshot.Digest,
                ["analysisVersion"] = profile.Version,
                ["algorithmVersion"] = RankingAlgorithm.Version,
                ["requiredCapabilities"] = new JsonArray(
                    [.. required.Select(capability => JsonValue.Create(capability.ToWire()))]),
                ["intent"] = profile.Intent.ToWire(),
                // Che cosa il testo sembra chiedere e che cosa il recinto e il profilo concedono.
                ["classification"] = PlanText.Json(plan),
                ["payloadBytes"] = profile.PayloadBytes,
                ["estimatedTokens"] = profile.EstimatedTokens,
                ["band"] = profile.Band.ToWire(),
                ["bandRequirements"] = new JsonObject
                {
                    ["minContextTokens"] = band.MinContextTokens,
                },
                ["dataClass"] = analysis.Payload.EffectiveClass.ToWire(),
                ["scanCertainty"] = analysis.FinalReport.Certainty.ToWire(),
                ["findingCounts"] = new JsonObject(
                    analysis.InitialReport.TypeCounts.Select(entry =>
                        new KeyValuePair<string, JsonNode?>(entry.Key.ToWire(), JsonValue.Create(entry.Value)))),
                ["payloadDigest"] = analysis.Payload.PayloadDigest,
                ["admissible"] = new JsonArray([.. explain.Admissible.Select(target => JsonValue.Create(target.TargetId))]),
                // Posto degli ammessi nell'ordine dei modelli (ADR-032, fase 3): il peso maggiore del ranking.
                ["order"] = new JsonObject(explain.Admissible.Select(target => new KeyValuePair<string, JsonNode?>(
                    target.TargetId,
                    OrderText.Json(local.Order.TryGetValue(target.TargetId, out OrderPosition place) ? place : null)))),
                // Costo stimato con la stessa stima del ranking; a punteggio uguale vince il minore.
                ["estimatedCosts"] = PricingText.Json(local.Costs),
                ["currency"] = local.Currency,
                // Tetto di spesa (ADR-032, fase 5): null senza tetto.
                ["maxCostPerRequest"] = local.MaxCostPerRequest,
                ["decisions"] = decisions,
            }));

            return explain.IsEmpty ? ExitCodes.PolicyDenied : ExitCodes.Success;
        }

        ExplainText.Write(local, plan, output.WriteLine);

        if (explain.IsEmpty)
        {
            error.WriteLine(Ui.Text("Nessun target ammissibile per questo profilo."));
            return ExitCodes.PolicyDenied;
        }

        return ExitCodes.Success;
    }

    private static int WriteRunJson(TextWriter output, ExecutionResult result, MemoryAugmentationResult memory)
    {
        JsonArray injected = [];

        foreach (string id in memory.Selected)
        {
            injected.Add(id);
        }

        JsonObject payload = new()
        {
            ["memoryStatus"] = memory.Status.ToWire(),
            ["memoryMode"] = memory.Mode.ToWire(),
            ["memoryInjected"] = injected,
            ["memoryBytes"] = memory.UsedBytes,
            ["executionId"] = result.ExecutionId,
            ["state"] = result.State.ToWire(),
            ["sideEffects"] = result.SideEffects.ToWire(),
            ["attemptsUsed"] = result.AttemptsUsed,
            ["output"] = result.Output,
        };

        if (result.WeakPerimeter)
        {
            payload["weakPerimeter"] = true;
        }

        if (result.BelowDemand)
        {
            payload["belowDemand"] = true;
        }

        if (result.ErrorCode is ErrorCode code)
        {
            payload["errorCode"] = code.ToWire();
        }

        JsonObject document = CliJson.Ok("run", payload);
        document["ok"] = result.State is ExecutionState.Completed;

        CliJson.Write(output, document);

        return ExitCodeFor(result);
    }

    private static int WriteRunText(TextWriter output, TextWriter error, ExecutionResult result, ConfigurationSnapshot snapshot)
    {
        if (result.Output is string text)
        {
            // **Il testo di un fornitore non raggiunge il terminale cosi' com'e'** (difetto trovato
            // dall'audit, S77).
            //
            // Il REPL fa passare ogni risposta da un solo imbuto che toglie le sequenze di
            // controllo; `pa run` in modalita' testo no, e scriveva il testo grezzo. Lo screening
            // della privacy decide **se** una risposta puo' comparire, non che cosa faccia al
            // terminale di chi la legge: una sequenza ANSI sposta il cursore e riscrive righe gia'
            // stampate dal router, comprese quelle che dichiarano l'esito.
            output.WriteLine(SafeText.Sanitize(text));
        }

        if (result.WeakPerimeter)
        {
            error.WriteLine(Ui.Text("turn.weakPerimeter"));
        }

        if (result.BelowDemand)
        {
            error.WriteLine(Ui.Text("turn.belowDemand"));
        }

        if (result.State is not ExecutionState.Completed)
        {
            error.WriteLine(
                Ui.Text("Esito: ") + result.State.ToWire()
                + (result.ErrorCode is ErrorCode code ? " (" + code.ToWire() + ")" : string.Empty));

            Explain(error, result.Denial, result.ErrorCode?.ToWire(), snapshot);
        }

        return ExitCodeFor(result);
    }

    private static ITurnObserver Named(ITurnObserver observer, ConfigurationSnapshot snapshot)
    {
        if (observer is QuotaNoticeObserver notice) { notice.Named = id => Repl.ServiceNames.Display(snapshot, id); }
        return observer;
    }

    /// <summary>
    /// Perche' il turno non ha risposto, come lo dice il REPL (24 settembre 2026): prima `pa run` scriveva solo
    /// «Esito: denied (NO_ADMISSIBLE_TARGET)», e per sapere quale regola avesse escluso quale servizio bisognava aprire il
    /// REPL o leggere il JSON. Un rifiuto porta i suoi numeri; un guasto la sua frase.
    /// </summary>
    private static void Explain(TextWriter error, DenialDetail? denial, string? code, ConfigurationSnapshot snapshot)
    {
        if (denial is { Services.Count: > 0 })
        {
            Repl.ReplDenialView.Write(new Repl.PlainReplRenderer(error, error), denial, verbose: false, id => Repl.ServiceNames.Display(snapshot, id), interactive: false, quotaOf: Output.QuotaText.StateOf(snapshot.DataDirectory));
        }
        else if (code is not null && Ui.Contains("fail." + code))
        {
            error.WriteLine(Ui.Text("fail." + code));
        }
    }

    private static int ExitCodeFor(ExecutionResult result) => result.State switch
    {
        ExecutionState.Completed => ExitCodes.Success,
        ExecutionState.Denied => ExitCodes.PolicyDenied,
        ExecutionState.Cancelled => ExitCodes.Cancelled,
        ExecutionState.Uncertain => ExitCodes.Uncertain,
        _ => result.ErrorCode is ErrorCode code ? ExitCodes.ForStorage(code) : ExitCodes.TransportError,
    };

    /// <summary>
    /// Il prompt arriva esclusivamente da standard input: un argomento di riga di comando
    /// resterebbe nella cronologia della shell.
    /// </summary>
    private static async Task<string> ReadPromptAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        _ = parseResult;
        return await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applica la scelta sul colore (S64). Falso se il valore non e' riconosciuto.
    ///
    /// <c>auto</c> rimette la decisione a terminale e <c>NO_COLOR</c>, che restano il caso normale;
    /// <c>always</c> serve a chi cattura l'uscita e le sequenze le vuole, <c>never</c> a chi ha un
    /// terminale e non le vuole.
    /// </summary>
    private static bool TryUseColor(string? choice)
    {
        switch ((choice ?? "auto").Trim().ToLowerInvariant())
        {
            case "auto":
                Surfaces.UseColor(null);
                return true;

            case "always":
                Surfaces.UseColor(true);
                return true;

            case "never":
                Surfaces.UseColor(false);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Osservatore degli eventi del turno (S64). Falso se il formato non e' riconosciuto.
    ///
    /// Le righe vanno su standard error: con <c>--json</c> lo standard output contiene esattamente
    /// un documento, e mescolarvi eventi lo renderebbe illeggibile a chi lo analizza.
    /// </summary>
    private static bool TryObserver(string? choice, TextWriter error, out ITurnObserver? observer)
    {
        observer = null;

        string value = (choice ?? string.Empty).Trim().ToLowerInvariant();

        if (value.Length == 0)
        {
            return true;
        }

        if (value is not "jsonl")
        {
            return false;
        }

        observer = new JsonlTurnObserver(error);

        return true;
    }

    /// <summary>
    /// Scrive la risposta finale su file (S64).
    ///
    /// E' il testo **gia' passato dallo screening**, senza avanzamento e senza decorazione: cio' che
    /// sarebbe comparso a schermo. Un turno senza risposta — negato, cancellato, fallito — scrive un
    /// file vuoto invece di lasciare quello di prima: un file rimasto da una corsa precedente
    /// verrebbe letto come la risposta di questa.
    /// </summary>
    private static void WriteLastMessage(FileInfo? file, string? text)
    {
        if (file is null)
        {
            return;
        }

        File.WriteAllText(file.FullName, text ?? string.Empty);
    }

    /// <summary>
    /// Avanzamento su standard error: non deve mai mescolarsi al JSON di stdout.
    ///
    /// Resta anche con <c>--json</c>, e non e' una svista. Lo standard output e' gia' il canale dello
    /// script — un documento solo, nient'altro — mentre lo standard error e' quello di chi guarda:
    /// toglierlo non avrebbe reso il JSON piu' pulito, avrebbe solo tolto l'avanzamento a una persona
    /// che lo stava leggendo. In S64 era stato tolto per un momento, e tre prove esistenti hanno
    /// ricordato che qualcuno lo verifica.
    ///
    /// Cio' che il gate di S64 chiede — che un consumatore automatico non debba leggere italiano —
    /// lo da' <c>--events jsonl</c>, che porta gli stessi fatti con nomi stabili sullo stesso canale.
    /// </summary>
    private static void Progress(TextWriter error, bool json, string message)
    {
        _ = json;

        // Con `--events jsonl` lo standard error e' un flusso di oggetti JSON, uno per riga (CLI-TUI): una riga di testo
        // in mezzo rompe chi lo legge (23 settembre 2026). Gli stessi fatti viaggiano come eventi.
        if (EventsOnError.Value) { return; }

        error.WriteLine(message);
    }

    /// <summary>Vero mentre il comando scrive eventi JSONL sullo standard error.</summary>
    private static readonly AsyncLocal<bool> EventsOnError = new();

    private static int Report(
        TextWriter output,
        TextWriter error,
        bool json,
        string command,
        ErrorCode code,
        string message,
        int exitCode)
    {
        // I messaggi delle operazioni nascono in inglese: si traducono qui, dove si scrivono per una persona.
        message = Ui.Contains(message) ? Ui.Text(message) : message;

        if (json)
        {
            CliJson.Write(output, CliJson.Error(command, code, message));
            return exitCode;
        }

        error.WriteLine(code.ToWire() + ": " + message);
        return exitCode;
    }
}

/// <summary>Osservatore di `pa run` senza eventi: scrive solo l'avviso di un servizio messo in pausa per quota finita.</summary>
internal sealed class QuotaNoticeObserver(TextWriter error) : ITurnObserver
{
    /// <summary>Nome di un servizio per una persona; lo fissa il comando quando ha letto la configurazione.</summary>
    internal Func<string, string>? Named { get; set; }

    /// <inheritdoc />
    public void Observe(TurnEvent turnEvent)
    {
        if (turnEvent is TurnPhaseEnded { Phase: "Quota" } ended)
        {
            IReadOnlyDictionary<string, string> quota = Repl.ReplTurnObserver.Values(ended.Outcome);
            string service = quota.GetValueOrDefault("service", "?");
            error.WriteLine(Output.QuotaText.Paused(Named?.Invoke(service) ?? service, Output.QuotaText.Until(quota.GetValueOrDefault("until"))));
        }
    }
}
