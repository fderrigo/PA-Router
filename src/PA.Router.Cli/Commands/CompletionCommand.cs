using System.CommandLine;
using System.Text;
using PA.Router.Cli.Output;

namespace PA.Router.Cli.Commands;

/// <summary>
/// <c>pa completion &lt;shell&gt;</c>: lo script di completamento per una shell (S64).
///
/// Lo script viene **generato da questo comando**, non dalla libreria: nella versione di
/// System.CommandLine bloccata in `Directory.Packages.props` non esiste una generazione di
/// completamenti da esporre, e attribuirgliela sarebbe una dichiarazione falsa in un documento.
/// Quello che c'e' qui e' semplice di proposito — i nomi dei comandi e delle opzioni, letti
/// dall'albero del parser — e non pretende di completare valori.
///
/// I nomi arrivano dal **parser vero**: un elenco scritto a mano finirebbe per proporre un comando
/// che non esiste piu', che e' peggio di nessun completamento. E' la stessa regola del completamento
/// del REPL.
///
/// Lo script si stampa e basta: non viene installato, non tocca il profilo della shell e non scrive
/// niente sul disco. Dove metterlo lo decide chi possiede quella shell.
/// </summary>
internal static class CompletionCommand
{
    /// <summary>Shell riconosciute.</summary>
    private static readonly string[] Shells = ["bash", "zsh", "powershell"];

    internal static Command Create()
    {
        Argument<string> shellArgument = new("shell")
        {
            Description = Ui.Text("Shell to generate the script for: bash, zsh or powershell."),
        };

        Command command = new("completion", Ui.Text("Print the shell completion script for pa.")) { shellArgument };

        command.SetAction(parseResult =>
        {
            string shell = (parseResult.GetValue(shellArgument) ?? string.Empty).Trim().ToLowerInvariant();

            if (!Shells.Contains(shell))
            {
                parseResult.InvocationConfiguration.Error.WriteLine(
                    Ui.Text("Shell non riconosciuta: ") + shell + Ui.Text(". Valori ammessi: ") + string.Join(", ", Shells) + ".");

                return ExitCodes.UsageError;
            }

            // L'albero si costruisce adesso: i nomi sono quelli che questo binario espone davvero.
            IReadOnlyList<string> commands = [.. Names(PaRootCommand.Create())];

            parseResult.InvocationConfiguration.Output.WriteLine(Script(shell, commands));

            return ExitCodes.Success;
        });

        return command;
    }

    /// <summary>Nomi dei comandi di primo livello, in ordine stabile.</summary>
    private static IEnumerable<string> Names(Command root) =>
        root.Subcommands.Select(child => child.Name).Order(StringComparer.Ordinal);

    /// <summary>
    /// Lo script per la shell indicata.
    ///
    /// Completa i comandi di primo livello. I sottocomandi e i valori delle opzioni restano fuori:
    /// prometterli senza generarli sarebbe una promessa che la riga di comando non mantiene.
    /// </summary>
    private static string Script(string shell, IReadOnlyList<string> commands)
    {
        string list = string.Join(' ', commands);

        return shell switch
        {
            "bash" => new StringBuilder()
                .AppendLine("# Completamento di pa per bash. Generato da 'pa completion bash'.")
                .AppendLine("# Aggiungilo al tuo profilo con:  source <(pa completion bash)")
                .AppendLine("_pa_completions() {")
                .AppendLine("  local current=\"${COMP_WORDS[COMP_CWORD]}\"")
                .AppendLine("  if [ \"$COMP_CWORD\" -eq 1 ]; then")
                .AppendLine("    COMPREPLY=( $(compgen -W \"" + list + "\" -- \"$current\") )")
                .AppendLine("  fi")
                .AppendLine("}")
                .AppendLine("complete -F _pa_completions pa")
                .ToString(),

            "zsh" => new StringBuilder()
                .AppendLine("# Completamento di pa per zsh. Generato da 'pa completion zsh'.")
                .AppendLine("# Aggiungilo al tuo profilo con:  source <(pa completion zsh)")
                .AppendLine("_pa() {")
                .AppendLine("  local -a commands")
                .AppendLine("  commands=(" + list + ")")
                .AppendLine("  if (( CURRENT == 2 )); then")
                .AppendLine("    compadd -- $commands")
                .AppendLine("  fi")
                .AppendLine("}")
                .AppendLine("compdef _pa pa")
                .ToString(),

            _ => new StringBuilder()
                .AppendLine("# Completamento di pa per PowerShell. Generato da 'pa completion powershell'.")
                .AppendLine("# Aggiungilo al profilo con:  pa completion powershell | Out-String | Invoke-Expression")
                .AppendLine("Register-ArgumentCompleter -Native -CommandName pa -ScriptBlock {")
                .AppendLine("  param($wordToComplete, $commandAst, $cursorPosition)")
                .AppendLine("  $commands = @('" + string.Join("','", commands) + "')")
                .AppendLine("  $commands |")
                .AppendLine("    Where-Object { $_ -like \"$wordToComplete*\" } |")
                .AppendLine("    ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }")
                .AppendLine("}")
                .ToString(),
        };
    }
}
