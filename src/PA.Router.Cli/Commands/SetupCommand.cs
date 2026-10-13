using System.CommandLine;
using PA.Router.Abstractions;
using PA.Router.Cli.Repl;

namespace PA.Router.Cli.Commands;

/// <summary>
/// <c>pa setup</c>: il configuratore guidato, lo stesso di <c>/setup</c> e del primo avvio (24 settembre 2026).
///
/// Il REPL aveva <c>/setup</c> e la riga di comando no, mentre `pa registry list` e `pa doctor` dicevano «per cambiare:
/// pa setup»: chi lo digitava finiva sull'aiuto generale. Regola dell'operatore: un comando che esiste come `/nome` nel
/// REPL esiste anche come `pa nome`, e viceversa. Le schermate e le scritture sono quelle di <see cref="SetupScreen"/>;
/// qui c'e' solo l'ingresso.
/// </summary>
internal static class SetupCommand
{
    internal static Command Create(Option<DirectoryInfo?> dataOption)
    {
        Command command = new("setup", Ui.Text("setup.command")) { dataOption };

        command.SetAction(parseResult =>
        {
            // Come /setup: il configuratore ha bisogno di un terminale che risponda ai tasti.
            if (!PaRootCommand.Interactive)
            {
                parseResult.InvocationConfiguration.Error.WriteLine(
                    ErrorCode.InputInvalid.ToWire() + ": " + Ui.Text("setup.notInteractive"));
                return ExitCodes.UsageError;
            }

            return ReplSession.RunSetup(
                CommandContext.ResolveDataDirectory(parseResult, dataOption),
                parseResult.InvocationConfiguration.Output,
                parseResult.InvocationConfiguration.Error);
        });

        return command;
    }
}
