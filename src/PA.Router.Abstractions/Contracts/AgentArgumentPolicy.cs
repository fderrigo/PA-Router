namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Argomenti vietati, per famiglia di programma.
///
/// La lista vive nel codice e non nella configurazione: un argomento che rimuove approvazioni,
/// sandbox o fiducia negli hook non e' una preferenza dell'operatore, e un file non deve poterlo
/// concedere. Gli argomenti elencati sono quelli **osservati** nell'help delle versioni installate
/// (evidence/S13/ADAPTER-COMPATIBILITY.md): non si vieta cio' che non esiste, e non si inventa cio'
/// che non e' stato visto.
///
/// Un servizio che ne dichiara uno viene rifiutato **alla lettura della configurazione**, non al
/// momento dell'esecuzione: un file sbagliato deve fallire quando lo si legge, non quando ha gia'
/// avviato un processo (03-specs/NESTED-AGENTS.md).
/// </summary>
public static class AgentArgumentPolicy
{
    private static readonly IReadOnlyList<string> ClaudeCode =
        [
            "--dangerously-skip-permissions",
            "--allow-dangerously-skip-permissions",

            // La sessione nativa la assegna il router e nessun altro (ADR-022). «Riprendi l'ultima» o un
            // identificatore scritto a mano riprenderebbero la conversazione di qualcun altro, e spegnere o
            // accendere la conservazione da qui scavalcherebbe la scelta dichiarata nel registro.
            "--continue",
            "-c",
            "--resume",
            "-r",
            "--session-id",
            "--fork-session",
            "--from-pr",
            "--teleport",
            "--no-session-persistence",
        ];

    private static readonly IReadOnlyList<string> Codex =
        [
            "--dangerously-bypass-approvals-and-sandbox",
            "--dangerously-bypass-hook-trust",
            "danger-full-access",

            // Concederebbe approvazioni automatiche alle azioni del modello.
            "--approve-for-me",

            // Riprendere «l'ultima» sessione significa riprendere quella di chiunque l'abbia lasciata li' (S15).
            "--last",
            "resume",
        ];

    private static readonly IReadOnlyList<string> OpenCode =
        [
            // Esegue le azioni senza chiedere: e' la stessa rimozione di approvazioni delle altre
            // famiglie, con un altro nome.
            "--auto",

            // Pubblica la sessione su un collegamento esterno: un turno riservato non si condivide.
            "--share",
        ];

    /// <summary>Argomenti vietati per la famiglia indicata; insieme vuoto per chi non ne ha.</summary>
    public static IReadOnlyList<string> ForbiddenFor(AdapterKind adapter) => adapter switch
    {
        AdapterKind.ClaudeCodeCli => ClaudeCode,
        AdapterKind.CodexCli => Codex,
        AdapterKind.OpenCodeCli => OpenCode,
        _ => [],
    };

    /// <summary>
    /// Primo argomento vietato presente fra quelli dichiarati, oppure null.
    ///
    /// Il confronto guarda anche la forma <c>--opzione=valore</c> e il valore di un'opzione: su
    /// Codex <c>danger-full-access</c> non e' un'opzione ma un **valore** di <c>--sandbox</c>, e
    /// cercare solo fra i nomi lo lascerebbe passare.
    /// </summary>
    public static string? FirstForbidden(AdapterKind adapter, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        IReadOnlyList<string> forbidden = ForbiddenFor(adapter);

        if (forbidden.Count == 0)
        {
            return null;
        }

        foreach (string argument in arguments)
        {
            foreach (string candidate in forbidden)
            {
                if (argument.Equals(candidate, StringComparison.OrdinalIgnoreCase)
                    || argument.StartsWith(candidate + "=", StringComparison.OrdinalIgnoreCase)
                    || argument.EndsWith("=" + candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
