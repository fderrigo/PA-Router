using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Output;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Commands;

/// <summary>
/// <c>pa tools</c>: attestazioni e revoche dell'operatore sugli strumenti delle applicazioni (ADR-020).
///
/// La verifica e la scrittura stanno in <see cref="ToolAdministration"/> e <see cref="RegistryRewrite"/>: qui
/// c'e' soltanto la resa (INV-22).
/// </summary>
internal static class ToolsCommand
{
    internal static Command Create() =>
        new("tools", Ui.Text("Attest or revoke the tools of external applications."))
        {
            CreateAttest(),
            CreateRevoke(),
            CreateList(),
        };

    private static Command CreateAttest()
    {
        Option<DirectoryInfo?> dataOption = CommandContext.DataOption();
        Argument<string> sourceArgument = new("source") { Description = Ui.Text("Configured source id, or the url of an application source.") };
        Argument<string> toolArgument = new("tool") { Description = Ui.Text("Tool name in the source.") };
        Option<string> fingerprintOption = new("--fingerprint") { Description = Ui.Text("Fingerprint the attestation refers to (sha256:...)."), Required = true };
        Option<string> acceptsOption = new("--accepts") { Description = Ui.Text("Highest data class the tool accepts: public, internal, personal or sensitive."), Required = true };
        Option<string> returnsOption = new("--returns") { Description = Ui.Text("Data class of what the tool returns."), Required = true };
        Option<string[]> regionOption = new("--region") { Description = Ui.Text("Where the tool processes data; repeat for more regions.") };
        Option<string> expiresOption = new("--expires") { Description = Ui.Text("Expiry of the attestation, ISO 8601."), Required = true };
        Option<string?> placementOption = new("--placement") { Description = Ui.Text("Placement, when it differs from the observed one.") };
        Option<string> byOption = new("--by") { Description = Ui.Text("Who attests."), Required = true };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("attest", Ui.Text("Attest what a tool accepts, returns and where it processes data."))
        {
            dataOption,
            sourceArgument,
            toolArgument,
            fingerprintOption,
            acceptsOption,
            returnsOption,
            regionOption,
            expiresOption,
            placementOption,
            byOption,
            jsonOption,
        };

        command.SetAction(parseResult =>
        {
            if (Load(parseResult, dataOption, "tools attest", jsonOption) is not ConfigurationSnapshot snapshot)
            {
                return ExitCodes.InvalidInput;
            }

            ToolAttestationConfig planned;

            try
            {
                planned = ToolAdministration.PlanAttestation(
                    snapshot,
                    parseResult.GetValue(sourceArgument)!,
                    parseResult.GetValue(toolArgument)!,
                    parseResult.GetValue(fingerprintOption)!,
                    parseResult.GetValue(acceptsOption)!,
                    parseResult.GetValue(returnsOption)!,
                    parseResult.GetValue(regionOption) ?? [],
                    parseResult.GetValue(expiresOption)!,
                    parseResult.GetValue(placementOption),
                    parseResult.GetValue(byOption)!,
                    DateTimeOffset.UtcNow);
            }
            catch (PaRouterException exception)
            {
                return Refused(parseResult, "tools attest", jsonOption, exception);
            }

            RegistryRewriteResult result = RegistryRewrite.ApplyToConfiguration(
                snapshot,
                ToolAdministration.BackupSuffix,
                config => ToolAdministration.ApplyAttestation(config, planned),
                SystemClock.Instance);

            return Written(parseResult, "tools attest", jsonOption, result, planned.Source, planned.Tool,
                Ui.Text("Attestazione scritta per ") + planned.Tool + Ui.Text(" di ") + planned.Source + Ui.Text(", valida fino al ")
                + planned.ExpiresAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".");
        });

        return command;
    }

    private static Command CreateRevoke()
    {
        Option<DirectoryInfo?> dataOption = CommandContext.DataOption();
        Argument<string> sourceArgument = new("source") { Description = Ui.Text("Configured source id, or the url of an application source.") };
        Argument<string> toolArgument = new("tool") { Description = Ui.Text("Tool name in the source.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("revoke", Ui.Text("Revoke a tool: it is not used, whatever an application declares."))
        {
            dataOption,
            sourceArgument,
            toolArgument,
            jsonOption,
        };

        command.SetAction(parseResult =>
        {
            if (Load(parseResult, dataOption, "tools revoke", jsonOption) is not ConfigurationSnapshot snapshot)
            {
                return ExitCodes.InvalidInput;
            }

            string source = parseResult.GetValue(sourceArgument)!;
            string tool = parseResult.GetValue(toolArgument)!;

            try
            {
                ToolAdministration.PlanRevocation(snapshot, source, tool);
            }
            catch (PaRouterException exception)
            {
                return Refused(parseResult, "tools revoke", jsonOption, exception);
            }

            RegistryRewriteResult result = RegistryRewrite.ApplyToConfiguration(
                snapshot,
                ToolAdministration.BackupSuffix,
                config => ToolAdministration.ApplyRevocation(config, source, tool),
                SystemClock.Instance);

            return Written(parseResult, "tools revoke", jsonOption, result, source, tool,
                Ui.Text("Strumento ") + tool + Ui.Text(" di ") + source + Ui.Text(" revocato: non si usa, qualunque cosa dichiari un'applicazione."));
        });

        return command;
    }

    private static Command CreateList()
    {
        Option<DirectoryInfo?> dataOption = CommandContext.DataOption();
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("list", Ui.Text("List tool sources, attestations and revocations."))
        {
            dataOption,
            jsonOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (Load(parseResult, dataOption, "tools list", jsonOption) is not ConfigurationSnapshot snapshot)
            {
                return ExitCodes.InvalidInput;
            }

            IReadOnlyList<StoredToolAttestation> registered;

            await using (RouterStack readOnly = RouterStack.Open(snapshot, ownsStore: false))
            {
                registered = readOnly.ToolAttestations().List(snapshot.Config.Api.ProjectId);
            }

            ToolsConfig? tools = snapshot.Config.Tools;
            TextWriter output = parseResult.InvocationConfiguration.Output;

            if (parseResult.GetValue(jsonOption))
            {
                CliJson.Write(output, CliJson.Ok("tools list", new JsonObject
                {
                    ["enabled"] = tools?.Enabled ?? false,
                    ["sources"] = new JsonArray([.. (tools?.Sources ?? []).Select(source => (JsonNode?)new JsonObject
                    {
                        ["id"] = source.Id,
                        ["kind"] = source.Kind.ToWire(),
                        ["url"] = source.Url?.AbsoluteUri,
                    })]),
                    ["attestations"] = new JsonArray([.. (tools?.Attestations ?? []).Select(attestation => (JsonNode?)Entry(
                        attestation.Source, attestation.Tool, attestation.Fingerprint, attestation.Accepts, attestation.Returns, attestation.ExpiresAt, "operator"))]),
                    ["registered"] = new JsonArray([.. registered.Select(attestation => (JsonNode?)Entry(
                        attestation.SourceKey, attestation.Tool, attestation.Fingerprint, attestation.Accepts, attestation.Returns, attestation.ExpiresAt, "application"))]),
                    ["revoked"] = new JsonArray([.. (tools?.Revocations ?? []).Select(revocation => (JsonNode?)new JsonObject
                    {
                        ["source"] = revocation.Source,
                        ["tool"] = revocation.Tool,
                    })]),
                }));

                return ExitCodes.Success;
            }

            output.WriteLine(Ui.Text("Strumenti: ") + (tools?.Enabled == true ? Ui.Text("accesi") : Ui.Text("spenti")));

            foreach (ToolSourceConfig source in tools?.Sources ?? [])
            {
                output.WriteLine(Ui.Text("  sorgente ") + source.Id + " (" + source.Kind.ToWire() + ") " + (source.Url?.AbsoluteUri ?? source.Command));
            }

            foreach (ToolAttestationConfig attestation in tools?.Attestations ?? [])
            {
                output.WriteLine(Ui.Text("  attestato dall'operatore: ") + attestation.Tool + Ui.Text(" di ") + attestation.Source
                    + Ui.Text(", accetta ") + attestation.Accepts.ToWire() + Ui.Text(", restituisce ") + attestation.Returns.ToWire()
                    + Ui.Text(", fino al ") + attestation.ExpiresAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            foreach (StoredToolAttestation attestation in registered)
            {
                output.WriteLine(Ui.Text("  dichiarato da un'applicazione: ") + attestation.Tool + Ui.Text(" di ") + attestation.SourceKey
                    + Ui.Text(", accetta ") + attestation.Accepts.ToWire() + Ui.Text(", restituisce ") + attestation.Returns.ToWire()
                    + Ui.Text(", fino al ") + attestation.ExpiresAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            foreach (ToolRevocation revocation in tools?.Revocations ?? [])
            {
                output.WriteLine(Ui.Text("  revocato: ") + revocation.Tool + Ui.Text(" di ") + revocation.Source);
            }

            return ExitCodes.Success;
        });

        return command;
    }

    private static JsonObject Entry(
        string source, string tool, string fingerprint, DataClass accepts, DataClass returns, DateTimeOffset expiresAt, string by) =>
        new()
        {
            ["source"] = source,
            ["tool"] = tool,
            ["fingerprint"] = fingerprint,
            ["accepts"] = accepts.ToWire(),
            ["returns"] = returns.ToWire(),
            ["expiresAt"] = expiresAt.ToString("O", CultureInfo.InvariantCulture),
            ["declaredBy"] = by,
        };

    private static ConfigurationSnapshot? Load(ParseResult parseResult, Option<DirectoryInfo?> dataOption, string name, Option<bool> jsonOption)
    {
        ConfigurationLoadResult configuration = new ConfigurationLoader(SystemClock.Instance)
            .Load(CommandContext.ResolveDataDirectory(parseResult, dataOption));

        if (configuration.IsValid)
        {
            return configuration.Require();
        }

        if (parseResult.GetValue(jsonOption))
        {
            CliJson.Write(parseResult.InvocationConfiguration.Output, CliJson.Invalid(name, configuration.Issues));
        }
        else
        {
            CommandContext.ReportIssues(parseResult, configuration.Issues);
        }

        return null;
    }

    private static int Refused(ParseResult parseResult, string name, Option<bool> jsonOption, PaRouterException exception)
    {
        string message = exception.Message
            + (exception.Metadata.TryGetValue("field", out string? field) ? " (" + field + ")" : string.Empty);

        if (parseResult.GetValue(jsonOption))
        {
            CliJson.Write(parseResult.InvocationConfiguration.Output, CliJson.Error(name, exception.Code, message));
        }
        else
        {
            parseResult.InvocationConfiguration.Error.WriteLine(message);
        }

        return ExitCodes.InvalidInput;
    }

    private static int Written(
        ParseResult parseResult,
        string name,
        Option<bool> jsonOption,
        RegistryRewriteResult result,
        string source,
        string tool,
        string message)
    {
        bool json = parseResult.GetValue(jsonOption);
        TextWriter output = parseResult.InvocationConfiguration.Output;

        if (result.Status is RegistryRewriteStatus.Missing or RegistryRewriteStatus.Unreadable or RegistryRewriteStatus.Restored)
        {
            if (json)
            {
                CliJson.Write(output, CliJson.Invalid(name, result.Issues));
            }
            else
            {
                parseResult.InvocationConfiguration.Error.WriteLine(Ui.Text("La configurazione non e' stata cambiata: ") + result.Status + ".");
                CommandContext.ReportIssues(parseResult, result.Issues);
            }

            return ExitCodes.InvalidInput;
        }

        bool written = result.Status is RegistryRewriteStatus.Written;

        if (json)
        {
            CliJson.Write(output, CliJson.Ok(name, new JsonObject { ["source"] = source, ["tool"] = tool, ["written"] = written }));
            return ExitCodes.Success;
        }

        output.WriteLine(written ? message : Ui.Text("Nulla da cambiare."));

        if (written)
        {
            output.WriteLine(Ui.Text("L'originale resta accanto con il suffisso ") + ToolAdministration.BackupSuffix + Ui.Text("; vale dalla richiesta successiva."));
        }

        return ExitCodes.Success;
    }
}
