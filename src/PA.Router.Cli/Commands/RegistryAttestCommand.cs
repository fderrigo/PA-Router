using System.CommandLine;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Output;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Commands;

internal static class RegistryAttestCommand
{
    internal static Command Create(Option<DirectoryInfo?> dataOption)
    {
        Argument<string> service = new("service");
        Argument<string> control = new("control");
        Option<string> by = new("--by") { Required = true };
        Option<DateTimeOffset> observed = new("--observed-at") { Required = true };
        // Dal 25 settembre 2026 (ADR-032, fase 4) una conferma non scade: la data resta facoltativa e non decide nulla.
        Option<DateTimeOffset?> expires = new("--expires-at");
        Option<bool> json = new("--json");
        Command command = new("attest", Ui.Text("Record an explicit operator attestation; never enables a service."))
            { service, control, by, observed, expires, json, dataOption };
        command.SetAction(parse =>
        {
            TextWriter output = parse.InvocationConfiguration.Output;
            try
            {
                string directory = CommandContext.ResolveDataDirectory(parse, dataOption);
                var loaded = new ConfigurationLoader(SystemClock.Instance).Load(directory);
                if (!loaded.IsValid)
                {
                    if (parse.GetValue(json)) { CliJson.Write(output, CliJson.Invalid("registry attest", loaded.Issues)); }
                    else { CommandContext.ReportIssues(parse, loaded.Issues); }
                    return ExitCodes.InvalidInput;
                }
                if (!WireEnum.TryParse(parse.GetValue(control)!, out Control property)
                    || !loaded.Require().Registry.Services.Any(item => item.Id == parse.GetValue(service)))
                { throw new ArgumentException(Ui.Text("Servizio o controllo sconosciuto.")); }
                PropertyAttestation attestation = new(parse.GetValue(by)!, parse.GetValue(observed), parse.GetValue(expires), null);
                ServiceAttestations.Validate(attestation, SystemClock.Instance);
                var result = RegistryRewrite.Apply(loaded.Require(), ".before-attest", root =>
                    ServiceAttestations.Apply(root, parse.GetValue(service)!, property, attestation), SystemClock.Instance);
                if (result.Status != RegistryRewriteStatus.Written) { throw new ArgumentException(Ui.Text("Attestazione non scritta: ") + result.Status); }
                if (parse.GetValue(json)) { CliJson.Write(output, CliJson.Ok("registry attest", new() { ["written"] = true })); }
                else { output.WriteLine(Ui.Text("Attestazione salvata; abilitazione invariata.")); }
                return ExitCodes.Success;
            }
            catch (ArgumentException exception)
            {
                if (parse.GetValue(json)) { CliJson.Write(output, CliJson.Error("registry attest", ErrorCode.InputInvalid, exception.Message)); }
                else { parse.InvocationConfiguration.Error.WriteLine(exception.Message); }
                return ExitCodes.InvalidInput;
            }
        });
        return command;
    }
}
