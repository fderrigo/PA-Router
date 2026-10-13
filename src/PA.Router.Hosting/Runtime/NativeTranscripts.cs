using PA.Router.Abstractions.Contracts;

namespace PA.Router.Hosting.Runtime;

/// <summary>
/// I transcript che un agente tiene per conto proprio quando l'operatore gli ha lasciato una sessione nativa
/// (ADR-022). Stanno **fuori dallo store del router e in chiaro**, nella cartella del programma: cancellare una
/// conversazione del router senza toglierli lascerebbe sul disco proprio il contenuto che si voleva rimuovere.
///
/// Si toglie solo cio' che il router ha creato: un file che si chiama come un identificatore **assegnato dal router**
/// e scritto in un turno di quella conversazione. Nessun altro file del programma viene toccato.
/// </summary>
public static class NativeTranscripts
{
    /// <summary>Cartella in cui Claude Code tiene le proprie sessioni, una sottocartella per directory di lavoro.</summary>
    public static string ClaudeCodeRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>
    /// Toglie i transcript nativi dei turni indicati e dice quanti file ha rimosso. Un file che non c'e' piu' non e'
    /// un errore; uno che non si riesce a togliere si conta come rimasto, e chi chiama lo dichiara.
    /// </summary>
    /// <param name="turns">Turni della conversazione che si sta cancellando.</param>
    /// <param name="root">Cartella del programma; quella predefinita quando non e' indicata.</param>
    public static (int Removed, int Left) Remove(IEnumerable<SessionTurn> turns, string? root = null)
    {
        ArgumentNullException.ThrowIfNull(turns);

        string directory = root ?? ClaudeCodeRoot;
        int removed = 0;
        int left = 0;

        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        foreach (string id in turns.Select(turn => turn.NativeSessionId).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            // Solo un UUID diventa un nome di file: un identificatore scritto male non diventa un percorso.
            if (!Guid.TryParseExact(id, "D", out _))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, id + ".jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    left++;
                }
            }
        }

        return (removed, left);
    }
}
