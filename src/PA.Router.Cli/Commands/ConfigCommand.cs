using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Output;
using PA.Router.Cli.Repl;
using PA.Router.Hosting.Runtime;
using PA.Router.Hosting.Security;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Crypto;
using PA.Router.Infrastructure.Storage;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Commands;

/// <summary>
/// <c>pa config show</c>: mostra la configurazione validata in forma sanitizzata.
///
/// Vengono stampati riferimenti e valori non segreti. Il token di servizio compare solo come
/// nome nel vault: l'esportazione del valore e' un comando separato dell'operatore, non un
/// effetto collaterale di una lettura.
/// </summary>
internal static class ConfigCommand
{
    internal static Command Create(Option<DirectoryInfo?> dataOption)
    {
        Option<bool> jsonOption = new("--json")
        {
            Description = Ui.Text("Write the sanitized configuration as JSON on standard output."),
        };

        Command show = new("show", Ui.Text("Show the validated configuration without secrets."))
        {
            dataOption,
            jsonOption,
        };

        show.SetAction(parseResult => Run(parseResult, dataOption, jsonOption));

        Option<bool> applyOption = new("--apply")
        {
            Description = Ui.Text("Write the converted files, keeping the originals as *.v1.bak."),
        };

        Command migrate = new("migrate", Ui.Text("Convert schema version 1 files to version 2."))
        {
            dataOption,
            applyOption,
        };

        migrate.SetAction(parseResult => Migrate(parseResult, dataOption, applyOption));

        Command config = new("config", Ui.Text("Inspect the operator configuration."))
        {
            show,
            migrate,
            Secret(dataOption),
        };

        config.SetAction(parseResult =>
        {
            parseResult.InvocationConfiguration.Output.WriteLine(
                Ui.Text("Specify a config subcommand. Run 'pa config --help' for the list."));
            return ExitCodes.UsageError;
        });

        return config;
    }

    /// <summary>
    /// <c>pa config secret</c>: scrive e elenca i segreti riferiti per nome dalla configurazione.
    ///
    /// Il valore arriva da standard input, mai da un argomento: un argomento resterebbe nella
    /// cronologia della shell. Non viene mai stampato, nemmeno subito dopo averlo scritto.
    /// </summary>
    private static Command Secret(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> nameArgument = new("name") { Description = Ui.Text("Name the configuration refers to.") };
        Option<bool> stdinOption = new("--stdin") { Description = Ui.Text("Read the secret value from standard input.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command set = new("set", Ui.Text("Store a secret value in the local vault."))
        {
            nameArgument,
            stdinOption,
            dataOption,
            jsonOption,
        };

        set.SetAction(parseResult => SetSecret(parseResult, dataOption, nameArgument, stdinOption, jsonOption));

        Command list = new("list", Ui.Text("List the names of the stored secrets, never the values."))
        {
            dataOption,
            jsonOption,
        };

        list.SetAction(parseResult => ListSecrets(parseResult, dataOption, jsonOption));

        Command secret = new("secret", Ui.Text("Manage the secrets the configuration refers to by name."))
        {
            set,
            list,
        };

        secret.SetAction(parseResult =>
        {
            parseResult.InvocationConfiguration.Output.WriteLine(
                Ui.Text("Specify a secret subcommand. Run 'pa config secret --help' for the list."));
            return ExitCodes.UsageError;
        });

        return secret;
    }

    private static int SetSecret(
        ParseResult parseResult,
        Option<DirectoryInfo?> dataOption,
        Argument<string> nameArgument,
        Option<bool> stdinOption,
        Option<bool> jsonOption)
    {
        bool json = parseResult.GetValue(jsonOption);
        TextWriter output = parseResult.InvocationConfiguration.Output;
        string name = parseResult.GetValue(nameArgument)!;

        if (!parseResult.GetValue(stdinOption))
        {
            // Un valore passato come argomento resterebbe nella cronologia della shell.
            return Fail(parseResult, json, "config secret set", ErrorCode.InputInvalid,
                Ui.Text("The value must come from standard input: pass --stdin."), ExitCodes.UsageError);
        }

        string value = Console.In.ReadToEnd().Trim();

        if (value.Length == 0)
        {
            return Fail(parseResult, json, "config secret set", ErrorCode.InputInvalid,
                Ui.Text("The secret is empty."), ExitCodes.InvalidInput);
        }

        StorePaths paths = StorePaths.For(CommandContext.ResolveDataDirectory(parseResult, dataOption)).EnsureCreated();

        if (!LocalSecretVault.TryWrite(paths, PlatformKeyProtector.Create(), name, value))
        {
            // Senza protector non si scrive nulla: un segreto in chiaro sarebbe peggio della sua
            // assenza.
            return Fail(parseResult, json, "config secret set", ErrorCode.CryptoUnavailable,
                Ui.Text("This platform has no key protector: the secret was not stored."), ExitCodes.InvalidInput);
        }

        if (json)
        {
            CliJson.Write(output, CliJson.Ok("config secret set", new JsonObject { ["name"] = name }));
            return ExitCodes.Success;
        }

        output.WriteLine(Ui.Text("Segreto '") + name + Ui.Text("' scritto nel vault locale. Il valore non viene stampato."));
        return ExitCodes.Success;
    }

    private static int ListSecrets(ParseResult parseResult, Option<DirectoryInfo?> dataOption, Option<bool> jsonOption)
    {
        bool json = parseResult.GetValue(jsonOption);
        TextWriter output = parseResult.InvocationConfiguration.Output;

        StorePaths paths = StorePaths.For(CommandContext.ResolveDataDirectory(parseResult, dataOption));
        IReadOnlyList<string> names = LocalSecretVault.List(paths);

        if (json)
        {
            CliJson.Write(output, CliJson.Ok("config secret list", new JsonObject
            {
                ["names"] = new JsonArray([.. names.Select(name => JsonValue.Create(name))]),
            }));

            return ExitCodes.Success;
        }

        output.WriteLine(names.Count == 0 ? Ui.Text("Nessun segreto nel vault locale.") : Ui.Text("Segreti nel vault locale:"));

        foreach (string name in names)
        {
            output.WriteLine("  " + name);
        }

        return ExitCodes.Success;
    }

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
        }
        else
        {
            parseResult.InvocationConfiguration.Error.WriteLine(message);
        }

        return exitCode;
    }

    /// <summary>
    /// Mostra la conversione alla versione 2 e la applica solo con <c>--apply</c>. Convertire in
    /// silenzio un file che decide autorizzazioni sarebbe una comodita' che si paga (ADR-015).
    /// </summary>
    private static int Migrate(ParseResult parseResult, Option<DirectoryInfo?> dataOption, Option<bool> applyOption)
    {
        string dataDirectory = CommandContext.ResolveDataDirectory(parseResult, dataOption);
        TextWriter output = parseResult.InvocationConfiguration.Output;
        TextWriter error = parseResult.InvocationConfiguration.Error;

        MigrationPlan plan = ConfigMigration.Plan(dataDirectory);

        foreach (string problem in plan.Problems)
        {
            error.WriteLine(problem);
        }

        if (!plan.IsApplicable)
        {
            return plan.Problems.Count > 0 ? ExitCodes.InvalidInput : ExitCodes.Success;
        }

        foreach (MigratedFile file in plan.Files)
        {
            // Un'etichetta si nomina come nel setup; il file resta tra parentesi, e' dove andare a guardare.
            output.WriteLine(file.ProfileId is string profileId
                ? Ui.Format("migrate.label {0} {1}", Repl.ProfilePresentation.Name(profileId, file.ProfileName), file.RelativePath)
                : file.RelativePath + " (" + file.Kind.ToString().ToLowerInvariant() + ")");

            foreach (string change in file.Changes)
            {
                // Le regole a zone si dicono con i nomi delle zone, come in `doctor` (24 settembre 2026): la riga tecnica
                // nominava campi che l'operatore non ha mai scritto.
                // La riga riassuntiva delle zone la scrive la conversione della fase 4 di ADR-032 (`zones.maxZone ...`).
                if (file.Zones is PolicyZones zones && change.StartsWith("zones.maxZone ", StringComparison.Ordinal))
                {
                    output.WriteLine("  " + Ui.Format("migrate.zones {0} {1} {2} {3}",
                        Repl.ReplConfirmation.Zone(zones.For(DataClass.Public).ToWire()),
                        Repl.ReplConfirmation.Zone(zones.For(DataClass.Internal).ToWire()),
                        Repl.ReplConfirmation.Zone(zones.For(DataClass.Personal).ToWire()),
                        Repl.ReplConfirmation.Zone(zones.For(DataClass.Sensitive).ToWire())));
                    continue;
                }

                output.WriteLine("  " + change);
            }
        }

        if (!parseResult.GetValue(applyOption))
        {
            output.WriteLine();
            output.WriteLine(Ui.Text("Nothing was written. Run again with --apply to convert the files."));
            return ExitCodes.Success;
        }

        ConfigMigration.Apply(dataDirectory, plan);

        output.WriteLine();
        output.WriteLine(
            Ui.Text("Converted ")
            + plan.Files.Count.ToString(Formats.Invariant)
            + Ui.Text(" file(s); the originals are next to them with the ")
            + plan.BackupSuffix
            + Ui.Text(" suffix."));

        // La conversione non abilita nulla e non sostituisce la validazione: il caricamento
        // successivo dira' se il risultato e' utilizzabile.
        ConfigurationLoadResult result = new ConfigurationLoader(SystemClock.Instance).Load(dataDirectory);

        if (result.IsValid)
        {
            output.WriteLine(Ui.Text("The converted configuration loads."));
            return ExitCodes.Success;
        }

        output.WriteLine(Ui.Text("The converted configuration still needs work:"));
        return CommandContext.ReportIssues(parseResult, result.Issues);
    }

    private static int Run(ParseResult parseResult, Option<DirectoryInfo?> dataOption, Option<bool> jsonOption)
    {
        string dataDirectory = CommandContext.ResolveDataDirectory(parseResult, dataOption);
        TextWriter output = parseResult.InvocationConfiguration.Output;

        ConfigurationLoadResult result = new ConfigurationLoader(SystemClock.Instance).Load(dataDirectory);

        bool json = parseResult.GetValue(jsonOption);

        if (!result.IsValid)
        {
            // Con `--json` anche un rifiuto e' un documento: prima questo percorso scriveva testo
            // sull'errore standard e lasciava lo standard output vuoto, quindi un lettore automatico
            // non riceveva nulla da leggere.
            if (json)
            {
                CliJson.Write(output, CliJson.Invalid("config show", result.Issues));
                return ExitCodes.InvalidInput;
            }

            return CommandContext.ReportIssues(parseResult, result.Issues);
        }

        ConfigurationSnapshot snapshot = result.Require();

        if (json)
        {
            // Dalla S57 anche questo comando usa l'envelope: `schemaVersion`, `command`, `ok` e il
            // contenuto sotto `result`. Prima emetteva il payload nudo, unico fra tutti i comandi, e
            // uno script non poteva distinguere un esito riuscito da un documento qualunque.
            CliJson.Write(output, CliJson.Ok("config show", Sanitized(snapshot)));
            return ExitCodes.Success;
        }

        WriteText(output, snapshot);
        return ExitCodes.Success;
    }

    /// <summary>
    /// La configurazione per una persona (23 settembre 2026): etichette in italiano allineate, nomi al posto degli
    /// identificatori, le etichette con la loro descrizione calcolata e i servizi con la resa di `pa registry list`. Prima
    /// meta' delle voci erano chiavi del file in inglese («concurrency», «attempts», «yes», «encrypted») e le colonne
    /// si rompevano sulle voci lunghe. I valori grezzi e l'impronta completa restano in `--json`.
    /// </summary>
    private static void WriteText(TextWriter output, ConfigurationSnapshot snapshot)
    {
        ConfigDocument config = snapshot.Config;
        const int Width = 34;

        void Row(string key, string value) => output.WriteLine("  " + (Ui.Text(key) + " ").PadRight(Width, ' ') + value);
        void Title(string key) { output.WriteLine(); output.WriteLine(Ui.Text(key)); }
        string YesNo(bool value) => value ? Ui.Text("show.yes") : Ui.Text("show.no");

        output.WriteLine(Ui.Text("show.title"));
        Row("show.directory", snapshot.DataDirectory);
        Row("show.language", Ui.NativeName(config.Language));
        Row("show.defaultLabel", ProfilePresentation.Name(snapshot, config.DefaultProfileId));
        Row("show.contents", config.ContentMode is ContentMode.Encrypted ? Ui.Text("show.kept") : Ui.Text("show.notKept"));
        Row("show.subscription", YesNo(config.PreferSubscription));

        Title("show.limits");
        Row("show.concurrency", config.Limits.MaxConcurrency.ToString(Formats.Invariant));
        Row("show.timeout", Ui.Format("show.seconds {0}", config.Limits.TimeoutSeconds.ToString(Formats.Invariant)));
        Row("show.attempts", config.Limits.MaxAttempts.ToString(Formats.Invariant));
        Row("show.history", config.Limits.HistoryTurns.ToString(Formats.Invariant));
        Row("show.maxInput", Ui.Format("show.bytes {0}", config.Limits.MaxInputBytes.ToString(Formats.Invariant)));
        Row("show.maxContext", Ui.Format("show.bytes {0}", config.Limits.MaxContextBytes.ToString(Formats.Invariant)));
        Row("show.maxOutput", Ui.Format("show.bytes {0}", config.Limits.MaxOutputBytes.ToString(Formats.Invariant)));
        Row("show.maxCost", config.Limits.MaxCostPerRequest is double cap
            ? cap.ToString("0.######", Formats.Invariant) + (snapshot.Registry.Currency is string currency ? " " + currency : string.Empty)
            : Ui.Text("show.noCap"));

        Title("show.retention");
        Row("show.audit", Ui.Format("show.days {0}", config.Retention.AuditDays.ToString(Formats.Invariant)));
        Row("show.conversations", Ui.Format("show.days {0}", config.Retention.ContentDays.ToString(Formats.Invariant)));
        Row("show.events", Ui.Format("show.hours {0}", config.Retention.EventHours.ToString(Formats.Invariant)));

        Title("show.memory");
        Row("show.memoryOn", config.Memory.Enabled
            ? (config.Memory.Required ? Ui.Text("show.memoryRequired") : Ui.Text("show.memoryOptional"))
            : Ui.Text("show.no"));
        Row("show.memorySearch", config.Memory.EmbeddingServiceId is string embedding
            ? ServiceNames.Display(snapshot, embedding)
            : Ui.Text("show.memoryNoModel"));
        Row("show.memoryKept", Ui.Format("show.days {0}", config.Memory.RetentionDays.ToString(Formats.Invariant)));

        if (config.Memory.IsUnconfigured)
        {
            Row("show.memoryState", Ui.Text("show.memoryUnconfigured"));
        }

        Title("show.api");
        Row("show.apiAddress", config.Api.Host + ":" + config.Api.Port.ToString(Formats.Invariant));
        Row("show.apiLabel", ProfilePresentation.Name(snapshot, config.Api.ServiceProfileId));
        Row("show.apiToken", Ui.Format("show.apiTokenKept {0}", config.Api.ServiceTokenRef));

        Title("show.telemetry");
        Row("show.telemetrySent", config.Telemetry.Endpoint is Uri endpoint
            ? Ui.Format("show.telemetryTo {0} {1}", config.Telemetry.Exporter.ToWire(), endpoint.ToString())
            : Ui.Text("show.telemetryNone"));

        Title("show.labels");

        foreach (PolicyDocument profile in snapshot.Profiles.Values.OrderBy(item => ProfilePresentation.Name(item), StringComparer.CurrentCultureIgnoreCase))
        {
            output.WriteLine("  " + ProfilePresentation.Name(profile)
                + (profile.ProfileId == config.DefaultProfileId ? Ui.Text("show.initial") : string.Empty));
            output.WriteLine("      " + ProfilePresentation.Summary(ProfileFacts.Describe(snapshot, profile.ProfileId, SystemClock.Instance)));
        }

        output.WriteLine();
        RegistryText.Write(snapshot, output.WriteLine, profileId: null);

        output.WriteLine();
        output.WriteLine(Ui.Format("show.footer {0} {1}",
            config.SchemaVersion.ToString(Formats.Invariant), snapshot.Digest.Length > 12 ? snapshot.Digest[..12] : snapshot.Digest));
    }

    private static JsonObject Sanitized(ConfigurationSnapshot snapshot)
    {
        ConfigDocument config = snapshot.Config;

        JsonArray services = [];

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
                ["needsConfiguration"] = service.HasPlaceholders,
            });
        }

        JsonArray profiles = [];

        foreach (PolicyDocument profile in snapshot.Profiles.Values.OrderBy(item => item.ProfileId, StringComparer.Ordinal))
        {
            profiles.Add(new JsonObject
            {
                ["profileId"] = profile.ProfileId,
                // Dal 25 settembre 2026 (ADR-032, fase 4) dove vanno i dati lo dicono le sole zone.
                ["maxZone"] = new JsonObject(PolicyZones.DeclaredClasses.Select(dataClass =>
                    KeyValuePair.Create(dataClass.ToWire(), (JsonNode?)JsonValue.Create(profile.Constraints.Zones.For(dataClass).ToWire())))),
                ["minimumDataClass"] = profile.Constraints.MinimumDataClass.ToWire(),
            });
        }

        return new JsonObject
        {
            ["dataDirectory"] = snapshot.DataDirectory,
            ["schemaVersion"] = config.SchemaVersion,
            ["defaultProfileId"] = config.DefaultProfileId,
            ["language"] = config.Language,
            ["contentMode"] = config.ContentMode.ToWire(),
            ["preferSubscription"] = config.PreferSubscription,
            ["digest"] = snapshot.Digest,
            ["api"] = new JsonObject
            {
                ["host"] = config.Api.Host,
                ["port"] = config.Api.Port,
                ["serviceProfileId"] = config.Api.ServiceProfileId,
                ["serviceTokenRef"] = config.Api.ServiceTokenRef,
                ["projectId"] = config.Api.ProjectId.ToString("D", Formats.Invariant),
            },
            ["telemetry"] = new JsonObject
            {
                ["exporter"] = config.Telemetry.Exporter.ToWire(),
                ["endpoint"] = config.Telemetry.Endpoint?.ToString(),
            },
            ["limits"] = new JsonObject
            {
                ["maxConcurrency"] = config.Limits.MaxConcurrency,
                ["timeoutSeconds"] = config.Limits.TimeoutSeconds,
                ["maxAttempts"] = config.Limits.MaxAttempts,
                ["maxInputBytes"] = config.Limits.MaxInputBytes,
                ["maxContextBytes"] = config.Limits.MaxContextBytes,
                ["maxOutputBytes"] = config.Limits.MaxOutputBytes,
                ["maxCostPerRequest"] = config.Limits.MaxCostPerRequest,
            },
            ["memory"] = new JsonObject
            {
                ["enabled"] = config.Memory.Enabled,
                ["required"] = config.Memory.Required,
                ["embeddingServiceId"] = config.Memory.EmbeddingServiceId,
                ["retentionDays"] = config.Memory.RetentionDays,
                ["configured"] = !config.Memory.IsUnconfigured,
            },
            ["profiles"] = profiles,
            ["registry"] = new JsonObject
            {
                ["revision"] = snapshot.Registry.Revision,
                ["services"] = services,
            },
        };
    }

    private static string Flag(bool value) => value ? "yes" : Ui.Text("no");
}
