using System.CommandLine;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Output;
using PA.Router.Extensions;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Storage;
using PA.Router.Policy;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Commands;

/// <summary>
/// <c>pa plugin install | list | show | enable | disable | remove</c> e <c>pa skill list | show</c>.
///
/// L'installazione e' l'unica autorizzazione che conta insieme all'abilitazione: nessun pacchetto
/// si attiva perche' e' presente, e nessuna riga di un SKILL.md concede permessi. La UI mostra
/// digest e permessi concreti prima che l'operatore decida (03-specs/PLUGINS-SKILLS.md).
/// </summary>
internal static class ExtensionCommands
{
    internal static Command CreatePlugin(Option<DirectoryInfo?> dataOption) =>
        Group("plugin", Ui.Text("Manage local extension packages."), [
            Install(dataOption),
            List(dataOption),
            Show(dataOption),
            Enable(dataOption),
            Disable(dataOption),
            Remove(dataOption),
        ]);

    internal static Command CreateSkill(Option<DirectoryInfo?> dataOption) =>
        Group("skill", Ui.Text("Inspect the skills offered by installed packages."), [
            SkillList(dataOption),
            SkillShow(dataOption),
        ]);

    private static Command Group(string name, string description, IReadOnlyList<Command> children)
    {
        Command command = new(name, description);

        foreach (Command child in children)
        {
            command.Subcommands.Add(child);
        }

        command.SetAction(parseResult =>
        {
            parseResult.InvocationConfiguration.Output.WriteLine(Ui.Text("Run 'pa ") + name + Ui.Text(" --help' for the available subcommands."));
            return ExitCodes.UsageError;
        });

        return command;
    }

    private static Command Install(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> directoryArgument = new("directory") { Description = Ui.Text("Package directory to install.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("install", Ui.Text("Validate a package and copy its declared files into the local store."))
        {
            dataOption,
            directoryArgument,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;

            return await WithCatalogAsync(parseResult, dataOption, json, "plugin install", async (context) =>
            {
                InstalledPlugin plugin = new PluginInstaller(SystemClock.Instance)
                    .Install(parseResult.GetValue(directoryArgument)!, context.PluginRoot);

                string manifestJson = File.ReadAllText(Path.Combine(plugin.Root, ManifestReader.FileName));

                await context.Catalog.RegisterAsync(
                    plugin.Manifest.Id,
                    plugin.Manifest.Version,
                    plugin.InstallDigest,
                    manifestJson,
                    cancellationToken).ConfigureAwait(false);

                if (json)
                {
                    CliJson.Write(output, CliJson.Ok("plugin install", new JsonObject
                    {
                        ["id"] = plugin.Manifest.Id,
                        ["version"] = plugin.Manifest.Version,
                        ["installDigest"] = plugin.InstallDigest,
                        ["state"] = PluginState.Installed.ToWire(),
                        ["skills"] = new JsonArray([.. plugin.Manifest.Skills.Select(skill => (JsonNode)skill.Id)]),
                    }));

                    return ExitCodes.Success;
                }

                output.WriteLine(Ui.Text("Installato ") + plugin.Manifest.Key + Ui.Text(" (disabilitato)."));
                output.WriteLine("Digest:    " + plugin.InstallDigest);
                output.WriteLine(Ui.Text("Abilitalo con 'pa plugin enable ") + plugin.Manifest.Id + "'.");

                return ExitCodes.Success;
            }).ConfigureAwait(false);
        });

        return command;
    }

    private static Command List(Option<DirectoryInfo?> dataOption)
    {
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("list", Ui.Text("List the installed packages.")) { dataOption, jsonOption };

        command.SetAction(parseResult =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;

            return WithCatalogAsync(parseResult, dataOption, json, "plugin list", context =>
            {
                JsonArray plugins = [];

                // Lo stesso elenco che mostra /plugins nel REPL (INV-22).
                IReadOnlyList<PluginSummary> installed = ExtensionQueries.ListPlugins(context.Catalog, context.Snapshot);

                foreach (PluginSummary plugin in installed)
                {
                    if (json)
                    {
                        plugins.Add(new JsonObject
                        {
                            ["id"] = plugin.Id,
                            ["version"] = plugin.Version,
                            ["state"] = plugin.State.ToWire(),
                            ["installDigest"] = plugin.InstallDigest,
                            ["enabledHere"] = plugin.EnabledHere,
                        });

                        continue;
                    }

                    output.WriteLine(
                        plugin.Id + "@" + plugin.Version + "  " + plugin.State.ToWire()
                        + Ui.Text("  abilitato qui: ") + (plugin.EnabledHere ? Ui.Text("si") : Ui.Text("no")));
                }

                if (json)
                {
                    CliJson.Write(output, CliJson.Ok("plugin list", new JsonObject { ["plugins"] = plugins }));
                }
                else if (installed.Count == 0)
                {
                    // Prima si contava l'array JSON, che in modo testo resta vuoto: la frase
                    // compariva anche sotto un elenco di pacchetti installati.
                    output.WriteLine(Ui.Text("Nessun pacchetto installato."));
                }

                return Task.FromResult(ExitCodes.Success);
            }, readOnly: true).GetAwaiter().GetResult();
        });

        return command;
    }

    private static Command Show(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("plugin-id") { Description = Ui.Text("Package to inspect.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("show", Ui.Text("Show a package manifest, digest and permissions."))
        {
            dataOption,
            idArgument,
            jsonOption,
        };

        command.SetAction(parseResult =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;

            return WithCatalogAsync(parseResult, dataOption, json, "plugin show", context =>
            {
                if (Find(context, parseResult.GetValue(idArgument)!) is not (PluginRecord record, PluginManifest manifest))
                {
                    return Task.FromResult(Fail(parseResult, json, "plugin show", ErrorCode.InputInvalid,
                        Ui.Text("That package is not installed."), ExitCodes.InvalidInput));
                }

                if (json)
                {
                    CliJson.Write(output, CliJson.Ok("plugin show", new JsonObject
                    {
                        ["id"] = record.Id,
                        ["version"] = record.Version,
                        ["state"] = record.State.ToWire(),
                        ["installDigest"] = record.InstallDigest,
                        ["description"] = manifest.Description,
                        ["files"] = new JsonArray([.. manifest.Files.Select(file => (JsonNode)new JsonObject
                        {
                            ["path"] = file.Path,
                            ["sha256"] = file.Sha256,
                        })]),
                        ["skills"] = new JsonArray([.. manifest.Skills.Select(skill => (JsonNode)new JsonObject
                        {
                            ["id"] = skill.Id,
                            ["description"] = skill.Description,
                        })]),
                        ["nativeBindings"] = new JsonArray([.. manifest.NativeBindings.Select(binding => (JsonNode)new JsonObject
                        {
                            ["provider"] = binding.Provider,
                            ["pluginId"] = binding.PluginId,
                            // L'intervallo dichiarato non e' una prova di compatibilita'.
                            ["versionRange"] = binding.VersionRange,
                            ["certified"] = false,
                        })]),
                    }));

                    return Task.FromResult(ExitCodes.Success);
                }

                output.WriteLine(record.Id + "@" + record.Version + "  " + record.State.ToWire());
                output.WriteLine("Digest:   " + record.InstallDigest);

                foreach (PluginSkill skill in manifest.Skills)
                {
                    output.WriteLine("Skill:    " + skill.Id + " — " + skill.Description);
                }

                foreach (NativeBinding binding in manifest.NativeBindings)
                {
                    output.WriteLine(Ui.Text("Binding nativo: ") + binding.Provider + "/" + binding.PluginId + Ui.Text(" — non certificato"));
                }

                return Task.FromResult(ExitCodes.Success);
            }, readOnly: true).GetAwaiter().GetResult();
        });

        return command;
    }

    private static Command Enable(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("plugin-id") { Description = Ui.Text("Package to enable.") };

        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("enable", Ui.Text("Enable a package in this project."))
        {
            dataOption,
            idArgument,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;

            return await WithCatalogAsync(parseResult, dataOption, json, "plugin enable", async context =>
            {
                if (Find(context, parseResult.GetValue(idArgument)!) is not (PluginRecord record, PluginManifest _))
                {
                    return Fail(parseResult, json, "plugin enable", ErrorCode.InputInvalid,
                        Ui.Text("That package is not installed."), ExitCodes.InvalidInput);
                }

                // I file vengono riverificati adesso: fra installazione e abilitazione potrebbero
                // essere stati sostituiti.
                PluginInstaller.Verify(Path.Combine(context.PluginRoot, record.Id, record.Version), record.InstallDigest);

                await context.Catalog.EnableAsync(record.InstallDigest, context.ProjectId, cancellationToken)
                    .ConfigureAwait(false);

                if (json)
                {
                    CliJson.Write(output, CliJson.Ok("plugin enable", new JsonObject
                    {
                        ["id"] = record.Id,
                        ["version"] = record.Version,
                        ["installDigest"] = record.InstallDigest,
                        ["enabledHere"] = true,
                    }));

                    return ExitCodes.Success;
                }

                // Abilitare e' tutto cio' che c'e' da concedere: un pacchetto porta solo testo di
                // skill, e il grant dice che l'operatore lo ammette in questo progetto.
                output.WriteLine(Ui.Text("Abilitato ") + record.Id + "@" + record.Version + Ui.Text(" in questo progetto."));

                return ExitCodes.Success;
            }).ConfigureAwait(false);
        });

        return command;
    }

    private static Command Disable(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("plugin-id") { Description = Ui.Text("Package to disable.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("disable", Ui.Text("Revoke the grants of a package immediately."))
        {
            dataOption,
            idArgument,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);

            return await WithCatalogAsync(parseResult, dataOption, json, "plugin disable", async context =>
            {
                if (Find(context, parseResult.GetValue(idArgument)!) is not (PluginRecord record, PluginManifest _))
                {
                    return Fail(parseResult, json, "plugin disable", ErrorCode.InputInvalid,
                        Ui.Text("That package is not installed."), ExitCodes.InvalidInput);
                }

                await context.Catalog.DisableAsync(record.InstallDigest, cancellationToken).ConfigureAwait(false);

                if (json)
                {
                    CliJson.Write(parseResult.InvocationConfiguration.Output, CliJson.Ok("plugin disable", new JsonObject
                    {
                        ["id"] = record.Id,
                        ["state"] = PluginState.Disabled.ToWire(),
                    }));

                    return ExitCodes.Success;
                }

                parseResult.InvocationConfiguration.Output.WriteLine(
                    Ui.Text("Disabilitato ") + record.Id + Ui.Text(": i grant sono revocati per le nuove chiamate."));

                return ExitCodes.Success;
            }).ConfigureAwait(false);
        });

        return command;
    }

    private static Command Remove(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("plugin-id") { Description = Ui.Text("Package to remove.") };
        Option<bool> confirmOption = new("--yes") { Description = Ui.Text("Confirm the removal without prompting.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("remove", Ui.Text("Remove a package from the catalogue and the store."))
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
                return Fail(parseResult, json, "plugin remove", ErrorCode.InputInvalid,
                    Ui.Text("Removing a package is irreversible: confirm with --yes."), ExitCodes.UsageError);
            }

            return await WithCatalogAsync(parseResult, dataOption, json, "plugin remove", async context =>
            {
                if (Find(context, parseResult.GetValue(idArgument)!) is not (PluginRecord record, PluginManifest _))
                {
                    return Fail(parseResult, json, "plugin remove", ErrorCode.InputInvalid,
                        Ui.Text("That package is not installed."), ExitCodes.InvalidInput);
                }

                await context.Catalog.RemoveAsync(record.InstallDigest, cancellationToken).ConfigureAwait(false);

                string root = Path.Combine(context.PluginRoot, record.Id, record.Version);

                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                if (json)
                {
                    CliJson.Write(parseResult.InvocationConfiguration.Output, CliJson.Ok("plugin remove", new JsonObject
                    {
                        ["id"] = record.Id,
                        ["version"] = record.Version,
                        // La memoria del progetto non viene toccata: e' una decisione separata.
                        ["memoryRemoved"] = false,
                    }));

                    return ExitCodes.Success;
                }

                parseResult.InvocationConfiguration.Output.WriteLine(
                    Ui.Text("Rimosso ") + record.Id + "@" + record.Version + Ui.Text("; la memoria del progetto resta."));

                return ExitCodes.Success;
            }).ConfigureAwait(false);
        });

        return command;
    }

    private static Command SkillList(Option<DirectoryInfo?> dataOption)
    {
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("list", Ui.Text("List the skills of the enabled packages.")) { dataOption, jsonOption };

        command.SetAction(parseResult =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;

            return WithCatalogAsync(parseResult, dataOption, json, "skill list", context =>
            {
                JsonArray skills = [];

                IReadOnlyList<SkillSummary> offered = ExtensionQueries.ListSkills(context.Catalog);

                foreach (SkillSummary skill in offered)
                {
                    if (json)
                    {
                        skills.Add(new JsonObject
                        {
                            ["id"] = skill.SkillId,
                            ["plugin"] = skill.PluginId,
                            ["state"] = skill.State.ToWire(),
                            ["description"] = skill.Description,
                        });

                        continue;
                    }

                    output.WriteLine(skill.SkillId + "  (" + skill.PluginId + ", " + skill.State.ToWire() + ")  " + skill.Description);
                }

                if (json)
                {
                    CliJson.Write(output, CliJson.Ok("skill list", new JsonObject { ["skills"] = skills }));
                }
                else if (offered.Count == 0)
                {
                    output.WriteLine(Ui.Text("Nessuna skill disponibile."));
                }

                return Task.FromResult(ExitCodes.Success);
            }, readOnly: true).GetAwaiter().GetResult();
        });

        return command;
    }

    private static Command SkillShow(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> idArgument = new("skill-id") { Description = Ui.Text("Skill to read.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("show", Ui.Text("Show the instructions of a skill without running anything."))
        {
            dataOption,
            idArgument,
            jsonOption,
        };

        command.SetAction(parseResult =>
        {
            bool json = parseResult.GetValue(jsonOption);
            TextWriter output = parseResult.InvocationConfiguration.Output;
            string skillId = parseResult.GetValue(idArgument)!;

            return WithCatalogAsync(parseResult, dataOption, json, "skill show", context =>
            {
                if (context.Resolve(skillId) is not (InstalledPlugin plugin, PluginSkill _))
                {
                    return Task.FromResult(Fail(parseResult, json, "skill show", ErrorCode.InputInvalid,
                        Ui.Text("That skill is not available."), ExitCodes.InvalidInput));
                }

                LoadedSkill loaded = SkillLoader.Load(plugin, skillId);

                if (json)
                {
                    CliJson.Write(output, CliJson.Ok("skill show", new JsonObject
                    {
                        ["id"] = loaded.SkillId,
                        ["plugin"] = loaded.PluginId,
                        ["installDigest"] = loaded.InstallDigest,
                        ["bytes"] = loaded.Bytes,
                        ["instructions"] = loaded.Instructions,
                    }));

                    return Task.FromResult(ExitCodes.Success);
                }

                output.WriteLine(loaded.Instructions);

                return Task.FromResult(ExitCodes.Success);
            }, readOnly: true).GetAwaiter().GetResult();
        });

        return command;
    }

    /// <summary>
    /// Contesto condiviso dai comandi: catalogo, radice dei pacchetti, progetto e configurazione.
    /// Lo snapshot serve a calcolare i permessi effettivi, che dipendono dal profilo.
    /// </summary>
    internal sealed record CatalogContext(
        SqlitePluginCatalog Catalog,
        string PluginRoot,
        Guid ProjectId,
        ConfigurationSnapshot Snapshot)
    {
        /// <summary>Trova la skill fra i pacchetti installati e la verifica prima dell'uso.</summary>
        internal (InstalledPlugin Plugin, PluginSkill Skill)? Resolve(string skillId)
        {
            foreach (PluginRecord record in Catalog.List())
            {
                PluginManifest manifest = ManifestReader.Read(record.ManifestJson);

                foreach (PluginSkill skill in manifest.Skills)
                {
                    if (!string.Equals(skill.Id, skillId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string root = Path.Combine(PluginRoot, record.Id, record.Version);

                    PluginInstaller.Verify(root, record.InstallDigest);

                    return (
                        new InstalledPlugin(manifest, record.InstallDigest, record.State, root, record.InstalledAt),
                        skill);
                }
            }

            return null;
        }
    }

    private static (PluginRecord Record, PluginManifest Manifest)? Find(CatalogContext context, string pluginId)
    {
        foreach (PluginRecord record in context.Catalog.List())
        {
            if (string.Equals(record.Id, pluginId, StringComparison.Ordinal))
            {
                return (record, ManifestReader.Read(record.ManifestJson));
            }
        }

        return null;
    }

    /// <param name="readOnly">
    /// Vero per i comandi che si limitano a leggere: non prendono la proprieta' dello store, cosi'
    /// funzionano anche mentre un worker o l'host API lo possiedono. Chi scrive la pretende.
    /// </param>
    private static async Task<int> WithCatalogAsync(
        ParseResult parseResult,
        Option<DirectoryInfo?> dataOption,
        bool json,
        string command,
        Func<CatalogContext, Task<int>> body,
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

        if (!ExtensionQueries.Available(snapshot))
        {
            return Fail(parseResult, json, command, ErrorCode.ExtensionPermissionDenied,
                Ui.Text("Extensions are disabled in the configuration."), ExitCodes.PolicyDenied);
        }

        string pluginRoot = Path.Combine(snapshot.DataDirectory, snapshot.Config.Extensions.PluginRoot);

        try
        {
            // L'apertura sta dentro il try: con la directory gia' posseduta il comando risponde con
            // il proprio codice invece di terminare con una traccia di stack.
            await using RouterStack runtime = RouterStack.Open(snapshot, ownsStore: !readOnly);

            if (!readOnly)
            {
                // Il grant referenzia il progetto: senza la riga corrispondente la scrittura
                // violerebbe la chiave esterna, e l'operatore vedrebbe un errore di storage al posto
                // di un permesso.
                await runtime.Store.EnsureProjectAsync(snapshot.Config.Api.ProjectId, "local", CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return await body(new CatalogContext(
                runtime.PluginCatalog(),
                pluginRoot,
                snapshot.Config.Api.ProjectId,
                snapshot)).ConfigureAwait(false);
        }
        catch (PaRouterException ex)
        {
            return Fail(parseResult, json, command, ex.Code, ex.Message, ExitCodes.ForStorage(ex.Code));
        }
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
            return exitCode;
        }

        parseResult.InvocationConfiguration.Error.WriteLine(code.ToWire() + ": " + message);

        return exitCode;
    }
}
