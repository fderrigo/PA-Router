using System.CommandLine;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Output;
using PA.Router.Cli.Repl;
using PA.Router.Core;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Storage;
using PA.Router.Registry.Loading;
using PA.Router.Runtime.Ipc;
using PA.Router.Session;

namespace PA.Router.Cli.Commands;

/// <summary>
/// <c>pa session list | show | recover | delete | capsule</c>.
///
/// <c>recover</c> chiude un'incertezza amministrativa e nient'altro: non riavvia il lavoro, non lo
/// dichiara completato e non crea una nuova esecuzione. Continuare richiede un run esplicito, con
/// un nuovo identificatore (03-specs/SESSION-HANDOVER.md).
/// </summary>
internal static class SessionCommand
{
    internal static Command Create(Option<DirectoryInfo?> dataOption)
    {
        Command command = new("session", Ui.Text("Inspect and administer local sessions."))
        {
            List(dataOption),
            Show(dataOption),
            Resume(dataOption),
            Name(dataOption),
            Archive(dataOption),
            Fork(dataOption),
            Transcript(dataOption),
            Handover(dataOption),
            Recover(dataOption),
            Delete(dataOption),
            Capsule(dataOption),
        };

        command.SetAction(parseResult =>
        {
            parseResult.InvocationConfiguration.Output.WriteLine(Ui.Text("Run 'pa session --help' for the available subcommands."));
            return ExitCodes.UsageError;
        });

        return command;
    }

    /// <summary>
    /// <c>pa session resume</c>: riapre il REPL su una sessione esistente, cosi' la conversazione
    /// riprende con la propria cronologia invece di ricominciare da zero.
    ///
    /// Solo con un terminale: in automazione la ripresa e' <c>run --session</c>, perche' un invito
    /// interattivo dentro uno script resterebbe appeso su nessuno.
    /// </summary>
    private static Command Resume(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("id")
        {
            Description = Ui.Text("Session identifier, name or list position; omit to choose from the list."),
            DefaultValueFactory = _ => string.Empty,
        };

        Command command = new("resume", Ui.Text("Reopen the REPL on an existing session.")) { dataOption, idArgument };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            TextWriter error = parseResult.InvocationConfiguration.Error;
            TextWriter output = parseResult.InvocationConfiguration.Output;

            if (!PaRootCommand.Interactive)
            {
                error.WriteLine(Ui.Text("La ripresa apre il REPL e richiede un terminale: in automazione usa 'pa run --session'."));
                return ExitCodes.UsageError;
            }

            string dataDirectory = CommandContext.ResolveDataDirectory(parseResult, dataOption);
            ConfigurationLoadResult configuration = new ConfigurationLoader(SystemClock.Instance).Load(dataDirectory);

            if (!configuration.IsValid)
            {
                return CommandContext.ReportIssues(parseResult, configuration.Issues);
            }

            ConfigurationSnapshot snapshot = configuration.Require();
            string requested = (parseResult.GetValue(idArgument) ?? string.Empty).Trim();
            string sessionId;

            try
            {
                await using SessionGateway gateway = await SessionGateway
                    .OpenAsync(snapshot, writable: false, cancellationToken)
                    .ConfigureAwait(false);

                SessionListPayload list = await gateway
                    .ListAsync(includeArchived: false, cancellationToken)
                    .ConfigureAwait(false);

                string? answer = requested;

                if (answer.Length == 0)
                {
                    if (list.Sessions.Count == 0)
                    {
                        error.WriteLine(Ui.Text("Nessuna conversazione da riprendere."));
                        return ExitCodes.InvalidInput;
                    }

                    Render(parseResult, list, gateway.Snapshot);
                    output.WriteLine(Ui.Text("Quale riprendi? (numero, nome o identificatore; vuoto annulla)"));

                    answer = Console.In.ReadLine();
                }

                // La regola di scelta e' quella del REPL: e' cio' che rende la ripresa dal selettore
                // identica alla ripresa per identificatore, che e' il gate di questo step.
                SessionChoice choice = SessionPicker.Resolve(answer, list.Sessions);

                // Archiviare toglie dalla proposta, non dalla disponibilita': un identificatore o un
                // nome la raggiungono comunque. Un numero no: indica una riga dell'elenco mostrato.
                if (choice.SessionId is null && answer is { Length: > 0 } && !SessionPicker.IsPosition(answer))
                {
                    SessionListPayload all = await gateway
                        .ListAsync(includeArchived: true, cancellationToken)
                        .ConfigureAwait(false);

                    choice = SessionPicker.Resolve(answer, all.Sessions);
                }

                if (choice.SessionId is not string chosen)
                {
                    if (choice.Problem is null)
                    {
                        output.WriteLine(Ui.Text("Ripresa annullata."));
                        return ExitCodes.Success;
                    }

                    // L'esistenza si verifica prima di aprire il REPL: riprendere una sessione che
                    // non c'e' ne creerebbe una nuova con quell'identificatore, che non e' una
                    // ripresa.
                    error.WriteLine(requested.Length > 0 ? Ui.Text("Sessione sconosciuta: ") + requested : choice.Problem);

                    return ExitCodes.InvalidInput;
                }

                sessionId = chosen;
            }
            catch (PaRouterException exception)
            {
                return Fail(parseResult, json: false, "session resume", exception.Code, exception.Message,
                    ExitCodes.ForStorage(exception.Code));
            }

            return await ReplSession
                .RunAsync(parseResult, snapshot, sessionId, cancellationToken)
                .ConfigureAwait(false);
        });

        return command;
    }

    /// <summary>
    /// <c>pa session list</c>: le conversazioni con nome, turni e stato (S62).
    ///
    /// Fino alla S61 stampava identificatori e basta: un elenco di GUID fra cui nessuno puo'
    /// riconoscere la conversazione di ieri. E' la stessa lettura del selettore del REPL, quindi i
    /// due elenchi non possono divergere.
    /// </summary>
    private static Command List(Option<DirectoryInfo?> dataOption)
    {
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };
        Option<bool> allOption = new("--all") { Description = Ui.Text("Include the archived sessions.") };

        Command command = new("list", Ui.Text("List the local sessions.")) { dataOption, jsonOption, allOption };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);

            return await WithGatewayAsync(parseResult, dataOption, json, "session list", writable: false, async gateway =>
            {
                SessionListPayload list = await gateway
                    .ListAsync(parseResult.GetValue(allOption), cancellationToken)
                    .ConfigureAwait(false);

                if (json)
                {
                    CliJson.Write(parseResult.InvocationConfiguration.Output, CliJson.Ok("session list", new JsonObject
                    {
                        ["sessions"] = new JsonArray([.. list.Sessions.Select(session => (JsonNode)new JsonObject
                        {
                            ["id"] = session.Id,
                            ["name"] = session.DisplayName,
                            ["profileId"] = session.ProfileId,
                            ["channel"] = session.Channel,
                            ["contentMode"] = session.ContentMode,
                            ["turns"] = session.Turns,
                            ["archived"] = session.Archived,
                            ["createdAt"] = session.CreatedAt.ToString("O", Formats.Invariant),
                        })]),
                        ["persistsHistory"] = list.PersistsHistory,
                    }));

                    return ExitCodes.Success;
                }

                Render(parseResult, list, gateway.Snapshot);

                return ExitCodes.Success;
            }, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    /// <summary>
    /// <c>pa session name &lt;id&gt; [nome]</c>: un nome leggibile, senza nome lo toglie.
    ///
    /// Il nome lo sceglie l'operatore e non viene dedotto dalla prima domanda: quella e' contenuto
    /// della conversazione, e finirebbe in un elenco che chiunque guardi lo schermo puo' leggere.
    /// </summary>
    private static Command Name(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("session-id") { Description = Ui.Text("Session to name.") };
        Argument<string> nameArgument = new("name") { Description = Ui.Text("Readable name; omit to remove it."), DefaultValueFactory = _ => string.Empty };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("name", Ui.Text("Give a session a readable name, or remove it."))
        {
            dataOption,
            idArgument,
            nameArgument,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);

            return await WithGatewayAsync(parseResult, dataOption, json, "session name", writable: true, async gateway =>
            {
                string id = await ReferenceAsync(gateway, parseResult.GetValue(idArgument)!, cancellationToken).ConfigureAwait(false);
                string requested = parseResult.GetValue(nameArgument) ?? string.Empty;
                string? name = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();

                await gateway.NameAsync(id, name, cancellationToken).ConfigureAwait(false);

                if (json)
                {
                    CliJson.Write(parseResult.InvocationConfiguration.Output, CliJson.Ok("session name", new JsonObject
                    {
                        ["id"] = id,
                        ["name"] = name,
                    }));

                    return ExitCodes.Success;
                }

                parseResult.InvocationConfiguration.Output.WriteLine(
                    name is null ? Ui.Text("Nome rimosso da ") + id + "." : Ui.Text("Nome assegnato a ") + id + ": " + name);

                return ExitCodes.Success;
            }, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    /// <summary>
    /// <c>pa session archive &lt;id&gt;</c>: toglie dall'elenco senza cancellare nulla.
    ///
    /// E' il confine che conta: archiviare lascia turni, contenuti ed esecuzioni dove sono e la
    /// conversazione resta riprendibile; cancellare e' <c>pa session delete</c>, che rimuove i
    /// contenuti e revoca la capability. Una parola sola per le due cose sarebbe una trappola.
    /// </summary>
    private static Command Archive(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("session-id") { Description = Ui.Text("Session to archive.") };
        Option<bool> restoreOption = new("--restore") { Description = Ui.Text("Bring the session back into the list.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("archive", Ui.Text("Hide a session from the list without deleting anything."))
        {
            dataOption,
            idArgument,
            restoreOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            bool restore = parseResult.GetValue(restoreOption);

            return await WithGatewayAsync(parseResult, dataOption, json, "session archive", writable: true, async gateway =>
            {
                string id = await ReferenceAsync(gateway, parseResult.GetValue(idArgument)!, cancellationToken).ConfigureAwait(false);

                await gateway.ArchiveAsync(id, !restore, cancellationToken).ConfigureAwait(false);

                if (json)
                {
                    CliJson.Write(parseResult.InvocationConfiguration.Output, CliJson.Ok("session archive", new JsonObject
                    {
                        ["id"] = id,
                        ["archived"] = !restore,
                        // Archiviare non cancella: e' la differenza con `session delete`.
                        ["contentsRemoved"] = false,
                    }));

                    return ExitCodes.Success;
                }

                parseResult.InvocationConfiguration.Output.WriteLine(
                    restore
                        ? Ui.Text("Riportata in elenco: ") + id
                        : Ui.Text("Archiviata: ") + id + Ui.Text(". Nulla e' stato cancellato; ricompare con 'pa session list --all'."));

                return ExitCodes.Success;
            }, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    /// <summary>
    /// <c>pa session fork &lt;id&gt;</c>: una conversazione nuova che parte dalla cronologia di quella
    /// indicata, senza toccarla.
    ///
    /// Non e' un handover: nessuna capsule, nessun altro servizio, nessun turno eseguito e nessuna
    /// quota consumata. Sono gli stessi contenuti gia' conservati da questo router.
    /// </summary>
    private static Command Fork(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("session-id") { Description = Ui.Text("Session the fork starts from.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("fork", Ui.Text("Start a new session from the history of an existing one."))
        {
            dataOption,
            idArgument,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);

            return await WithGatewayAsync(parseResult, dataOption, json, "session fork", writable: true, async gateway =>
            {
                string id = await ReferenceAsync(gateway, parseResult.GetValue(idArgument)!, cancellationToken).ConfigureAwait(false);

                SessionAckPayload fork = await gateway.ForkAsync(id, cancellationToken).ConfigureAwait(false);

                if (json)
                {
                    CliJson.Write(parseResult.InvocationConfiguration.Output, CliJson.Ok("session fork", new JsonObject
                    {
                        ["sourceSessionId"] = id,
                        ["sessionId"] = fork.SessionId,
                        ["sourceModified"] = false,
                    }));

                    return ExitCodes.Success;
                }

                parseResult.InvocationConfiguration.Output.WriteLine(Ui.Text("Sessione nuova: ") + fork.SessionId);
                parseResult.InvocationConfiguration.Output.WriteLine(Ui.Text("L'originale (") + id + Ui.Text(") non e' stato toccato."));

                return ExitCodes.Success;
            }, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    /// <summary>
    /// <c>pa session transcript &lt;id&gt;</c>: i turni conservati, senza interrogare nessun servizio.
    /// </summary>
    private static Command Transcript(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("session-id") { Description = Ui.Text("Session to read.") };
        Option<int> turnsOption = new("--turns") { Description = Ui.Text("How many of the last turns to show.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("transcript", Ui.Text("Show the stored turns of a session."))
        {
            dataOption,
            idArgument,
            turnsOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);

            return await WithGatewayAsync(parseResult, dataOption, json, "session transcript", writable: false, async gateway =>
            {
                string id = await ReferenceAsync(gateway, parseResult.GetValue(idArgument)!, cancellationToken).ConfigureAwait(false);

                SessionTranscriptPayload transcript = await gateway
                    .TranscriptAsync(id, parseResult.GetValue(turnsOption), cancellationToken)
                    .ConfigureAwait(false);

                if (json)
                {
                    CliJson.Write(parseResult.InvocationConfiguration.Output, CliJson.Ok("session transcript", new JsonObject
                    {
                        ["id"] = id,
                        ["total"] = transcript.Total,
                        ["turns"] = new JsonArray([.. transcript.Turns.Select(turn => (JsonNode)new JsonObject
                        {
                            ["ordinal"] = turn.Ordinal,
                            [Ui.Text("input")] = turn.Input,
                            ["output"] = turn.Output,
                            ["createdAt"] = turn.CreatedAt.ToString("O", Formats.Invariant),
                        })]),
                    }));

                    return ExitCodes.Success;
                }

                IOutputSurface surface = Surface(parseResult);

                if (transcript.Turns.Count == 0)
                {
                    surface.Line(Ui.Text("Nessun turno conservato in questa conversazione."));

                    return ExitCodes.Success;
                }

                foreach (SessionTurnPayload turn in transcript.Turns)
                {
                    surface.Rule(Ui.Text("turno ") + turn.Ordinal.ToString(Formats.Invariant));

                    // Contenuto della conversazione: reso senza interpretarlo, come ogni testo che
                    // il router non ha generato.
                    surface.Untrusted(turn.Input);

                    if (turn.Output is string reply)
                    {
                        surface.Blank();
                        surface.Untrusted(reply);
                    }
                }

                if (transcript.Turns.Count < transcript.Total)
                {
                    surface.Blank();
                    surface.Line(
                        Ui.Text("Mostrati gli ultimi ") + transcript.Turns.Count.ToString(Formats.Invariant)
                        + Ui.Text(" di ") + transcript.Total.ToString(Formats.Invariant) + Ui.Text(" turni."));
                }

                return ExitCodes.Success;
            }, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    /// <summary>Elenco delle conversazioni, nella forma condivisa con il selettore del REPL.</summary>
    private static void Render(ParseResult parseResult, SessionListPayload list, ConfigurationSnapshot? snapshot)
    {
        IOutputSurface surface = Surface(parseResult);

        if (list.Sessions.Count == 0)
        {
            surface.Line(Ui.Text("Nessuna conversazione conservata."));

            return;
        }

        (IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows) = SessionPicker.Table(list.Sessions, snapshot);
        surface.Table(headers, rows);

        if (!list.PersistsHistory)
        {
            surface.Warn(Ui.Text("Conservazione contenuti disattivata: dopo un riavvio le sessioni non hanno contesto."));
        }
    }

    private static IOutputSurface Surface(ParseResult parseResult) =>
        Surfaces.For(
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            Surfaces.CanDecorate);

    /// <summary>
    /// Apre il trasporto delle sessioni e ci esegue il comando.
    ///
    /// <paramref name="writable"/> distingue chi legge da chi scrive: senza worker, un elenco non
    /// deve occupare la directory dati e una scrittura deve possederla.
    /// </summary>
    private static async Task<int> WithGatewayAsync(
        ParseResult parseResult,
        Option<DirectoryInfo?> dataOption,
        bool json,
        string command,
        bool writable,
        Func<SessionGateway, Task<int>> body,
        CancellationToken cancellationToken)
    {
        TextWriter output = parseResult.InvocationConfiguration.Output;

        try
        {
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

            await using SessionGateway gateway = await SessionGateway
                .OpenAsync(configuration.Require(), writable, cancellationToken)
                .ConfigureAwait(false);

            return await body(gateway).ConfigureAwait(false);
        }
        catch (PaRouterException exception)
        {
            return Fail(parseResult, json, command, exception.Code, exception.Message, ExitCodes.ForStorage(exception.Code));
        }
    }

    private static Command Show(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("session-id") { Description = Ui.Text("Session to inspect.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("show", Ui.Text("Show the state of a session.")) { dataOption, idArgument, jsonOption };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);

            return await WithSessionsAsync(parseResult, dataOption, json, "session show", async (service, stack) =>
            {
                string id = Reference(service, parseResult.GetValue(idArgument)!);
                SessionSummary? summary = await service.ReadAsync(id, cancellationToken).ConfigureAwait(false);

                if (summary is null)
                {
                    return Fail(parseResult, json, "session show", ErrorCode.InputInvalid,
                        Ui.Text("That session does not exist."), ExitCodes.InvalidInput);
                }

                if (json)
                {
                    CliJson.Write(parseResult.InvocationConfiguration.Output, CliJson.Ok("session show", new JsonObject
                    {
                        ["id"] = summary.SessionId,
                        ["profileId"] = summary.ProfileId,
                        ["channel"] = summary.Channel.ToWire(),
                        ["contentMode"] = summary.ContentMode.ToWire(),
                        ["turns"] = summary.Turns,
                        ["persistsHistory"] = service.PersistsHistory,
                        ["contextAvailability"] = service.PersistsHistory
                            ? ContentAvailability.Available.ToWire()
                            : ContentAvailability.Unavailable.ToWire(),
                        ["uncertainExecutions"] = new JsonArray([.. summary.UncertainExecutions.Select(value => (JsonNode)value)]),
                        ["createdAt"] = summary.CreatedAt.ToString("O", Formats.Invariant),
                        ["expiresAt"] = summary.ExpiresAt.ToString("O", Formats.Invariant),
                    }));

                    return ExitCodes.Success;
                }

                TextWriter output = parseResult.InvocationConfiguration.Output;

                output.WriteLine(Ui.Text("Sessione:  ") + summary.SessionId);
                output.WriteLine(Ui.Text("Profilo:   ") + Repl.ProfilePresentation.Name(stack.Snapshot, summary.ProfileId));
                output.WriteLine(Ui.Text("Contenuti: ") + (service.PersistsHistory ? Ui.Text("show.kept") : Ui.Text("show.notKept")));
                output.WriteLine(Ui.Text("Turni:     ") + summary.Turns.ToString(Formats.Invariant));

                foreach (string execution in summary.UncertainExecutions)
                {
                    output.WriteLine(Ui.Text("Incerta:   ") + execution + Ui.Text(" — richiede 'pa session recover'"));
                }

                return ExitCodes.Success;
            }, readOnly: true).ConfigureAwait(false);
        });

        return command;
    }

    private static Command Recover(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("execution-id") { Description = Ui.Text("Uncertain execution to resolve.") };
        Option<string?> resolutionOption = new("--resolution") { Description = Ui.Text("Administrative resolution, e.g. inspected.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("recover", Ui.Text("Record that an uncertain execution was inspected by the operator."))
        {
            dataOption,
            idArgument,
            resolutionOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            string? resolution = parseResult.GetValue(resolutionOption);

            if (resolution is not "inspected")
            {
                // La risoluzione e' una dichiarazione dell'operatore, non un valore di comodo.
                return Fail(parseResult, json, "session recover", ErrorCode.InputInvalid,
                    Ui.Text("Pass --resolution inspected after checking diff and processes."), ExitCodes.UsageError);
            }

            return await WithSessionsAsync(parseResult, dataOption, json, "session recover", async (service, _) =>
            {
                string executionId = parseResult.GetValue(idArgument)!;
                bool resolved = await service.ResolveUncertainAsync(executionId, resolution, cancellationToken).ConfigureAwait(false);

                if (!resolved)
                {
                    return Fail(parseResult, json, "session recover", ErrorCode.InputInvalid,
                        Ui.Text("That execution is not in the uncertain state."), ExitCodes.InvalidInput);
                }

                if (json)
                {
                    CliJson.Write(parseResult.InvocationConfiguration.Output, CliJson.Ok("session recover", new JsonObject
                    {
                        ["executionId"] = executionId,
                        ["resolution"] = resolution,
                        // La risoluzione chiude l'incertezza, non riprende il lavoro.
                        ["workResumed"] = false,
                        ["workCompleted"] = false,
                    }));

                    return ExitCodes.Success;
                }

                parseResult.InvocationConfiguration.Output.WriteLine(
                    Ui.Text("Incertezza chiusa per ") + executionId + Ui.Text(". Il lavoro precedente non e' ripreso ne' completato: ")
                    + Ui.Text("per continuare serve un nuovo 'pa run'."));

                return ExitCodes.Success;
            }).ConfigureAwait(false);
        });

        return command;
    }

    private static Command Delete(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("session-id") { Description = Ui.Text("Session whose contents are removed.") };
        Option<bool> confirmOption = new("--yes") { Description = Ui.Text("Confirm the removal without prompting.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("delete", Ui.Text("Delete the stored contents of a session."))
        {
            dataOption,
            idArgument,
            confirmOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);

            if (!parseResult.GetValue(confirmOption))
            {
                return Fail(parseResult, json, "session delete", ErrorCode.InputInvalid,
                    Ui.Text("Deleting session contents is irreversible: confirm with --yes."), ExitCodes.UsageError);
            }

            return await WithSessionsAsync(parseResult, dataOption, json, "session delete", async (service, stack) =>
            {
                string id = Reference(service, parseResult.GetValue(idArgument)!);

                // Una sessione che non c'e' non si «cancella con zero contenuti»: si dice che non c'e'.
                if (await service.ReadAsync(id, cancellationToken).ConfigureAwait(false) is null)
                {
                    return Fail(parseResult, json, "session delete", ErrorCode.InputInvalid,
                        Ui.Text("That session does not exist."), ExitCodes.InvalidInput);
                }

                // I transcript che un agente ha tenuto per conto proprio stanno fuori dallo store, in chiaro: si tolgono
                // **prima**, finche' i turni dicono ancora quali sono (ADR-022).
                (int nativeRemoved, int nativeLeft) = NativeTranscripts.Remove(
                    await service.ReadTurnsAsync(id, cancellationToken).ConfigureAwait(false));

                SessionDeletionResult deleted = await SessionDeletion.DeleteAsync(stack, service, id, cancellationToken).ConfigureAwait(false);
                int removed = deleted.ContentsRemoved;

                if (json)
                {
                    CliJson.Write(parseResult.InvocationConfiguration.Output, CliJson.Ok("session delete", new JsonObject
                    {
                        ["id"] = id,
                        ["blobsRemoved"] = removed,
                        ["memoriesRemoved"] = deleted.MemoriesRemoved,
                        ["nativeTranscriptsRemoved"] = nativeRemoved,
                        ["nativeTranscriptsLeft"] = nativeLeft,
                        // I metadati restano per l'audit; il repository dell'utente non viene toccato.
                        ["auditRetained"] = true,
                        ["repositoryTouched"] = false,
                    }));

                    return ExitCodes.Success;
                }

                parseResult.InvocationConfiguration.Output.WriteLine(
                    Ui.Text("Contenuti rimossi: ") + removed.ToString(Formats.Invariant)
                    + Ui.Text(". I metadati restano per l'audit; il repository non e' stato toccato."));

                if (deleted.MemoriesRemoved > 0)
                {
                    parseResult.InvocationConfiguration.Output.WriteLine(Ui.Format("session.memoriesRemoved {0}", deleted.MemoriesRemoved));
                }

                if (nativeRemoved > 0)
                {
                    parseResult.InvocationConfiguration.Output.WriteLine(Ui.Format("session.nativeRemoved {0}", nativeRemoved));
                }

                if (nativeLeft > 0)
                {
                    // Un contenuto che si voleva togliere ed e' rimasto si dice, con il posto dove sta.
                    parseResult.InvocationConfiguration.Error.WriteLine(
                        Ui.Format("session.nativeLeft {0} {1}", nativeLeft, NativeTranscripts.ClaudeCodeRoot));
                }

                return ExitCodes.Success;
            }).ConfigureAwait(false);
        });

        return command;
    }

    /// <summary>
    /// <c>pa session handover</c>: consegna il lavoro a un altro servizio con una capsule.
    ///
    /// La resa sta qui, l'operazione sta in <c>SessionHandover</c>: il <c>/handover</c> del REPL
    /// invoca la stessa, e non ne esiste una seconda (INV-22).
    /// </summary>
    private static Command Handover(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("session-id") { Description = Ui.Text("Session the work comes from.") };
        Option<string?> targetOption = new("--target") { Description = Ui.Text("Service that must continue the work.") };
        Option<string?> workspaceOption = new("--workspace") { Description = Ui.Text("Workspace root, a Git repository.") };
        Option<bool> stdinOption = new("--stdin") { Description = Ui.Text("Read the objective from standard input.") };
        Option<string?> executionOption = new("--execution") { Description = Ui.Text("Source execution; must be terminal.") };
        Option<string[]> constraintOption = new("--constraint") { Description = Ui.Text("Constraint that stays valid; repeatable."), AllowMultipleArgumentsPerToken = true };
        Option<string[]> decisionOption = new("--decision") { Description = Ui.Text("Decision taken; repeatable."), AllowMultipleArgumentsPerToken = true };
        Option<string[]> openWorkOption = new("--open") { Description = Ui.Text("Open work item; repeatable."), AllowMultipleArgumentsPerToken = true };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("handover", Ui.Text("Hand the work of a session to another service, through a capsule."))
        {
            dataOption,
            idArgument,
            targetOption,
            workspaceOption,
            stdinOption,
            executionOption,
            constraintOption,
            decisionOption,
            openWorkOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;

            if (parseResult.GetValue(targetOption) is not string target)
            {
                return Fail(parseResult, json, "session handover", ErrorCode.InputInvalid,
                    Ui.Text("The destination service is required: pass --target."), ExitCodes.UsageError);
            }

            if (parseResult.GetValue(workspaceOption) is not string workspace)
            {
                return Fail(parseResult, json, "session handover", ErrorCode.InputInvalid,
                    Ui.Text("A capsule needs the workspace it describes: pass --workspace."), ExitCodes.UsageError);
            }

            if (!parseResult.GetValue(stdinOption))
            {
                return Fail(parseResult, json, "session handover", ErrorCode.InputInvalid,
                    Ui.Text("The objective must come from standard input: pass --stdin."), ExitCodes.UsageError);
            }

            string objective = (await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();

            string dataDirectory = CommandContext.ResolveDataDirectory(parseResult, dataOption);
            ConfigurationLoadResult configuration = new ConfigurationLoader(SystemClock.Instance).Load(dataDirectory);

            if (!configuration.IsValid)
            {
                // Con `--json` anche un rifiuto e' un documento: vale qui come in `config show`.
                if (json)
                {
                    CliJson.Write(output, CliJson.Invalid("session handover", configuration.Issues));
                    return ExitCodes.InvalidInput;
                }

                return CommandContext.ReportIssues(parseResult, configuration.Issues);
            }

            ConfigurationSnapshot snapshot = configuration.Require();

            try
            {
                await using RouterStack runtime = RouterStack.Open(snapshot);

                HandoverOutcome outcome = await SessionHandover.RunAsync(
                    runtime,
                    snapshot.DefaultProfile,
                    new HandoverRequest(
                        parseResult.GetValue(idArgument)!,
                        target,
                        objective,
                        workspace,
                        parseResult.GetValue(constraintOption),
                        parseResult.GetValue(decisionOption),
                        parseResult.GetValue(openWorkOption),
                        parseResult.GetValue(executionOption)),
                    cancellationToken).ConfigureAwait(false);

                return Present(parseResult, json, output, outcome);
            }
            catch (PaRouterException ex)
            {
                return Fail(parseResult, json, "session handover", ex.Code, ex.Message,
                    ExitCodes.ForStorage(ex.Code));
            }
        });

        return command;
    }

    /// <summary>
    /// Resa dell'esito di un handover. Dice «continuita' da capsule», mai «ripresa identica»: la
    /// destinazione riceve una sessione nuova e non ha mai visto la conversazione di prima.
    /// </summary>
    private static int Present(ParseResult parseResult, bool json, TextWriter output, HandoverOutcome outcome)
    {
        if (outcome.Refusal is ErrorCode refusal)
        {
            return Fail(parseResult, json, "session handover", refusal, outcome.RefusalDetail ?? string.Empty,
                refusal is ErrorCode.SideEffectUncertain ? ExitCodes.Uncertain : ExitCodes.InvalidInput);
        }

        ExecutionResult result = outcome.Turn!.Result!;

        if (json)
        {
            CliJson.Write(output, CliJson.Ok("session handover", new JsonObject
            {
                ["destinationSessionId"] = outcome.DestinationSessionId,
                ["sourceExecutionId"] = outcome.SourceExecutionId,
                ["capsuleDigest"] = outcome.CapsuleDigest,
                ["executionId"] = result.ExecutionId,
                ["state"] = result.State.ToWire(),
                ["errorCode"] = result.ErrorCode?.ToWire(),
                ["continuity"] = "capsule",
            }));

            return result.State is ExecutionState.Completed ? ExitCodes.Success : ExitCodes.PolicyDenied;
        }

        output.WriteLine(Ui.Text("Continuita' da capsule, non ripresa identica."));
        output.WriteLine(Ui.Text("  sessione nuova:  ") + outcome.DestinationSessionId);
        output.WriteLine(Ui.Text("  da esecuzione:   ") + outcome.SourceExecutionId);
        output.WriteLine(Ui.Text("  capsule digest:  ") + outcome.CapsuleDigest);
        output.WriteLine(Ui.Text("  esito:           ") + result.State.ToWire());

        if (result.Output is string text)
        {
            output.WriteLine();
            output.WriteLine(text);
        }

        return result.State is ExecutionState.Completed ? ExitCodes.Success : ExitCodes.PolicyDenied;
    }

    private static Command Capsule(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("session-id") { Description = Ui.Text("Session the capsule comes from.") };
        Option<string?> executionOption = new("--execution") { Description = Ui.Text("Source execution; must be terminal.") };
        Option<string?> workspaceOption = new("--workspace") { Description = Ui.Text("Workspace root, a Git repository.") };
        Option<bool> stdinOption = new("--stdin") { Description = Ui.Text("Read the objective from standard input.") };
        Option<string[]> constraintOption = new("--constraint") { Description = Ui.Text("Constraint that stays valid; repeatable."), AllowMultipleArgumentsPerToken = true };
        Option<string[]> decisionOption = new("--decision") { Description = Ui.Text("Decision taken; repeatable."), AllowMultipleArgumentsPerToken = true };
        Option<string[]> openWorkOption = new("--open") { Description = Ui.Text("Open work item; repeatable."), AllowMultipleArgumentsPerToken = true };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("capsule", Ui.Text("Build the deterministic handover capsule of a session."))
        {
            dataOption,
            idArgument,
            executionOption,
            workspaceOption,
            stdinOption,
            constraintOption,
            decisionOption,
            openWorkOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;

            if (!parseResult.GetValue(stdinOption))
            {
                return Fail(parseResult, json, "session capsule", ErrorCode.InputInvalid,
                    Ui.Text("The objective must come from standard input: pass --stdin."), ExitCodes.UsageError);
            }

            string objective = (await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();

            if (objective.Length == 0)
            {
                return Fail(parseResult, json, "session capsule", ErrorCode.InputInvalid,
                    Ui.Text("The objective is empty."), ExitCodes.InvalidInput);
            }

            string? workspace = parseResult.GetValue(workspaceOption);

            if (workspace is null)
            {
                return Fail(parseResult, json, "session capsule", ErrorCode.InputInvalid,
                    Ui.Text("A capsule needs the workspace it describes: pass --workspace."), ExitCodes.UsageError);
            }

            return await WithSessionsAsync(parseResult, dataOption, json, "session capsule", async (service, runtime) =>
            {
                string sessionId = Reference(service, parseResult.GetValue(idArgument)!);
                SessionSummary? summary = await service.ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);

                if (summary is null)
                {
                    return Fail(parseResult, json, "session capsule", ErrorCode.InputInvalid,
                        Ui.Text("That session does not exist."), ExitCodes.InvalidInput);
                }

                if (summary.UncertainExecutions.Count > 0)
                {
                    // Un turno con effetti incerti va risolto prima: una capsule costruita adesso
                    // descriverebbe un workspace di cui nessuno sa lo stato.
                    return Fail(parseResult, json, "session capsule", ErrorCode.SideEffectUncertain,
                        Ui.Text("The session has uncertain executions: run 'pa session recover' first."), ExitCodes.Uncertain);
                }

                WorkspaceCheckpoint checkpoint = await WorkspaceCheckpointReader
                    .ReadAsync(workspace, runtime.Digest, cancellationToken)
                    .ConfigureAwait(false);

                IReadOnlyList<SessionTurn> turns = await service.ReadTurnsAsync(sessionId, cancellationToken).ConfigureAwait(false);

                HandoverCapsule capsule = CapsuleBuilder.Create(
                    sessionId,
                    parseResult.GetValue(executionOption) ?? turns.LastOrDefault()?.ExecutionId ?? sessionId,
                    objective,
                    parseResult.GetValue(constraintOption) ?? [],
                    // I fatti verificati arrivano da annotazioni dell'operatore: la beta non ne
                    // inventa, e un riassunto di un modello non sarebbe verificato.
                    [],
                    parseResult.GetValue(decisionOption) ?? [],
                    parseResult.GetValue(openWorkOption) ?? [],
                    [],
                    checkpoint,
                    SystemClock.Instance.UtcNow);

                if (json)
                {
                    CliJson.Write(output, CliJson.Ok("session capsule", new JsonObject
                    {
                        ["capsule"] = JsonNode.Parse(CapsuleBuilder.ToJson(capsule)),
                        ["turnsAvailable"] = turns.Count,
                        ["historyPersisted"] = service.PersistsHistory,
                    }));

                    return ExitCodes.Success;
                }

                output.WriteLine(CapsuleBuilder.ToJson(capsule));

                return ExitCodes.Success;
            }).ConfigureAwait(false);
        });

        return command;
    }

    /// <param name="readOnly">
    /// Vero per i comandi che si limitano a leggere: non prendono la proprieta' dello store, cosi'
    /// funzionano anche mentre un worker o l'host API lo possiedono. Chi scrive la pretende.
    /// </param>
    private static async Task<int> WithSessionsAsync(
        ParseResult parseResult,
        Option<DirectoryInfo?> dataOption,
        bool json,
        string command,
        Func<SqliteSessionService, RouterStack, Task<int>> body,
        bool readOnly = false)
    {
        TextWriter output = parseResult.InvocationConfiguration.Output;

        try
        {
            // Anche la risoluzione della directory sta dentro il try: un `PA_ROUTER_DATA_DIR` presente e non valido e'
            // un errore dell'operatore, e deve uscire con il suo codice invece che con una traccia di stack (S52).
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

            // L'apertura sta dentro il try: se un worker possiede la directory il comando deve
            // dirlo con il proprio codice, non morire con una traccia di stack.
            await using RouterStack runtime = RouterStack.Open(configuration.Require(), ownsStore: !readOnly);

            return await body(runtime.Sessions(), runtime).ConfigureAwait(false);
        }
        catch (PaRouterException ex)
        {
            return Fail(parseResult, json, command, ex.Code, ex.Message, ExitCodes.ForStorage(ex.Code));
        }
    }

    /// <summary>Identificatore di una sessione indicata per identificatore, prefisso o nome, sul trasporto del comando.</summary>
    private static async Task<string> ReferenceAsync(SessionGateway gateway, string text, CancellationToken cancellationToken)
    {
        SessionListPayload all = await gateway.ListAsync(includeArchived: true, cancellationToken).ConfigureAwait(false);

        // Non trovata fra le elencate: resta il testo, e l'operazione dira' con il proprio errore che non esiste.
        return SessionPicker.Reference(text, [.. all.Sessions.Select(session => (session.Id, session.DisplayName))]).SessionId ?? text.Trim();
    }

    /// <summary>Come <see cref="ReferenceAsync"/>, per i comandi che leggono lo store direttamente.</summary>
    private static string Reference(SqliteSessionService service, string text) =>
        SessionPicker.Reference(text, [.. service.Summaries(includeArchived: true, limit: 500).Select(session => (session.SessionId, session.DisplayName))])
            .SessionId ?? text.Trim();

    private static int Fail(
        ParseResult parseResult,
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
            CliJson.Write(parseResult.InvocationConfiguration.Output, CliJson.Error(command, code, message));
            return exitCode;
        }

        parseResult.InvocationConfiguration.Error.WriteLine(code.ToWire() + ": " + message);

        return exitCode;
    }
}
