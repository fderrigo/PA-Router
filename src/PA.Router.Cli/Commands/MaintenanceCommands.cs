using System.CommandLine;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Abstractions.Observability;
using PA.Router.Cli.Output;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Observability;
using PA.Router.Infrastructure.Storage;
using PA.Router.Policy;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Commands;

/// <summary>
/// <c>pa maintenance retention | backup | verify | telemetry</c>.
///
/// Manutenzione a riposo (03-specs/STORAGE-AUDIT.md): la retention e il backup si eseguono quando
/// non c'e' un turno in corso, e lo store viene aperto in esclusiva proprio per garantirlo — se un
/// worker lo possiede, il comando dice STORE_IN_USE invece di lavorare su un database vivo.
///
/// <c>verify</c> controlla una copia ma non la ripristina: rimettere al posto dei dati e' una
/// decisione dell'operatore, e un riavvio automatico dopo un restore riprenderebbe esecuzioni di
/// cui nessuno conosce lo stato.
/// </summary>
internal static class MaintenanceCommands
{
    internal static Command Create(Option<DirectoryInfo?> dataOption)
    {
        Command command = new("maintenance", Ui.Text("Run the idle-time maintenance of the local store."))
        {
            Retention(dataOption),
            Backup(dataOption),
            Verify(),
            Telemetry(dataOption),
        };

        command.SetAction(parseResult =>
        {
            parseResult.InvocationConfiguration.Output.WriteLine(
                Ui.Text("Run 'pa maintenance --help' for the available subcommands."));

            return ExitCodes.UsageError;
        });

        return command;
    }

    private static Command Retention(Option<DirectoryInfo?> dataOption)
    {
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("retention", Ui.Text("Expire contents, events, idempotency keys and audit rows."))
        {
            dataOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;

            return await WithRuntimeAsync(parseResult, dataOption, json, "maintenance retention", async runtime =>
            {
                RetentionReport report = await runtime.Maintenance()
                    .ApplyRetentionAsync(runtime.ContentStore(), runtime.MemoryStore(), cancellationToken)
                    .ConfigureAwait(false);

                if (json)
                {
                    CliJson.Write(output, CliJson.Ok("maintenance retention", new JsonObject
                    {
                        ["contents"] = report.Contents,
                        ["events"] = report.Events,
                        ["idempotencyKeys"] = report.IdempotencyKeys,
                        ["auditEvents"] = report.AuditEvents,
                        ["orphanFiles"] = report.OrphanFiles,
                        ["memories"] = report.Memories,
                    }));

                    return ExitCodes.Success;
                }

                output.WriteLine(Ui.Text("Contenuti scaduti:   ") + report.Contents.ToString(Formats.Invariant));
                output.WriteLine(Ui.Text("Eventi scaduti:      ") + report.Events.ToString(Formats.Invariant));
                output.WriteLine(Ui.Text("Chiavi scadute:      ") + report.IdempotencyKeys.ToString(Formats.Invariant));
                output.WriteLine(Ui.Text("Audit oltre 30g:     ") + report.AuditEvents.ToString(Formats.Invariant));
                output.WriteLine(Ui.Text("File orfani rimossi: ") + report.OrphanFiles.ToString(Formats.Invariant));
                output.WriteLine(Ui.Text("Ricordi scaduti:     ") + report.Memories.ToString(Formats.Invariant));

                return ExitCodes.Success;
            }).ConfigureAwait(false);
        });

        return command;
    }

    private static Command Backup(Option<DirectoryInfo?> dataOption)
    {
        Option<DirectoryInfo> toOption = new("--to") { Description = Ui.Text("Empty directory that will hold the copy."), Required = true };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("backup", Ui.Text("Copy database, blobs and protected key with a checksum manifest."))
        {
            dataOption,
            toOption,
            jsonOption,
        };

        command.SetAction(parseResult =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;
            DirectoryInfo destination = parseResult.GetValue(toOption)!;

            return WithRuntimeAsync(parseResult, dataOption, json, "maintenance backup", runtime =>
            {
                BackupReport report = runtime.Maintenance().Backup(destination.FullName);

                if (json)
                {
                    CliJson.Write(output, CliJson.Ok("maintenance backup", new JsonObject
                    {
                        ["directory"] = report.Directory,
                        ["files"] = report.Files,
                        ["manifest"] = report.ManifestPath,
                        // La chiave e' protetta con DPAPI: su un'altra macchina la copia non si apre.
                        ["portable"] = false,
                    }));

                    return Task.FromResult(ExitCodes.Success);
                }

                output.WriteLine(Ui.Text("Copia in:   ") + report.Directory);
                output.WriteLine(Ui.Text("File:       ") + report.Files.ToString(Formats.Invariant));
                output.WriteLine(Ui.Text("Manifesto:  ") + report.ManifestPath);
                output.WriteLine(Ui.Text("La chiave protetta vale solo per questo utente e questa macchina."));

                return Task.FromResult(ExitCodes.Success);
            });
        });

        return command;
    }

    private static Command Verify()
    {
        Argument<DirectoryInfo> pathArgument = new("path") { Description = Ui.Text("Backup directory to check.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("verify", Ui.Text("Check a backup against its manifest. Does not restore anything."))
        {
            pathArgument,
            jsonOption,
        };

        command.SetAction(parseResult =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;
            DirectoryInfo directory = parseResult.GetValue(pathArgument)!;

            bool valid = directory.Exists && MaintenanceService.Verify(directory.FullName);

            if (json)
            {
                CliJson.Write(output, valid
                    ? CliJson.Ok("maintenance verify", new JsonObject { ["valid"] = true })
                    : CliJson.Error("maintenance verify", ErrorCode.ContentUnavailable,
                        Ui.Text("The backup does not match its manifest.")));
            }
            else
            {
                output.WriteLine(valid
                    ? Ui.Text("Backup integro: ogni file corrisponde al manifesto.")
                    : Ui.Text("Backup non integro: manifesto assente o digest diversi."));
            }

            return valid ? ExitCodes.Success : ExitCodes.InvalidInput;
        });

        return command;
    }

    private static Command Telemetry(Option<DirectoryInfo?> dataOption)
    {
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("telemetry", Ui.Text("Show what the telemetry would export and what stays local."))
        {
            dataOption,
            jsonOption,
        };

        command.SetAction(parseResult =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;
            string dataDirectory = CommandContext.ResolveDataDirectory(parseResult, dataOption);

            ConfigurationLoadResult configuration = new ConfigurationLoader(SystemClock.Instance).Load(dataDirectory);

            TelemetryOptions options = configuration.IsValid
                ? TelemetryOptions.FromConfig(
                    configuration.Require().Config,
                    PolicyCompiler.Compile(configuration.Require().DefaultProfile, configuration.Require().Config).Zones)
                : TelemetryOptions.Default;

            TelemetryDecision decision = options.Resolve();

            if (json)
            {
                CliJson.Write(output, CliJson.Ok("maintenance telemetry", new JsonObject
                {
                    ["decision"] = decision.ToString(),
                    ["source"] = RouterTelemetry.SourceName,
                    ["allowedAttributes"] = new JsonArray(
                        [.. RouterTelemetry.AllowedAttributes.OrderBy(name => name, StringComparer.Ordinal)
                            .Select(name => (JsonNode?)name)]),
                    ["spans"] = new JsonArray([.. RouterTelemetry.SpanNames.Select(name => (JsonNode?)name)]),
                    ["gauges"] = new JsonArray([.. RouterTelemetry.GaugeNames.Select(name => (JsonNode?)name)]),
                }));

                return ExitCodes.Success;
            }

            output.WriteLine(Ui.Text("Telemetria:  ") + Describe(decision));
            output.WriteLine(Ui.Text("Sorgente:    ") + RouterTelemetry.SourceName);
            output.WriteLine(Ui.Text("Attributi:   ") + string.Join(", ",
                RouterTelemetry.AllowedAttributes.OrderBy(name => name, StringComparer.Ordinal)));
            output.WriteLine(Ui.Text("Fuori:       prompt, output, percorsi, sessionId, executionId, messaggi del provider"));

            return ExitCodes.Success;
        });

        return command;
    }

    private static string Describe(TelemetryDecision decision) => decision switch
    {
        TelemetryDecision.Disabled => Ui.Text("spenta (nessun ascoltatore, nessuna uscita)"),
        TelemetryDecision.LocalFile => Ui.Text("accesa in locale (scrive nel log della directory dati)"),
        TelemetryDecision.RemoteRefusedDataStaysHome => Ui.Text("telemetry.refused.home"),
        TelemetryDecision.RemoteRefusedInsecure => Ui.Text("export remoto rifiutato: l'endpoint non usa TLS"),
        _ => Ui.Text("export remoto non implementato in questa beta: nessun collector incluso"),
    };

    private static async Task<int> WithRuntimeAsync(
        ParseResult parseResult,
        Option<DirectoryInfo?> dataOption,
        bool json,
        string command,
        Func<RouterStack, Task<int>> body)
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

            await using RouterStack runtime = RouterStack.Open(configuration.Require());

            return await body(runtime).ConfigureAwait(false);
        }
        catch (PaRouterException ex)
        {
            if (json)
            {
                CliJson.Write(output, CliJson.Error(command, ex.Code, ex.Message));
            }
            else
            {
                parseResult.InvocationConfiguration.Error.WriteLine(ex.Code.ToWire() + ": " + ex.Message);
            }

            return ex.Code is ErrorCode.StoreInUse or ErrorCode.StoreUnavailable
                ? ExitCodes.StoreUnavailable
                : ExitCodes.InvalidInput;
        }
    }
}
