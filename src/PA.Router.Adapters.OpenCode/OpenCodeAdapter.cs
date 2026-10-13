using System.Runtime.CompilerServices;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Adapters.OpenCode;

/// <summary>
/// Adapter del programma ufficiale OpenCode.
///
/// Il router lancia `opencode run --format json` e legge JSONL, come per gli altri due programmi:
/// e' un supervisore del processo, non un client delle API. Le credenziali dei provider restano di
/// OpenCode; il router non legge il suo file di autenticazione.
///
/// I parametri sono quelli congelati sulla versione 1.18.30 in
/// <c>evidence/S32/opencode/CERTIFICAZIONE.md</c>, osservati eseguendo il programma.
///
/// **Il confinamento arriva per una strada diversa dagli altri adapter.** OpenCode non ha
/// equivalenti di <c>--sandbox</c>, <c>--ask-for-approval</c> o <c>--ignore-user-config</c>: i
/// permessi stanno nella sua configurazione. Il router ne scrive una propria e ci punta
/// <c>OPENCODE_CONFIG</c>, che l'ambiente ripulito porta al processo. E' lo stesso principio —
/// non fidarsi della configurazione dell'utente per ottenere un controllo — ottenuto con il
/// meccanismo che questo programma offre.
///
/// **Limite dichiarato, non aggirato.** Quella configurazione si **fonde** con quella dell'utente:
/// le chiavi del router vincono, ma <c>agent</c>, <c>command</c>, <c>mode</c> e <c>plugin</c>
/// dell'utente sopravvivono, e in OpenCode un agente puo' portarsi i propri permessi. Per questo il
/// probe non dichiara alcun controllo osservato e il servizio resta da certificare.
/// </summary>
public sealed class OpenCodeAdapter : IExecutionAdapter
{
    /// <summary>Sottocomando non interattivo certificato.</summary>
    public const string RunSubcommand = "run";

    /// <summary>Variabile con cui si impone la configurazione, verificata eseguendo il programma.</summary>
    public const string ConfigVariable = "OPENCODE_CONFIG";

    /// <summary>Opzioni vietate: nessun percorso del router puo' produrle.</summary>
    public static IReadOnlyList<string> ForbiddenArguments { get; } =
        [
            // L'help del programma la marca «dangerous!»: approva da sola cio' che non e'
            // esplicitamente negato.
            "--auto",
            // Pubblica la sessione.
            "--share",
        ];

    private readonly IProcessGuardFactory guards;
    private readonly IReadOnlyDictionary<string, string?> extraEnvironment;

    /// <param name="guards">Contenimento dell'albero dei processi.</param>
    /// <param name="extraEnvironment">
    /// Variabili aggiuntive consentite a questo programma, oltre alla allowlist di base. Non e' una
    /// via per passare credenziali, che restano gestite dal programma ufficiale.
    /// </param>
    public OpenCodeAdapter(IProcessGuardFactory guards, IReadOnlyDictionary<string, string?>? extraEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(guards);

        this.guards = guards;
        this.extraEnvironment = extraEnvironment ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public AdapterKind AdapterId => AdapterKind.OpenCodeCli;

    /// <inheritdoc />
    public bool SupportsMemoryTools => false;

    /// <inheritdoc />
    public bool SupportsNativeSkills => false;

    /// <summary>Argomenti effettivi per un'esecuzione autorizzata.</summary>
    public static IReadOnlyList<string> ArgumentsFor(AuthorizedExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        return ArgumentsFor(execution.Model, execution.Workspace, execution.ExtraArguments);
    }

    /// <summary>
    /// Stessa costruzione, sui soli dati che la determinano. E' l'unica forma verificabile senza
    /// fabbricare un'autorizzazione, che nessun test puo' creare.
    /// </summary>
    /// <param name="model">Modello dichiarato dal registro, nella forma <c>fornitore/modello</c>.</param>
    /// <param name="workspace">Workspace autorizzato: decide la directory di lavoro.</param>
    /// <param name="extra">
    /// Argomenti dichiarati dal registro. Si sommano a quelli certificati. Un argomento vietato non
    /// arriva fin qui, e se arrivasse verrebbe scartato adesso.
    /// </param>
    public static IReadOnlyList<string> ArgumentsFor(
        string? model,
        WorkspaceDescriptor? workspace,
        IReadOnlyList<string>? extra = null)
    {
        List<string> arguments =
        [
            RunSubcommand,
            // Eventi strutturati invece del testo formattato per un terminale.
            "--format", "json",
            // Nessun plugin esterno: sono codice di terze parti che la configurazione dell'utente
            // puo' aggiungere, e il router non lo ha certificato.
            "--pure",
        ];

        if (workspace is not null)
        {
            arguments.Add("--dir");
            arguments.Add(workspace.RootPath);
        }

        if (model is { Length: > 0 })
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        foreach (string argument in Allowed(extra))
        {
            arguments.Add(argument);
        }

        // Nessun messaggio sulla riga di comando: il prompt arriva da standard input, come per gli
        // altri due programmi. Verificato eseguendo il programma - accetta entrambe le forme, e
        // quella per argomento lascerebbe il testo della richiesta visibile a chiunque possa
        // elencare i processi.
        return arguments;
    }

    /// <summary>
    /// Argomenti aggiuntivi ammessi: tutto tranne quelli vietati per questa famiglia. E' l'ultima
    /// barriera, non la prima, e non sostituisce il rifiuto alla lettura della configurazione.
    /// </summary>
    private static IEnumerable<string> Allowed(IReadOnlyList<string>? extra) =>
        extra is null || AgentArgumentPolicy.FirstForbidden(AdapterKind.OpenCodeCli, extra) is not null
            ? []
            : extra;

    /// <summary>
    /// Configurazione che il router impone al programma.
    ///
    /// I permessi sono negati per default e concessi solo dove il workspace autorizzato lo
    /// consente: senza workspace scrivibile il programma non modifica file. L'accesso alla rete
    /// resta negato in ogni caso — un agente che scarica non e' cio' che l'operatore ha
    /// autorizzato chiedendo una capacita' di lettura o scrittura.
    /// </summary>
    public static string ConfigurationFor(WorkspaceDescriptor? workspace)
    {
        string edit = workspace is { Writable: true } ? "allow" : "deny";

        // I comandi di sistema li concede il recinto del turno, non una preferenza del programma: senza quella
        // concessione restano negati, come prima.
        string bash = workspace is { Shell: true } ? "allow" : "deny";

        return "{"
            + "\"$schema\":\"https://opencode.ai/config.json\","
            + "\"permission\":{"
            + "\"edit\":\"" + edit + "\","
            + "\"bash\":\"" + bash + "\","
            + "\"webfetch\":\"deny\""
            + "}}";
    }

    /// <summary>
    /// Nomi di configurazione che OpenCode legge dalla directory di lavoro e da quelle superiori.
    /// Osservati eseguendo il programma, uno per uno.
    /// </summary>
    private static readonly string[] ProjectConfigurationNames =
    [
        "opencode.json",
        "opencode.jsonc",
    ];

    /// <summary>
    /// Prima configurazione di progetto che sovrascriverebbe quella del router, cercata dal
    /// workspace verso la radice. Restituisce <c>null</c> se non ce n'e' nessuna.
    ///
    /// Esiste perche' la precedenza di OpenCode e' questa, verificata eseguendo il programma:
    /// **configurazione di progetto &gt; OPENCODE_CONFIG &gt; configurazione dell'utente**. Un file
    /// <c>opencode.json</c> nella directory su cui l'agente lavora trasforma in <c>allow</c> i
    /// permessi che il router ha negato, e quella directory e' contenuto che il router non controlla:
    /// un repository clonato puo' portarselo dentro.
    ///
    /// Trovarne uno non e' un errore del workspace: e' l'assenza del controllo. L'adapter si rifiuta
    /// di partire, come fa senza contenimento dell'albero dei processi.
    /// </summary>
    public static string? OverridingConfiguration(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        DirectoryInfo? directory = new(System.IO.Path.GetFullPath(workspaceRoot));

        while (directory is not null)
        {
            foreach (string name in ProjectConfigurationNames)
            {
                string candidate = System.IO.Path.Combine(directory.FullName, name);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // Anche la forma in sottodirectory, che il programma legge allo stesso modo.
            string nested = System.IO.Path.Combine(directory.FullName, ".opencode", "opencode.json");

            if (File.Exists(nested))
            {
                return nested;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AdapterEvent> ExecuteAsync(
        AuthorizedExecution execution,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);

        if (execution.Executable is not string executable || executable.Length == 0)
        {
            yield return new AdapterFailed(
                ErrorCode.TargetUnavailable,
                "The target does not declare the absolute path of the official program.");

            yield break;
        }

        if (execution.Workspace is not { } workspace)
        {
            yield return new AdapterFailed(
                ErrorCode.ControlUnavailable,
                "An agent target needs an authorized workspace.");

            yield break;
        }

        if (!guards.IsSupported)
        {
            yield return new AdapterFailed(
                ErrorCode.ControlUnavailable,
                "Process tree containment is not available on this platform.");

            yield break;
        }

        if (OverridingConfiguration(workspace.RootPath) is string overriding)
        {
            // Senza questo controllo l'adapter imporrebbe permessi che il programma non applica, e
            // il router dichiarerebbe un confinamento che non ha. Meglio non partire.
            yield return new AdapterFailed(
                ErrorCode.ControlUnavailable,
                "A project configuration overrides the permissions imposed by the router: "
                + overriding);

            yield break;
        }

        yield return new AdapterStarted();

        // La configurazione imposta vive per il solo turno e viene cancellata subito dopo: lasciarla
        // sul disco significherebbe lasciare in giro un file che decide dei permessi di un agente.
        string configuration = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pa-router-opencode-" + Guid.NewGuid().ToString("N") + ".json");

        System.Threading.Channels.Channel<AdapterEvent> events =
            System.Threading.Channels.Channel.CreateUnbounded<AdapterEvent>();

        Task pump = Task.Run(
            async () =>
            {
                try
                {
                    await File.WriteAllTextAsync(configuration, ConfigurationFor(workspace), CancellationToken.None)
                        .ConfigureAwait(false);

                    CliOutcome outcome = await CliProcessLauncher.RunAsync(
                        guards,
                        executable,
                        ArgumentsFor(execution),
                        workspace.RootPath,
                        System.Text.Encoding.UTF8.GetString(execution.Payload.Bytes.Span),
                        Merged(configuration),
                        line => TranslateAsync(line, events.Writer),
                        cancellationToken).ConfigureAwait(false);

                    if (outcome.Truncated)
                    {
                        await events.Writer.WriteAsync(
                            new AdapterFailed(ErrorCode.OutputLimit, "The program produced more output than allowed."),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    else if (cancellationToken.IsCancellationRequested)
                    {
                        // Un'uscita avvenuta a cancellazione gia' richiesta e' una conseguenza, non
                        // una causa: il motivo lo conosce il coordinatore, non l'adapter.
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    else if (outcome.ExitCode is not 0)
                    {
                        await events.Writer.WriteAsync(
                            new AdapterFailed(
                                ErrorCode.TargetUnavailable,
                                "The official program exited with code "
                                + outcome.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                + " after "
                                + outcome.ErrorLines.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                + " diagnostic lines."),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        await events.Writer.WriteAsync(new AdapterCompleted(0), CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await events.Writer.WriteAsync(
                        new AdapterFailed(ErrorCode.TargetUnavailable, "The official program could not be supervised."),
                        CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        File.Delete(configuration);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // Il file resta in una directory temporanea e non contiene segreti: non
                        // vale un errore che nasconderebbe l'esito del turno.
                    }

                    events.Writer.TryComplete();
                }
            },
            CancellationToken.None);

        await foreach (AdapterEvent adapterEvent in events.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            yield return adapterEvent;
        }

        await pump.ConfigureAwait(false);
    }

    /// <summary>
    /// Ambiente aggiuntivo: le chiavi API concorrenti vengono rimosse, perche' il programma usa la
    /// propria autenticazione e trovarne un'altra cambierebbe in silenzio chi paga.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Environment() =>
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["OPENAI_API_KEY"] = null,
            ["ANTHROPIC_API_KEY"] = null,
        };

    /// <summary>Ambiente effettivo: quello dell'adapter, la configurazione imposta e le variabili consentite.</summary>
    private IReadOnlyDictionary<string, string?> Merged(string configuration)
    {
        Dictionary<string, string?> merged = new(Environment(), StringComparer.OrdinalIgnoreCase)
        {
            [ConfigVariable] = configuration,
        };

        foreach ((string key, string? value) in extraEnvironment)
        {
            merged[key] = value;
        }

        return merged;
    }

    /// <summary>
    /// Traduce una riga di <c>opencode run --format json</c> in eventi normalizzati.
    ///
    /// La forma e' quella osservata sulla versione 1.18.30 con un turno reale contro un modello
    /// locale (<c>evidence/S32/opencode/eventi-json.txt</c>): ogni riga e' un oggetto con
    /// <c>type</c> e <c>part</c>. Cio' che non si riconosce viene ignorato invece che interpretato.
    /// </summary>
    public static async Task TranslateAsync(string line, System.Threading.Channels.ChannelWriter<AdapterEvent> writer)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(writer);

        if (line.Length == 0)
        {
            return;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            await writer.WriteAsync(
                new AdapterFailed(ErrorCode.ProtocolError, "The official program produced a malformed event."),
                CancellationToken.None).ConfigureAwait(false);

            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object
                || !root.TryGetProperty("type", out JsonElement type)
                || type.ValueKind is not JsonValueKind.String)
            {
                return;
            }

            JsonElement part = root.TryGetProperty("part", out JsonElement value) ? value : default;

            switch (type.GetString())
            {
                case "text" when Text(part) is string text:
                    await writer.WriteAsync(new AdapterOutputChunk(text), CancellationToken.None).ConfigureAwait(false);
                    break;

                case "step_finish":
                    await WriteUsageAsync(part, writer).ConfigureAwait(false);
                    break;

                case "error":
                    await writer.WriteAsync(
                        new AdapterFailed(ErrorCode.TargetUnavailable, "The official program reported an error."),
                        CancellationToken.None).ConfigureAwait(false);
                    break;

                default:
                    break;
            }
        }
    }

    private static string? Text(JsonElement part) =>
        part.ValueKind is JsonValueKind.Object
        && part.TryGetProperty("text", out JsonElement text)
        && text.ValueKind is JsonValueKind.String
            ? text.GetString()
            : null;

    private static async Task WriteUsageAsync(JsonElement part, System.Threading.Channels.ChannelWriter<AdapterEvent> writer)
    {
        if (part.ValueKind is not JsonValueKind.Object
            || !part.TryGetProperty("tokens", out JsonElement tokens)
            || tokens.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        long? input = Count(tokens, "input");
        long? output = Count(tokens, "output");

        if (input is null && output is null)
        {
            return;
        }

        // Il costo lo dichiara il programma. Con un modello locale vale zero, ed e' un valore
        // osservato: non viene stimato ne' inventato quando manca.
        decimal? cost = part.TryGetProperty("cost", out JsonElement declared) && declared.ValueKind is JsonValueKind.Number
            ? declared.GetDecimal()
            : null;

        await writer.WriteAsync(new AdapterUsage(new UsageReport(input, output, cost)), CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static long? Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.Number
            ? value.GetInt64()
            : null;
}
