using System.Diagnostics;
using System.Security.Cryptography;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Abstractions;

/// <summary>
/// Osservazione di un programma ufficiale.
///
/// Fa solo cio' che non costa niente all'operatore: verifica che l'eseguibile dichiarato esista al
/// percorso assoluto indicato, ne calcola il digest e ne legge la versione con <c>--version</c>.
/// Non avvia un turno, non tocca l'autenticazione e non consuma quota.
///
/// Non dichiara alcun controllo. I confini di lettura e scrittura, le restrizioni di shell e
/// l'isolamento della configurazione sono <c>unknown</c> finche' non vengono osservati con un turno
/// reale: la matrice di S13 li elenca uno per uno, e un profilo che li richiede nega
/// (03-specs/ADAPTERS.md, «unknown su una proprieta' necessaria → CONTROL_UNAVAILABLE»).
/// </summary>
public static class CliProbe
{
    /// <summary>Durata di una osservazione.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(30);

    /// <summary>Tempo massimo concesso a <c>--version</c>.</summary>
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Osserva il programma dichiarato dal service.</summary>
    /// <param name="service">Servizio del registro.</param>
    /// <param name="guards">Contenimento dell'albero, richiesto per i servizi agente.</param>
    /// <param name="clock">Orologio.</param>
    /// <param name="cancellationToken">Annullamento.</param>
    public static async ValueTask<ProbeResult> ObserveAsync(
        ServiceDescriptor service,
        IProcessGuardFactory guards,
        IClock clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(guards);
        ArgumentNullException.ThrowIfNull(clock);

        if (service.Executable is not string executable || executable.Length == 0 || service.HasPlaceholders)
        {
            return Unavailable(service, clock, "executable_not_configured");
        }

        if (!System.IO.Path.IsPathRooted(executable))
        {
            // Un nome risolto dal PATH sarebbe il programma scelto dall'ambiente, non dal registro.
            return Unavailable(service, clock, "executable_not_absolute");
        }

        if (!File.Exists(executable))
        {
            return Unavailable(service, clock, "executable_missing");
        }

        if (!guards.IsSupported)
        {
            return Unavailable(service, clock, "process_containment_unavailable");
        }

        string digest = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(executable, cancellationToken).ConfigureAwait(false)));
        string? version = await ReadVersionAsync(executable, cancellationToken).ConfigureAwait(false);

        if (version is null)
        {
            return Unavailable(service, clock, "version_not_observed");
        }

        DateTimeOffset now = clock.UtcNow;

        return new ProbeResult(
            service.Id,
            ProbeHealth.Healthy,
            // L'autenticazione appartiene al programma: il router non la osserva e non la dichiara.
            AuthStatus.Unknown,
            QuotaState.Unknown,
            ExecutablePath: executable,
            ExecutableHash: digest,
            Version: version,
            ProtocolVersion: null,
            Model: service.Model,
            Capabilities: new HashSet<Capability>(service.Capabilities),
            // Nessun controllo dichiarato: nessuno e' stato osservato.
            Controls: new HashSet<Control>(),
            ContextWindowTokens: null,
            CheckedAt: now,
            ExpiresAt: now + Freshness,
            FailureCode: null);
    }

    /// <summary>
    /// Legge la versione dichiarata. Il programma viene avviato senza shell, senza prompt e con
    /// standard input chiuso: non puo' entrare in modalita' interattiva.
    /// </summary>
    public static async ValueTask<string?> ReadVersionAsync(string executable, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        ProcessStartInfo start = new()
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        start.ArgumentList.Add("--version");

        // Anche per chiedere la versione il programma non eredita l'ambiente del router: passphrase e chiavi restano fuori.
        start.Environment.Clear();

        foreach (string name in CliProcessLauncher.BaseEnvironmentAllowlist)
        {
            if (Environment.GetEnvironmentVariable(name) is string value)
            {
                start.Environment[name] = value;
            }
        }

        try
        {
            using Process process = new() { StartInfo = start };

            if (!process.Start())
            {
                return null;
            }

            process.StandardInput.Close();

            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(VersionTimeout);

            string output = await process.StandardOutput.ReadToEndAsync(deadline.Token).ConfigureAwait(false);

            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);

            string version = output.Trim();

            return version.Length == 0 ? null : version;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static ProbeResult Unavailable(ServiceDescriptor service, IClock clock, string failureCode)
    {
        DateTimeOffset now = clock.UtcNow;

        return new ProbeResult(
            service.Id,
            ProbeHealth.Unavailable,
            AuthStatus.Unknown,
            QuotaState.Unknown,
            ExecutablePath: null,
            ExecutableHash: null,
            Version: null,
            ProtocolVersion: null,
            Model: null,
            Capabilities: new HashSet<Capability>(),
            Controls: new HashSet<Control>(),
            ContextWindowTokens: null,
            CheckedAt: now,
            ExpiresAt: now + Freshness,
            FailureCode: failureCode);
    }
}
