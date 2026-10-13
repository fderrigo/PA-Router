using System.CommandLine;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Output;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Memory;
using PA.Router.Policy;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Commands;

/// <summary>
/// <c>pa memory search | timeline | show | forget | status | reindex</c>.
///
/// Tutti i comandi lavorano nell'ambito del progetto configurato e della sessione indicata: lo
/// scope non e' un parametro che allarga la visibilita', e chiedere un identificatore altrui
/// produce lo stesso errore di un identificatore inesistente (03-specs/MEMORY.md).
///
/// Il testo dei ricordi non finisce mai su standard error ne' nei log: <c>show</c> lo scrive su
/// standard output perche' e' cio' che l'operatore ha chiesto di vedere.
/// </summary>
internal static class MemoryCommand
{
    internal static Command Create(Option<DirectoryInfo?> dataOption)
    {
        Command command = new("memory", Ui.Text("Inspect and maintain the local memory."))
        {
            Search(dataOption),
            Timeline(dataOption),
            Show(dataOption),
            Forget(dataOption),
            Status(dataOption),
            Reindex(dataOption),
        };

        command.SetAction(parseResult =>
        {
            parseResult.InvocationConfiguration.Output.WriteLine(Ui.Text("Run 'pa memory --help' for the available subcommands."));
            return ExitCodes.UsageError;
        });

        return command;
    }

    private static Command Search(Option<DirectoryInfo?> dataOption)
    {
        Option<bool> stdinOption = new("--stdin") { Description = Ui.Text("Read the query from standard input.") };
        Option<string?> sessionOption = new("--session") { Description = Ui.Text("Session whose memories are in scope.") };
        Option<int> limitOption = new("--limit") { Description = Ui.Text("Results to return, 1 to 20."), DefaultValueFactory = _ => MemoryLimits.DefaultSearchLimit };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("search", Ui.Text("Search the authorized memories."))
        {
            dataOption,
            stdinOption,
            sessionOption,
            limitOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            TextWriter output = parseResult.InvocationConfiguration.Output;
            bool json = parseResult.GetValue(jsonOption);

            if (!parseResult.GetValue(stdinOption))
            {
                return Fail(parseResult, json, "memory search", ErrorCode.InputInvalid,
                    Ui.Text("The query must come from standard input: pass --stdin."), ExitCodes.UsageError);
            }

            string query = (await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();

            if (query.Length == 0)
            {
                return Fail(parseResult, json, "memory search", ErrorCode.InputInvalid, Ui.Text("The query is empty."), ExitCodes.InvalidInput);
            }

            return await WithMemoryAsync(parseResult, dataOption, sessionOption, json, "memory search", async (stack, scope) =>
            {
                MemorySearchResult result = await MemoryQueries.SearchAsync(
                    stack, scope, query, parseResult.GetValue(limitOption), cancellationToken).ConfigureAwait(false);

                if (json)
                {
                    JsonArray hits = [];

                    foreach (MemoryHit hit in result.Hits)
                    {
                        hits.Add(new JsonObject
                        {
                            ["id"] = hit.ObservationId,
                            ["kind"] = hit.Kind.ToWire(),
                            ["verification"] = hit.Verification.ToWire(),
                            ["createdAt"] = hit.CreatedAt.ToString("O", Formats.Invariant),
                            ["title"] = hit.Title,
                            ["score"] = Math.Round(hit.Score, 6),
                        });
                    }

                    CliJson.Write(output, CliJson.Ok("memory search", new JsonObject
                    {
                        ["mode"] = result.Mode.ToWire(),
                        ["indexStatus"] = result.IndexStatus.ToWire(),
                        ["modelFingerprint"] = result.ModelFingerprint,
                        // Un indice che fallisce e riprova resta «in ricostruzione»: il codice distingue
                        // «aspetta» da «non succedera'».
                        ["indexError"] = stack.Store.LastJobError()?.Code,
                        ["indexErrorAt"] = stack.Store.LastJobError()?.At.ToString("O", Formats.Invariant),
                        ["hits"] = hits,
                    }));

                    return ExitCodes.Success;
                }

                output.WriteLine(Repl.MemoryText.Search(result.Mode.ToWire(), result.IndexStatus.ToWire()));

                if (stack.Store.LastJobError() is (string indexError, DateTimeOffset failedAt))
                {
                    output.WriteLine(Ui.Format("memory.indexStuck {0} {1}", indexError, Repl.MemoryText.Failed(failedAt)));
                }

                if (result.Hits.Count == 0)
                {
                    output.WriteLine(Ui.Text("memory.none"));
                    return ExitCodes.Success;
                }

                int position = 0;

                foreach (MemoryHit hit in result.Hits)
                {
                    foreach (string line in Repl.MemoryText.Hit(
                        ++position, hit.CreatedAt, hit.Kind.ToWire(), hit.Verification.ToWire(), hit.Title, hit.ObservationId))
                    {
                        output.WriteLine(line);
                    }
                }

                return ExitCodes.Success;
            }, readOnly: true).ConfigureAwait(false);
        });

        return command;
    }

    private static Command Timeline(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("observation-id") { Description = Ui.Text("Memory to walk around.") };
        Option<string?> sessionOption = new("--session") { Description = Ui.Text("Session whose memories are in scope.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("timeline", Ui.Text("Show the authorized neighbours of a memory."))
        {
            dataOption,
            idArgument,
            sessionOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;

            return await WithMemoryAsync(parseResult, dataOption, sessionOption, json, "memory timeline", async (stack, scope) =>
            {
                IReadOnlyList<MemoryHit> around = await stack.Search.TimelineAsync(
                    scope,
                    parseResult.GetValue(idArgument)!,
                    MemoryLimits.MaxTimelineNeighbours,
                    MemoryLimits.MaxTimelineNeighbours,
                    cancellationToken).ConfigureAwait(false);

                if (json)
                {
                    JsonArray items = [];

                    foreach (MemoryHit hit in around)
                    {
                        items.Add(new JsonObject
                        {
                            ["id"] = hit.ObservationId,
                            ["kind"] = hit.Kind.ToWire(),
                            ["verification"] = hit.Verification.ToWire(),
                            ["createdAt"] = hit.CreatedAt.ToString("O", Formats.Invariant),
                            ["title"] = hit.Title,
                        });
                    }

                    CliJson.Write(output, CliJson.Ok("memory timeline", new JsonObject { ["items"] = items }));
                    return ExitCodes.Success;
                }

                foreach (MemoryHit hit in around)
                {
                    output.WriteLine(
                        hit.CreatedAt.ToString("yyyy-MM-dd HH:mm", Formats.Invariant) + "  "
                        + hit.ObservationId + "  " + hit.Title);
                }

                return ExitCodes.Success;
            }, readOnly: true).ConfigureAwait(false);
        });

        return command;
    }

    private static Command Show(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("observation-id") { Description = Ui.Text("Memory to read.") };
        Option<string?> sessionOption = new("--session") { Description = Ui.Text("Session whose memories are in scope.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("show", Ui.Text("Show one authorized memory."))
        {
            dataOption,
            idArgument,
            sessionOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;

            return await WithMemoryAsync(parseResult, dataOption, sessionOption, json, "memory show", async (stack, scope) =>
            {
                IReadOnlyList<ObservationRecord> found = await stack.Search.GetAsync(
                    scope, [parseResult.GetValue(idArgument)!], cancellationToken).ConfigureAwait(false);

                if (found.Count == 0)
                {
                    // Stessa risposta per «non esiste» e «non e' tuo»: la seconda non va rivelata.
                    return Fail(parseResult, json, "memory show", ErrorCode.MemoryScopeDenied,
                        Ui.Text("That memory is not available in this scope."), ExitCodes.PolicyDenied);
                }

                ObservationRecord observation = found[0];

                if (json)
                {
                    JsonArray evidence = [];

                    foreach (string reference in observation.EvidenceRefs)
                    {
                        evidence.Add(reference);
                    }

                    CliJson.Write(output, CliJson.Ok("memory show", new JsonObject
                    {
                        ["id"] = observation.Id,
                        ["kind"] = observation.Kind.ToWire(),
                        ["verification"] = observation.Verification.ToWire(),
                        ["classification"] = observation.Classification.ToWire(),
                        ["scope"] = observation.Scope.ToWire(),
                        ["version"] = observation.Version,
                        ["supersedes"] = observation.Supersedes,
                        ["createdAt"] = observation.CreatedAt.ToString("O", Formats.Invariant),
                        ["expiresAt"] = observation.ExpiresAt.ToString("O", Formats.Invariant),
                        ["evidenceRefs"] = evidence,
                        ["content"] = observation.Content,
                    }));

                    return ExitCodes.Success;
                }

                foreach (string line in Repl.MemoryText.Show(
                    observation.CreatedAt,
                    observation.ExpiresAt,
                    observation.Kind.ToWire(),
                    observation.Verification.ToWire(),
                    observation.Classification.ToWire(),
                    observation.Content,
                    observation.Id))
                {
                    output.WriteLine(line);
                }

                return ExitCodes.Success;
            }, readOnly: true).ConfigureAwait(false);
        });

        return command;
    }

    private static Command Forget(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("observation-id") { Description = Ui.Text("Memory to remove.") };
        Option<string?> sessionOption = new("--session") { Description = Ui.Text("Session whose memories are in scope.") };
        Option<bool> confirmOption = new("--yes") { Description = Ui.Text("Confirm the removal without prompting.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("forget", Ui.Text("Remove one memory and purge its vectors."))
        {
            dataOption,
            idArgument,
            sessionOption,
            confirmOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;

            if (!parseResult.GetValue(confirmOption))
            {
                // Cancellazione irreversibile: la conferma e' esplicita e locale, mai implicita.
                return Fail(parseResult, json, "memory forget", ErrorCode.InputInvalid,
                    Ui.Text("Removing a memory is irreversible: confirm with --yes."), ExitCodes.UsageError);
            }

            string id = parseResult.GetValue(idArgument)!;

            return await WithMemoryAsync(parseResult, dataOption, sessionOption, json, "memory forget", async (stack, scope) =>
            {
                IReadOnlyList<ObservationRecord> found = await stack.Search.GetAsync(scope, [id], cancellationToken).ConfigureAwait(false);

                if (found.Count == 0)
                {
                    return Fail(parseResult, json, "memory forget", ErrorCode.MemoryScopeDenied,
                        Ui.Text("That memory is not available in this scope."), ExitCodes.PolicyDenied);
                }

                await stack.Store.TombstoneAsync(id, cancellationToken).ConfigureAwait(false);

                await stack.Store.RecordAsync(
                    MemoryAuditCodes.Forgotten,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["observationId"] = id,
                        ["sessionId"] = scope.SessionId,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (json)
                {
                    CliJson.Write(output, CliJson.Ok("memory forget", new JsonObject
                    {
                        ["id"] = id,
                        ["purgeQueued"] = true,
                    }));

                    return ExitCodes.Success;
                }

                output.WriteLine(Ui.Text("Ricordo ") + id + Ui.Text(" rimosso; purge dei vettori accodato."));

                return ExitCodes.Success;
            }).ConfigureAwait(false);
        });

        return command;
    }

    private static Command Status(Option<DirectoryInfo?> dataOption)
    {
        Option<string?> sessionOption = new("--session") { Description = Ui.Text("Session whose memories are in scope.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("status", Ui.Text("Show the state of the local memory."))
        {
            dataOption,
            sessionOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;

            return await WithMemoryAsync(parseResult, dataOption, sessionOption, json, "memory status", async (stack, scope) =>
            {
                MemoryQueueStatus status = await MemoryQueries.StatusAsync(stack, scope, cancellationToken).ConfigureAwait(false);

                if (json)
                {
                    CliJson.Write(output, CliJson.Ok("memory status", new JsonObject
                    {
                        ["configured"] = status.Configured,
                        ["modelFingerprint"] = status.ModelFingerprint,
                        ["observations"] = status.Observations,
                        ["jobsPending"] = status.JobsPending,
                        ["jobsRunning"] = status.JobsRunning,
                        ["jobsFailed"] = status.JobsFailed,
                        ["generation"] = status.Generation,
                        ["maxChunksPerProject"] = MemoryLimits.MaxChunksPerProject,
                    }));

                    return ExitCodes.Success;
                }

                foreach (string line in Repl.MemoryText.Status(
                    status.ModelFingerprint, status.Observations, status.JobsPending, status.JobsRunning, status.JobsFailed))
                {
                    output.WriteLine(line);
                }

                if (!status.Configured)
                {
                    output.WriteLine(Ui.Text("Senza modello di embedding la ricerca resta solo lessicale."));
                }

                return ExitCodes.Success;
            }, readOnly: true).ConfigureAwait(false);
        });

        return command;
    }

    private static Command Reindex(Option<DirectoryInfo?> dataOption)
    {
        Option<string?> sessionOption = new("--session") { Description = Ui.Text("Session whose memories are in scope.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("reindex", Ui.Text("Run the pending memory jobs with the local model."))
        {
            dataOption,
            sessionOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;

            return await WithMemoryAsync(parseResult, dataOption, sessionOption, json, "memory reindex", async (stack, scope) =>
            {
                _ = scope;

                if (!stack.Configured)
                {
                    // Nessun ripiego cloud: senza modello locale non si indicizza.
                    return Fail(parseResult, json, "memory reindex", ErrorCode.EmbeddingUnavailable,
                        Ui.Text("No local embedding model is configured."), ExitCodes.InvalidInput);
                }

                // Reindicizzare non e' svuotare la coda: dopo un cambio di modello i lavori vecchi
                // sono legati al fingerprint di prima e falliscono per costruzione. Qui si accoda
                // cio' che manca sotto il modello attivo e poi si esegue (difetto D2).
                (int enqueued, int executed) = await stack
                    .ReindexAsync(1024, cancellationToken).ConfigureAwait(false);

                if (json)
                {
                    CliJson.Write(output, CliJson.Ok("memory reindex", new JsonObject
                    {
                        ["jobsEnqueued"] = enqueued,
                        ["jobsExecuted"] = executed,
                        ["jobsPending"] = stack.Store.CountJobs(MemoryJobState.Pending),
                        ["jobsFailed"] = stack.Store.CountJobs(MemoryJobState.Failed),
                    }));

                    return ExitCodes.Success;
                }

                output.WriteLine(
                    Ui.Text("Lavori accodati: ") + enqueued.ToString(Formats.Invariant)
                    + Ui.Text(", eseguiti: ") + executed.ToString(Formats.Invariant)
                    + Ui.Text(", ancora in coda: ") + stack.Store.CountJobs(MemoryJobState.Pending).ToString(Formats.Invariant));

                return ExitCodes.Success;
            }).ConfigureAwait(false);
        });

        return command;
    }

    /// <summary>
    /// Apre configurazione, runtime e memoria per un comando, e chiude tutto anche in caso di
    /// errore tipizzato.
    /// </summary>
    /// <param name="readOnly">
    /// Vero per i comandi che si limitano a leggere: non prendono la proprieta' dello store, cosi'
    /// funzionano anche mentre un worker o l'host API lo possiedono. Chi scrive la pretende.
    /// </param>
    private static async Task<int> WithMemoryAsync(
        ParseResult parseResult,
        Option<DirectoryInfo?> dataOption,
        Option<string?> sessionOption,
        bool json,
        string command,
        Func<MemoryStack, MemoryScope, Task<int>> body,
        bool readOnly = false)
    {
        TextWriter output = parseResult.InvocationConfiguration.Output;
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

        if (!MemoryQueries.Available(snapshot))
        {
            return Fail(parseResult, json, command, ErrorCode.MemoryUnavailable,
                Ui.Text("The local memory is disabled in the configuration."), ExitCodes.InvalidInput);
        }

        if (!snapshot.Profiles.TryGetValue(snapshot.Config.DefaultProfileId, out PolicyDocument? profile))
        {
            return Fail(parseResult, json, command, ErrorCode.PolicyInvalid,
                Ui.Text("The configured profile is not defined by any policy file."), ExitCodes.InvalidInput);
        }

        string session = parseResult.GetValue(sessionOption) ?? DefaultSession;

        try
        {
            await using RouterStack runtime = RouterStack.Open(snapshot, ownsStore: !readOnly);

            // Ambito e composizione della memoria sono gli stessi dei comandi /memory e /jobs del
            // REPL: vengono da un punto solo (INV-22).
            using MemoryStack stack = MemoryQueries.Open(runtime, profile, session, out MemoryScope scope);

            return await body(stack, scope).ConfigureAwait(false);
        }
        catch (PaRouterException ex)
        {
            return Fail(parseResult, json, command, ex.Code, ex.Message, ExitCodes.ForStorage(ex.Code));
        }
    }

    /// <summary>
    /// Sessione usata quando l'operatore non ne indica una: rende visibile la sola memoria di
    /// progetto. I ricordi di sessione richiedono <c>--session</c>, perche' appartengono a quella.
    /// </summary>
    internal const string DefaultSession = "00000000-0000-4000-8000-000000000000";

    private static int Fail(
        ParseResult parseResult,
        bool json,
        string command,
        ErrorCode code,
        string message,
        int exitCode)
    {
        if (json)
        {
            CliJson.Write(parseResult.InvocationConfiguration.Output, CliJson.Error(command, code, message));
            return exitCode;
        }

        parseResult.InvocationConfiguration.Error.WriteLine(code.ToWire() + ": " + message);

        return exitCode;
    }
}
