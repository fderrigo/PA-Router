using System.Security.Cryptography;
using System.Text;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Registry.Loading;

namespace PA.Router.Hosting.Runtime;

/// <summary>Stato della configurazione rispetto ai file sul disco.</summary>
public enum ConfigurationState
{
    /// <summary>La configurazione in uso e' quella sul disco.</summary>
    Current,

    /// <summary>I file sul disco non formano una configurazione valida.</summary>
    Invalid,

    /// <summary>I file sul disco cambiano un'impostazione che si applica solo ripartendo.</summary>
    RestartRequired,
}

/// <summary>Esito di una rilettura.</summary>
/// <param name="State">Stato rispetto ai file sul disco.</param>
/// <param name="Reloaded">Vero se questa chiamata ha sostituito la configurazione in uso.</param>
/// <param name="Issues">Problemi dei file, quando non sono validi.</param>
/// <param name="RestartField">Impostazione che richiede di ripartire, quando e' quello il caso.</param>
public sealed record ConfigurationRefresh(
    ConfigurationState State,
    bool Reloaded,
    IReadOnlyList<ValidationIssue> Issues,
    string? RestartField)
{
    /// <summary>Vero se si puo' eseguire con la configurazione in uso.</summary>
    public bool IsUsable => State is ConfigurationState.Current;
}

/// <summary>
/// Configurazione di un processo che vive a lungo — worker, REPL, host API — tenuta allineata ai
/// file sul disco.
///
/// Fino alla S38 un worker leggeva la configurazione all'avvio e non la rileggeva piu': un voto
/// scritto con <c>pa registry rate</c>, un servizio aggiunto o un profilo **ristretto** non valevano
/// finche' qualcuno non lo riavviava. L'ultimo caso e' il grave: l'operatore stringe una regola, e
/// il processo continua a decidere con quella larga.
///
/// La regola e' una: **non si esegue mai con una configurazione diversa da quella sul disco.**
/// Quando i file cambiano e sono validi, la configurazione nuova sostituisce la vecchia per le
/// richieste successive. Quando non sono validi, o cambiano un'impostazione che vale solo
/// ripartendo, il processo non ripiega sulla configurazione di prima: si ferma e lo dice, finche' i
/// file non tornano validi o qualcuno non lo riavvia. Ripiegare significherebbe proprio continuare
/// con la regola che l'operatore voleva cambiare.
///
/// Il confronto e' sul contenuto dei file, non sulla data di modifica: costa pochi kilobyte letti
/// per richiesta e non dipende dalla risoluzione dell'orologio del filesystem.
/// </summary>
public sealed class ConfigurationWatch
{
    private readonly Lock gate = new();
    private readonly IClock clock;

    private ConfigurationSnapshot current;
    private string runningFingerprint;
    private string lastFingerprint;
    private ConfigurationRefresh lastRefresh = new(ConfigurationState.Current, false, [], null);

    /// <param name="snapshot">Configurazione caricata all'avvio.</param>
    /// <param name="clock">Orologio delle riletture.</param>
    public ConfigurationWatch(ConfigurationSnapshot snapshot, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(clock);

        current = snapshot;
        this.clock = clock;
        runningFingerprint = Fingerprint(snapshot);
        lastFingerprint = runningFingerprint;
    }

    /// <summary>Configurazione in uso: l'ultima valida sostituita da una rilettura.</summary>
    public ConfigurationSnapshot Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    /// <summary>
    /// Confronta i file con la configurazione in uso e, se sono cambiati, li rilegge.
    /// </summary>
    public ConfigurationRefresh Refresh()
    {
        lock (gate)
        {
            string fingerprint = Fingerprint(current);

            if (string.Equals(fingerprint, lastFingerprint, StringComparison.Ordinal))
            {
                return lastRefresh with { Reloaded = false };
            }

            lastFingerprint = fingerprint;

            if (string.Equals(fingerprint, runningFingerprint, StringComparison.Ordinal))
            {
                // I file sono tornati quelli della configurazione in uso: e' di nuovo utilizzabile.
                return lastRefresh = new ConfigurationRefresh(ConfigurationState.Current, false, [], null);
            }

            ConfigurationLoadResult loaded = new ConfigurationLoader(clock).Load(current.DataDirectory);

            if (!loaded.IsValid)
            {
                return lastRefresh = new ConfigurationRefresh(ConfigurationState.Invalid, false, loaded.Issues, null);
            }

            ConfigurationSnapshot next = loaded.Require();

            if (RestartField(current.Config, next.Config) is string field)
            {
                return lastRefresh = new ConfigurationRefresh(ConfigurationState.RestartRequired, false, [], field);
            }

            // I file possono cambiare di nuovo mentre il caricamento li legge. Con lo stesso elenco di
            // file vale l'impronta presa **prima** di caricare: se nel frattempo e' cambiato
            // qualcosa, la prossima rilettura vede la differenza. Con un elenco diverso l'impronta
            // resta sconosciuta, e la prossima rilettura ricarica una volta ancora.
            runningFingerprint = FilesOf(current).SequenceEqual(FilesOf(next), StringComparer.Ordinal)
                ? fingerprint
                : string.Empty;

            lastFingerprint = runningFingerprint;
            current = next;

            return lastRefresh = new ConfigurationRefresh(ConfigurationState.Current, true, [], null);
        }
    }

    /// <summary>
    /// Impostazioni che si applicano solo ripartendo. Il modo dei contenuti decide dove finisce cio'
    /// che le sessioni gia' aperte hanno scritto; telemetria e API aprono ascoltatori ed esportatori
    /// all'avvio. Tutto il resto — registro, profili, bande, memoria, estensioni, limiti — vale dalla
    /// richiesta successiva.
    /// </summary>
    private static string? RestartField(ConfigDocument running, ConfigDocument next)
    {
        if (running.ContentMode != next.ContentMode)
        {
            return "contentMode";
        }

        if (running.Telemetry != next.Telemetry)
        {
            return "telemetry";
        }

        return running.Api != next.Api ? "api" : null;
    }

    /// <summary>File che compongono la configurazione, relativi alla directory dati.</summary>
    private static IEnumerable<string> FilesOf(ConfigurationSnapshot snapshot) =>
        [.. ConfigurationLoader.ConfigFileNames, snapshot.Config.RegistryFile, .. snapshot.Config.PolicyFiles];

    /// <summary>Impronta del contenuto dei file: nome, presenza e byte di ciascuno.</summary>
    private static string Fingerprint(ConfigurationSnapshot snapshot)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (string relative in FilesOf(snapshot))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\n"));

            string path = Path.Combine(snapshot.DataDirectory, relative);

            try
            {
                if (!File.Exists(path))
                {
                    hash.AppendData("missing\n"u8);
                    continue;
                }

                byte[] content = File.ReadAllBytes(path);

                hash.AppendData(Encoding.UTF8.GetBytes(content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"));
                hash.AppendData(content);
            }
            catch (IOException)
            {
                // Un file in scrittura in questo momento: diverso da qualsiasi contenuto, quindi la
                // rilettura prova a caricare e, se non riesce, lo dichiara.
                hash.AppendData("unreadable\n"u8);
            }
            catch (UnauthorizedAccessException)
            {
                hash.AppendData("unreadable\n"u8);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
