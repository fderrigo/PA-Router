using System.Text;
using PA.Router.Abstractions;

namespace PA.Router.Session;

/// <summary>Esito della preparazione di un workspace alla scrittura.</summary>
public enum WorkspacePreparation
{
    /// <summary>Repository esistente, senza modifiche che non vengano da un turno del router.</summary>
    Ready,

    /// <summary>La cartella non era un repository: e' diventata tale, con un'istantanea iniziale.</summary>
    Initialized,
}

/// <summary>
/// La cartella in cui un agente scrive e' un repository git: e' un vincolo di progetto (ADR-019).
///
/// Prima di una scrittura: se la cartella non e' un repository lo diventa, con un'istantanea
/// iniziale; se lo e', il suo contenuto deve coincidere con l'ultimo commit o con l'ultima istantanea
/// del router. Modifiche che non vengono da un turno — lavoro dell'operatore non salvato — fermano
/// l'agente: il router non le salva al posto suo, perche' poi non si saprebbe piu' che cosa annullare.
///
/// Dopo una scrittura: un'istantanea di cio' che l'agente ha lasciato. Le istantanee vivono in
/// <see cref="CheckpointRef"/>, **mai** sui rami dell'utente, e il router non fa reset, checkout,
/// stash o push. Tornare indietro e' una decisione dell'operatore, con git.
///
/// L'istantanea serve a tornare indietro, non e' un controllo di sicurezza: un agente che scrive nel
/// workspace puo' scrivere anche in <c>.git</c>. Cio' che il router garantisce e' che Git, lanciato
/// da lui, non esegua nulla di cio' che quel <c>.git</c> contiene (<see cref="GitCommand"/>).
/// </summary>
public static class WorkspaceRepository
{
    /// <summary>Riferimento delle istantanee del router.</summary>
    public const string CheckpointRef = "refs/pa-router/checkpoint";

    /// <summary>Prepara il workspace a una scrittura.</summary>
    /// <exception cref="PaRouterException">
    /// WORKSPACE_DIRTY con modifiche non salvate; INPUT_INVALID se la cartella e' dentro un altro
    /// repository; CONTEXT_UNAVAILABLE se la cartella manca o Git non risponde.
    /// </exception>
    public static async Task<WorkspacePreparation> PrepareForWriteAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        string root = RootOf(workspaceRoot);

        string? top = await GitCommand.RunAsync(root, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);

        if (top is null)
        {
            await RequiredAsync(root, ["init", "--quiet", "--template=" + GitCommand.EmptyDirectory], cancellationToken)
                .ConfigureAwait(false);

            string tree = await SnapshotTreeAsync(root, cancellationToken).ConfigureAwait(false);
            string commit = (await RequiredAsync(
                root, ["commit-tree", tree, "-m", "pa-router: istantanea iniziale"], cancellationToken).ConfigureAwait(false)).Trim();

            await RequiredAsync(root, ["update-ref", CheckpointRef, commit], cancellationToken).ConfigureAwait(false);

            return WorkspacePreparation.Initialized;
        }

        if (!SamePath(top.Trim(), root))
        {
            // Una sottocartella di un altro repository: le istantanee riguarderebbero file che
            // l'operatore non ha messo nel recinto.
            throw new PaRouterException(
                    ErrorCode.InputInvalid,
                    "The workspace is inside another repository: declare the repository root as the workspace.")
                .WithMetadata("reason", "workspace_nested_repository");
        }

        string current = await SnapshotTreeAsync(root, cancellationToken).ConfigureAwait(false);
        string? head = await TreeOfAsync(root, "HEAD", cancellationToken).ConfigureAwait(false);
        string? checkpoint = await TreeOfAsync(root, CheckpointRef, cancellationToken).ConfigureAwait(false);

        if (string.Equals(current, head, StringComparison.Ordinal)
            || string.Equals(current, checkpoint, StringComparison.Ordinal))
        {
            return WorkspacePreparation.Ready;
        }

        throw new PaRouterException(
                ErrorCode.WorkspaceDirty,
                "The workspace has changes that do not come from a router turn: commit or stash them first.")
            .WithMetadata("reason", "workspace_dirty");
    }

    /// <summary>Istantanea di cio' che c'e' adesso nel workspace, dopo un turno.</summary>
    public static async Task CheckpointAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        string root = RootOf(workspaceRoot);
        string tree = await SnapshotTreeAsync(root, cancellationToken).ConfigureAwait(false);

        string? parent =
            (await GitCommand.RunAsync(root, ["rev-parse", "--verify", "--quiet", CheckpointRef], cancellationToken).ConfigureAwait(false))?.Trim()
            ?? (await GitCommand.RunAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken).ConfigureAwait(false))?.Trim();

        if (parent is not null
            && string.Equals(await TreeOfAsync(root, parent, cancellationToken).ConfigureAwait(false), tree, StringComparison.Ordinal))
        {
            return;
        }

        List<string> arguments = ["commit-tree", tree];

        if (parent is not null)
        {
            arguments.AddRange(["-p", parent]);
        }

        arguments.AddRange(["-m", "pa-router: istantanea dopo un turno"]);

        string commit = (await RequiredAsync(root, arguments, cancellationToken).ConfigureAwait(false)).Trim();

        await RequiredAsync(root, ["update-ref", CheckpointRef, commit], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Che cosa e' cambiato nel workspace rispetto all'ultima istantanea del router (S61).
    ///
    /// E' una **lettura**: confronta l'albero dell'istantanea con quello del contenuto attuale, con
    /// gli stessi vincoli di <see cref="SnapshotTreeAsync"/> — nessun <c>add</c>, nessun filtro,
    /// l'indice dell'utente non viene toccato. Non esistono qui <c>reset</c>, <c>checkout</c> o
    /// <c>stash</c>: tornare indietro resta una decisione dell'operatore, con git.
    ///
    /// Restituisce le righe di <c>diff-tree</c> in forma compatta — stato e percorso — senza il
    /// contenuto delle modifiche: un elenco di file che cambiano non deve portare con se' il testo
    /// che contengono.
    /// </summary>
    /// <param name="workspaceRoot">Radice del workspace.</param>
    /// <param name="cancellationToken">Token di annullamento.</param>
    /// <returns>Coppie stato/percorso, vuote se non c'e' nulla da mostrare.</returns>
    public static async Task<IReadOnlyList<(string Status, string Path)>> DiffAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        string root = RootOf(workspaceRoot);

        string? baseline =
            await TreeOfAsync(root, CheckpointRef, cancellationToken).ConfigureAwait(false)
            ?? await TreeOfAsync(root, "HEAD", cancellationToken).ConfigureAwait(false);

        if (baseline is null)
        {
            return [];
        }

        string current = await SnapshotTreeAsync(root, cancellationToken).ConfigureAwait(false);

        if (string.Equals(baseline, current, StringComparison.Ordinal))
        {
            return [];
        }

        string? output = await GitCommand
            .RunAsync(root, ["diff-tree", "-r", "-z", "--name-status", baseline, current], cancellationToken)
            .ConfigureAwait(false);

        if (output is null)
        {
            return [];
        }

        // `-z` separa con NUL e alterna stato e percorso: niente righe da interpretare, quindi un
        // nome di file con un a capo non puo' fingere una riga in piu'.
        string[] fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        List<(string Status, string Path)> changes = [];

        for (int index = 0; index + 1 < fields.Length; index += 2)
        {
            changes.Add((Describe(fields[index]), fields[index + 1]));
        }

        return changes;
    }

    /// <summary>Stato di <c>diff-tree</c> in parole, senza abbreviazioni da ricordare.</summary>
    private static string Describe(string status) => status switch
    {
        ['A', ..] => "aggiunto",
        ['M', ..] => "modificato",
        ['D', ..] => "cancellato",
        ['R', ..] => "rinominato",
        ['C', ..] => "copiato",
        ['T', ..] => "tipo cambiato",
        _ => status,
    };

    /// <summary>
    /// Albero del contenuto attuale, senza toccare l'indice dell'utente e senza filtri.
    ///
    /// I file tracciati e quelli non ignorati entrano con il loro contenuto letto cosi' com'e'
    /// (<c>hash-object --no-filters</c>) in un indice temporaneo; collegamenti simbolici e
    /// sottomoduli restano come li registra l'indice. <c>git add</c> non si usa: applicherebbe i
    /// filtri <c>clean</c> della configurazione del repository, cioe' programmi scelti da chiunque
    /// abbia scritto quel file.
    /// </summary>
    private static async Task<string> SnapshotTreeAsync(string root, CancellationToken cancellationToken)
    {
        string staged = await RequiredAsync(root, ["ls-files", "-z", "--stage"], cancellationToken).ConfigureAwait(false);
        string others = await RequiredAsync(root, ["ls-files", "-z", "--others", "--exclude-standard"], cancellationToken).ConfigureAwait(false);

        SortedDictionary<string, (string Mode, string? Sha)> entries = new(StringComparer.Ordinal);

        foreach (string record in staged.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<modo> <sha> <stadio>\t<percorso>"
            int tab = record.IndexOf('\t', StringComparison.Ordinal);
            string[] head = record[..tab].Split(' ');
            string path = record[(tab + 1)..];

            if (head[2] is not "0")
            {
                continue;
            }

            entries[path] = head[0] is "120000" or "160000" ? (head[0], head[1]) : (head[0], null);
        }

        foreach (string path in others.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            entries.TryAdd(path, (ModeOf(Path.Combine(root, path)), null));
        }

        List<(string Mode, string Path)> toHash = [];
        List<(string Mode, string Sha, string Path)> ready = [];

        foreach ((string path, (string mode, string? sha)) in entries)
        {
            if (sha is not null)
            {
                ready.Add((mode, sha, path));
                continue;
            }

            FileInfo file = new(Path.Combine(root, path));

            // Un file tracciato e cancellato non entra; un nome con un a capo non si passa per righe.
            if (!file.Exists || file.LinkTarget is not null || path.Contains('\n', StringComparison.Ordinal))
            {
                continue;
            }

            toHash.Add((mode, path));
        }

        if (toHash.Count > 0)
        {
            string hashes = await RequiredAsync(
                root,
                ["hash-object", "-w", "--no-filters", "--stdin-paths"],
                cancellationToken,
                string.Join('\n', toHash.Select(item => item.Path)) + "\n").ConfigureAwait(false);

            string[] shas = hashes.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            for (int position = 0; position < toHash.Count; position++)
            {
                ready.Add((toHash[position].Mode, shas[position].Trim(), toHash[position].Path));
            }
        }

        string index = Path.Combine(Path.GetTempPath(), "pa-router-index-" + Guid.NewGuid().ToString("n"));
        Dictionary<string, string> temporaryIndex = new(StringComparer.Ordinal) { ["GIT_INDEX_FILE"] = index };

        try
        {
            if (ready.Count > 0)
            {
                StringBuilder info = new();

                foreach ((string mode, string sha, string path) in ready)
                {
                    info.Append(mode).Append(' ').Append(sha).Append('\t').Append(path).Append('\0');
                }

                await RequiredAsync(root, ["update-index", "-z", "--index-info"], cancellationToken, info.ToString(), temporaryIndex)
                    .ConfigureAwait(false);
            }

            return (await RequiredAsync(root, ["write-tree"], cancellationToken, null, temporaryIndex).ConfigureAwait(false)).Trim();
        }
        finally
        {
            try
            {
                File.Delete(index);
            }
            catch (IOException)
            {
                // Un indice temporaneo rimasto non contiene nulla che non sia gia' nel repository.
            }
        }
    }

    private static string ModeOf(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return "100644";
        }

        return (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0 ? "100755" : "100644";
    }

    private static async Task<string?> TreeOfAsync(string root, string reference, CancellationToken cancellationToken) =>
        (await GitCommand.RunAsync(root, ["rev-parse", "--verify", "--quiet", reference + "^{tree}"], cancellationToken).ConfigureAwait(false))?.Trim();

    private static async Task<string> RequiredAsync(
        string root,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? standardInput = null,
        IReadOnlyDictionary<string, string>? environment = null) =>
        await GitCommand.RunAsync(root, arguments, cancellationToken, standardInput, environment).ConfigureAwait(false)
        ?? throw new PaRouterException(ErrorCode.ContextUnavailable, "Git did not complete a workspace operation.")
            .WithMetadata("reason", "git_unavailable");

    private static string RootOf(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        string root = Path.GetFullPath(workspaceRoot);

        if (!Directory.Exists(root))
        {
            throw new PaRouterException(ErrorCode.ContextUnavailable, "The workspace does not exist.")
                .WithMetadata("reason", "workspace_missing");
        }

        return root;
    }

    private static bool SamePath(string gitPath, string root) =>
        string.Equals(
            Path.GetFullPath(gitPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
