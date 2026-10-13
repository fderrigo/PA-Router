using System.Text;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Session;

/// <summary>
/// Fotografia del workspace tramite Git (03-specs/SESSION-HANDOVER.md, «Capsule portabile»).
///
/// Legge <c>HEAD</c> e lo stato delle modifiche non committate — tracciate, non tracciate e
/// cancellate — e ne produce due digest con la chiave locale. Serve a rilevare una differenza fra
/// il momento in cui la capsule e' stata creata e quello del dispatch: se qualcosa e' cambiato, il
/// turno riparte dalla preparazione invece di lavorare su un presupposto scaduto.
///
/// Non esegue <c>reset</c>, <c>clean</c> o <c>stash</c>: il checkpoint osserva, non ripristina. E i
/// nomi dei file non escono da qui — nel checkpoint restano solo digest.
///
/// Un workspace che non e' un repository Git non e' utilizzabile dagli agenti nella beta: senza
/// riferimento non esiste modo di dire che cosa e' cambiato.
/// </summary>
public static class WorkspaceCheckpointReader
{
    /// <summary>Tempo massimo concesso a un comando Git.</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Legge il checkpoint del workspace indicato.</summary>
    /// <param name="workspaceRoot">Radice del workspace.</param>
    /// <param name="digest">Digest con chiave locale.</param>
    /// <param name="cancellationToken">Annullamento.</param>
    public static async Task<WorkspaceCheckpoint> ReadAsync(
        string workspaceRoot,
        IMetadataDigest digest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(digest);

        string root = Path.GetFullPath(workspaceRoot);

        if (!Directory.Exists(root))
        {
            throw new PaRouterException(ErrorCode.ContextUnavailable, "The workspace does not exist.")
                .WithMetadata("reason", "workspace_missing");
        }

        string? head = await GitAsync(root, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);

        if (head is null)
        {
            throw new PaRouterException(ErrorCode.ContextUnavailable, "The workspace is not a Git repository with a commit.")
                .WithMetadata("reason", "git_head_unavailable");
        }

        string? status = await GitAsync(root, ["status", "--porcelain=v1"], cancellationToken).ConfigureAwait(false);

        if (status is null)
        {
            throw new PaRouterException(ErrorCode.ContextUnavailable, "The workspace status could not be read.")
                .WithMetadata("reason", "git_status_unavailable");
        }

        // Il manifesto e' ordinato: due letture dello stesso stato devono produrre lo stesso digest.
        string manifest = string.Join(
            '\n',
            status.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .OrderBy(line => line, StringComparer.Ordinal));

        return new WorkspaceCheckpoint(
            digest.Compute(Encoding.UTF8.GetBytes(root)),
            head.Trim(),
            digest.Compute(Encoding.UTF8.GetBytes(manifest)));
    }

    /// <summary>
    /// Vero se il workspace e' cambiato rispetto al checkpoint. Un drift non e' un errore del
    /// workspace: e' la ragione per cui la preparazione va rifatta.
    /// </summary>
    public static bool HasDrifted(WorkspaceCheckpoint expected, WorkspaceCheckpoint observed)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observed);

        return !string.Equals(expected.GitHead, observed.GitHead, StringComparison.Ordinal)
            || !string.Equals(expected.DirtyManifestDigest, observed.DirtyManifestDigest, StringComparison.Ordinal)
            || !string.Equals(expected.RepoRootDigest, observed.RepoRootDigest, StringComparison.Ordinal);
    }

    /// <summary>
    /// Esegue un comando Git di sola lettura con le difese di <see cref="GitCommand"/>: il workspace
    /// e' stato scritto da un agente, e `git status` non deve eseguire nulla di cio' che la
    /// configurazione di quel repository chiede.
    /// </summary>
    private static Task<string?> GitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        GitCommand.RunAsync(workingDirectory, arguments, cancellationToken);
}
