using System.CommandLine;
using PA.Router.Abstractions;
using PA.Router.Cli.Output;
using PA.Router.Cli.Repl;
using PA.Router.Infrastructure;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Commands;

/// <summary>
/// <c>pa script [file]</c>: il REPL con le righe da un file o dallo standard input (23 settembre 2026).
///
/// E' una resa del REPL, non un secondo motore: le righe attraversano la stessa sessione — classificazione, canale verso
/// il worker, turno, racconto, <c>/debug</c> — che attraversano scritte a mano. Esiste perche' chi prova il router possa
/// fare domande dalle piu' semplici alle piu' complesse e leggere le risposte complete senza un terminale.
/// </summary>
internal static class ScriptCommand
{
    internal static Command Create(Option<DirectoryInfo?> dataOption)
    {
        Argument<FileInfo?> fileArgument = new("file")
        {
            Description = Ui.Text("File with one REPL line per line; standard input when omitted."),
            Arity = ArgumentArity.ZeroOrOne,
        };

        Option<string?> profileOption = new("--profile") { Description = Ui.Text("repl.profileOption") };

        Command command = new("script", Ui.Text("Run REPL lines from a file or standard input, with plain output."))
        {
            dataOption,
            fileArgument,
            profileOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            ConfigurationLoadResult configuration = new ConfigurationLoader(SystemClock.Instance)
                .Load(CommandContext.ResolveDataDirectory(parseResult, dataOption));

            if (!configuration.IsValid)
            {
                return CommandContext.ReportIssues(parseResult, configuration.Issues);
            }

            FileInfo? file = parseResult.GetValue(fileArgument);

            // Il copione si legge tutto: e' breve, e cosi' si toglie il segno d'ordine dei byte che PowerShell mette in testa a
            // un flusso. Rimasto li', la prima riga — di solito un comando — arrivava al modello come domanda.
            // «-» e' lo standard input, come nei comandi comuni; un file che non c'e' si dice, non esce come eccezione non
            // gestita (24 settembre 2026).
            bool fromInput = file is null || file.Name == "-";

            if (!fromInput && !file!.Exists)
            {
                parseResult.InvocationConfiguration.Error.WriteLine(ErrorCode.InputInvalid.ToWire() + ": " + Ui.Format("script.missing {0}", file.FullName));
                return ExitCodes.InvalidInput;
            }

            string lines = fromInput
                ? await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false)
                : await File.ReadAllTextAsync(file!.FullName, cancellationToken).ConfigureAwait(false);
            using TextReader input = new StringReader(lines.TrimStart('﻿'));

            return await ReplSession.RunScriptAsync(
                    configuration.Require(),
                    input,
                    parseResult.InvocationConfiguration.Output,
                    parseResult.InvocationConfiguration.Error,
                    cancellationToken,
                    parseResult.GetValue(profileOption))
                .ConfigureAwait(false);
        });

        return command;
    }
}
