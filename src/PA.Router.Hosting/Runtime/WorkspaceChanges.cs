using PA.Router.Session;

namespace PA.Router.Hosting.Runtime;

/// <summary>Che cosa e' cambiato nel recinto, e come tornare indietro.</summary>
/// <param name="Status">Stato in parole: aggiunto, modificato, cancellato…</param>
/// <param name="Path">Percorso relativo alla radice del recinto.</param>
public sealed record WorkspaceChange(string Status, string Path);

/// <summary>
/// Lettura delle modifiche del recinto, come operazione (S61).
///
/// Sta qui e non nel front end per la regola di sempre: il terminale **rende**, non compone
/// (INV-22). E sta qui e non nella CLI anche per una ragione concreta — `GitCommand` e'
/// interno a `PA.Router.Session`, e la CLI non referenzia quel progetto: farglielo referenziare
/// avrebbe aggiunto un arco al grafo delle dipendenze per una comodita' di resa.
///
/// E' una lettura pura: nessun `add`, nessun `reset`, nessun `checkout`, nessuno `stash`.
/// </summary>
public static class WorkspaceChanges
{
    /// <summary>Riferimento delle istantanee del router, per chi deve mostrarlo.</summary>
    public static string CheckpointRef => WorkspaceRepository.CheckpointRef;

    /// <summary>
    /// File cambiati rispetto all'ultima istantanea. Elenco vuoto quando non c'e' nulla da
    /// mostrare, anche se il recinto non e' ancora un repository.
    /// </summary>
    public static async Task<IReadOnlyList<WorkspaceChange>> ListAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        IReadOnlyList<(string Status, string Path)> changes =
            await WorkspaceRepository.DiffAsync(workspaceRoot, cancellationToken).ConfigureAwait(false);

        return [.. changes.Select(change => new WorkspaceChange(change.Status, change.Path))];
    }

    /// <summary>
    /// Comandi con cui l'operatore puo' guardare e, **se decide**, tornare indietro.
    ///
    /// Il router non li esegue: ADR-019 gli vieta `reset`, `checkout` e `stash`, e un comando che
    /// riscrive il lavoro di qualcuno non puo' partire da una riga digitata in fretta. Qui si
    /// consegnano gia' pronti, con la radice giusta, perche' sbagliarli a mano e' facile.
    /// </summary>
    public static IReadOnlyList<(string What, string Command)> RestoreCommands(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        string root = workspaceRoot;

        return
        [
            ("vedere le differenze", "git -C " + root + " diff " + WorkspaceRepository.CheckpointRef + " --"),
            ("tornare all'istantanea", "git -C " + root + " restore --source=" + WorkspaceRepository.CheckpointRef + " -- ."),
            ("istantanea corrente", "git -C " + root + " rev-parse " + WorkspaceRepository.CheckpointRef),
        ];
    }
}
