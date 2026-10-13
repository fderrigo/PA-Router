using System.Runtime.CompilerServices;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Adapters.Codex;

/// <summary>
/// Adapter del programma ufficiale Codex CLI.
///
/// Il router lancia `codex exec --json` e legge JSONL: e' un supervisore del programma, non un
/// client delle API. Login e subscription restano di Codex; il router non legge il file di
/// autenticazione ne' i token che contiene.
///
/// I parametri sono quelli congelati in S13 sulla versione installata. Due scelte non sono
/// negoziabili: `--sandbox` viene sempre passato esplicitamente, perche' la configurazione
/// dell'utente su una macchina qualunque puo' essere permissiva quanto vuole, e
/// `--ignore-user-config` impedisce che quella configurazione rientri dalla finestra.
/// `danger-full-access` e le opzioni che saltano approvazioni e sandbox non compaiono qui.
/// </summary>
public sealed class CodexAdapter : IExecutionAdapter
{
    /// <summary>Sottocomando non interattivo certificato.</summary>
    public const string ExecSubcommand = "exec";

    /// <summary>Opzioni vietate: nessun percorso del router puo' produrle.</summary>
    public static IReadOnlyList<string> ForbiddenArguments { get; } =
        [
            "--dangerously-bypass-approvals-and-sandbox",
            "--dangerously-bypass-hook-trust",
            "danger-full-access",
            // Concederebbe approvazioni automatiche alle azioni del modello.
            "--approve-for-me",
        ];

    private readonly IProcessGuardFactory guards;
    private readonly IReadOnlyDictionary<string, string?> extraEnvironment;

    /// <param name="guards">Contenimento dell'albero dei processi.</param>
    /// <param name="extraEnvironment">
    /// Variabili aggiuntive consentite a questo programma, oltre alla allowlist di base. Serve ai
    /// programmi che hanno bisogno di localizzare il proprio runtime; non e' una via per passare
    /// credenziali, che restano gestite dal programma ufficiale.
    /// </param>
    public CodexAdapter(IProcessGuardFactory guards, IReadOnlyDictionary<string, string?>? extraEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(guards);

        this.guards = guards;
        this.extraEnvironment = extraEnvironment ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public AdapterKind AdapterId => AdapterKind.CodexCli;

    /// <inheritdoc />
    public bool SupportsMemoryTools => false;

    /// <inheritdoc />
    public bool SupportsNativeSkills => false;

    /// <summary>
    /// Argomenti effettivi. Il confine di scrittura viene dal workspace autorizzato, non da una
    /// preferenza: senza workspace scrivibile la sandbox e' <c>read-only</c>.
    /// </summary>
    public static IReadOnlyList<string> ArgumentsFor(AuthorizedExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        return ArgumentsFor(execution.Model, execution.Workspace, execution.ExtraArguments);
    }

    /// <summary>
    /// Stessa costruzione, sui soli dati che la determinano: modello e confine del workspace. E'
    /// l'unica forma verificabile senza fabbricare un'autorizzazione, che nessun test puo' creare.
    /// </summary>
    /// <param name="model">Modello dichiarato dal registro, quando c'e'.</param>
    /// <param name="workspace">Workspace autorizzato, che decide il confine di scrittura.</param>
    /// <param name="extra">
    /// Argomenti dichiarati dal registro. Si **sommano** a quelli certificati: e' cosi' che un
    /// operatore rende locale l'agente (<c>--oss --local-provider ollama</c>). Un argomento vietato
    /// non arriva fin qui — viene rifiutato alla lettura della configurazione e di nuovo alla
    /// costruzione dell'esecuzione autorizzata — e se arrivasse verrebbe scartato adesso.
    /// </param>
    public static IReadOnlyList<string> ArgumentsFor(
        string? model,
        WorkspaceDescriptor? workspace,
        IReadOnlyList<string>? extra = null)
    {

        List<string> arguments =
        [
            ExecSubcommand,
            "--json",
            // La configurazione utente non partecipa: potrebbe disattivare la sandbox.
            "--ignore-user-config",
            "--ignore-rules",
            "--skip-git-repo-check",
            "--color", "never",
            "--sandbox", workspace is { Writable: true } ? "workspace-write" : "read-only",
            // `--ask-for-approval` non esiste sotto `exec` nella 0.153.4: passarlo fa terminare il
            // programma con codice 2 prima di qualunque inferenza. Lo smoke reale lo ha mostrato,
            // il programma finto no, perche' accettava qualunque argomento.
            //
            // Sotto `exec` il confinamento resta esplicito comunque: la sandbox e' passata sopra e
            // `--approve-for-me` — l'unica opzione che concederebbe approvazioni automatiche — non
            // viene mai aggiunta.
        ];

        if (workspace is not null)
        {
            arguments.Add("--cd");
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

        // Il trattino finale dice a Codex di leggere le istruzioni da standard input.
        arguments.Add("-");

        return arguments;
    }

    /// <summary>
    /// Argomenti aggiuntivi ammessi: tutto tranne quelli vietati per questa famiglia. E' l'ultima
    /// barriera, non la prima, e non sostituisce il rifiuto alla lettura della configurazione.
    /// </summary>
    private static IEnumerable<string> Allowed(IReadOnlyList<string>? extra) =>
        extra is null || AgentArgumentPolicy.FirstForbidden(AdapterKind.CodexCli, extra) is not null
            ? []
            : extra;

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

        yield return new AdapterStarted();

        System.Threading.Channels.Channel<AdapterEvent> events =
            System.Threading.Channels.Channel.CreateUnbounded<AdapterEvent>();

        Task pump = Task.Run(
            async () =>
            {
                try
                {
                    CliOutcome outcome = await CliProcessLauncher.RunAsync(
                        guards,
                        executable,
                        ArgumentsFor(execution),
                        workspace.RootPath,
                        System.Text.Encoding.UTF8.GetString(execution.Payload.Bytes.Span),
                        Merged(),
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
                        // Stessa corsa dell'adapter di Claude Code: il codice di uscita di un
                        // programma ucciso dalla cancellazione e' una conseguenza, non una causa, e
                        // chiamarlo «programma non disponibile» direbbe la cosa sbagliata su un
                        // target che funziona. Il motivo della cancellazione lo conosce il
                        // coordinatore, non l'adapter.
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
                // Una cancellazione non viene classificata qui: l'adapter non sa se e' scaduta la
                // deadline o se l'ha chiesta l'operatore, e indovinare produrrebbe un codice
                // sbagliato. L'eccezione risale al coordinatore, che quella distinzione ce l'ha.
                catch (PaRouterException ex)
                {
                    await events.Writer.WriteAsync(
                        new AdapterFailed(ex.Code, "The official program could not be started."),
                        CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    events.Writer.Complete();
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
    /// propria autenticazione e trovarne un'altra cambierebbe in silenzio il modo di fatturazione.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Environment() =>
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["OPENAI_API_KEY"] = null,
            ["ANTHROPIC_API_KEY"] = null,
        };

    /// <summary>Ambiente effettivo: quello dell'adapter piu' le variabili consentite dal chiamante.</summary>
    private IReadOnlyDictionary<string, string?> Merged()
    {
        Dictionary<string, string?> merged = new(Environment(), StringComparer.OrdinalIgnoreCase);

        foreach ((string key, string? value) in extraEnvironment)
        {
            merged[key] = value;
        }

        return merged;
    }

    /// <summary>
    /// Traduce una riga JSONL di <c>codex exec --json</c> in eventi normalizzati.
    ///
    /// Vengono conservati il messaggio finale dell'agente, le modifiche ai file dichiarate e l'uso
    /// dei token. Il ragionamento interno non viene propagato.
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

            if (root.ValueKind is not JsonValueKind.Object)
            {
                return;
            }

            // La forma degli eventi cambia fra versioni: il tipo viene cercato dove le versioni
            // note lo mettono, e cio' che non si riconosce viene ignorato invece che interpretato.
            JsonElement payload = root.TryGetProperty("msg", out JsonElement message) ? message : root;

            if (!payload.TryGetProperty("type", out JsonElement type) || type.ValueKind is not JsonValueKind.String)
            {
                return;
            }

            switch (type.GetString())
            {
                case "agent_message" or "item.completed" when Text(payload) is string text:
                    await writer.WriteAsync(new AdapterOutputChunk(text), CancellationToken.None).ConfigureAwait(false);
                    break;

                case "patch_apply_end" or "turn.diff" when Path(payload) is string path:
                    await writer.WriteAsync(new AdapterArtifactChanged(path), CancellationToken.None).ConfigureAwait(false);
                    break;

                case "token_count" or "turn.completed":
                    await WriteUsageAsync(payload, writer).ConfigureAwait(false);
                    break;

                case "error" or "stream_error":
                    await writer.WriteAsync(
                        new AdapterFailed(ErrorCode.TargetUnavailable, "The official program reported an error."),
                        CancellationToken.None).ConfigureAwait(false);
                    break;

                default:
                    break;
            }
        }
    }

    private static string? Text(JsonElement payload)
    {
        if (payload.TryGetProperty("message", out JsonElement message) && message.ValueKind is JsonValueKind.String)
        {
            return message.GetString();
        }

        if (payload.TryGetProperty("item", out JsonElement item)
            && item.TryGetProperty("text", out JsonElement text)
            && text.ValueKind is JsonValueKind.String)
        {
            return text.GetString();
        }

        return null;
    }

    private static string? Path(JsonElement payload)
    {
        if (payload.TryGetProperty("path", out JsonElement path) && path.ValueKind is JsonValueKind.String)
        {
            return path.GetString();
        }

        return null;
    }

    private static async Task WriteUsageAsync(JsonElement payload, System.Threading.Channels.ChannelWriter<AdapterEvent> writer)
    {
        JsonElement usage = payload.TryGetProperty("info", out JsonElement info) ? info : payload;

        if (usage.TryGetProperty("total_token_usage", out JsonElement total))
        {
            usage = total;
        }
        else if (usage.TryGetProperty("usage", out JsonElement nested))
        {
            usage = nested;
        }

        long? input = Count(usage, "input_tokens");
        long? output = Count(usage, "output_tokens");

        if (input is not null || output is not null)
        {
            // Il costo non viene dichiarato da Codex in questa modalita': null significa non
            // misurato, e non viene stimato.
            await writer.WriteAsync(new AdapterUsage(new UsageReport(input, output, BilledCost: null)), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static long? Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.Number
            ? value.GetInt64()
            : null;
}
