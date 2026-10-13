using System.Runtime.CompilerServices;
using System.Text.Json;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Adapters.ClaudeCode;

/// <summary>
/// Adapter del programma ufficiale Claude Code.
///
/// PA Router lancia il programma `claude` e ne supervisiona il processo: non e' un client delle API
/// Anthropic, e non usa l'Agent SDK al suo posto. Claude Code conserva il proprio harness, i propri
/// strumenti e la propria autenticazione nativa; il router non legge ne' copia credenziali
/// (03-specs/ADAPTERS.md).
///
/// I parametri sono quelli congelati in S13 sulla versione installata: `--print` con
/// `--output-format stream-json` e `--verbose`, prompt su standard input. Le opzioni che
/// disattivano le approvazioni non compaiono in questo file, e non devono comparirci: un adapter
/// che le usasse renderebbe inutile ogni controllo a monte.
/// </summary>
public sealed class ClaudeCodeAdapter : IExecutionAdapter
{
    /// <summary>Argomenti fissi della modalita' non interattiva certificata.</summary>
    public static IReadOnlyList<string> BaseArguments { get; } =
        ["--print", "--output-format", "stream-json", "--verbose", "--input-format", "text"];

    /// <summary>Opzioni vietate: la loro presenza in una configurazione e' un errore, non una scelta.</summary>
    public static IReadOnlyList<string> ForbiddenArguments { get; } =
        ["--dangerously-skip-permissions", "--allow-dangerously-skip-permissions"];

    private readonly IProcessGuardFactory guards;
    private readonly IReadOnlyDictionary<string, string?> extraEnvironment;

    /// <param name="guards">Contenimento dell'albero dei processi.</param>
    /// <param name="extraEnvironment">
    /// Variabili aggiuntive consentite a questo programma, oltre alla allowlist di base. Serve ai
    /// programmi che hanno bisogno di localizzare il proprio runtime; non e' una via per passare
    /// credenziali, che restano gestite dal programma ufficiale.
    /// </param>
    public ClaudeCodeAdapter(IProcessGuardFactory guards, IReadOnlyDictionary<string, string?>? extraEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(guards);

        this.guards = guards;
        this.extraEnvironment = extraEnvironment ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public AdapterKind AdapterId => AdapterKind.ClaudeCodeCli;

    /// <inheritdoc />
    public bool SupportsMemoryTools => false;

    /// <inheritdoc />
    public bool SupportsNativeSkills => true;

    /// <summary>
    /// Argomenti effettivi per un'esecuzione: base certificata, modalita' dei permessi derivata dal
    /// confine del workspace, e le directory autorizzate. Nessun argomento arriva dal modello.
    /// </summary>
    public static IReadOnlyList<string> ArgumentsFor(AuthorizedExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        return ArgumentsFor(execution.Model, execution.Workspace, execution.ExtraArguments, execution.NativeSession);
    }

    /// <summary>
    /// Che cosa scrivere al programma. Proseguendo la sua sessione riceve **solo la richiesta nuova** con i dati che
    /// la accompagnano: i turni precedenti li ha gia' letti, e rimandarglieli sarebbe pagare due volte (ADR-022).
    /// Aprendone una — primo turno, o servizio appena cambiato — riceve il payload intero, cronologia compresa: e'
    /// il contesto impacchettato di S15. In entrambi i casi sono byte gia' esaminati, mai aggiunti.
    /// </summary>
    public static string PromptFor(AuthorizedExecution execution, bool resume)
    {
        ArgumentNullException.ThrowIfNull(execution);

        return resume
            ? ChatTranscript.Split(execution.Payload)[^1].Text
            : System.Text.Encoding.UTF8.GetString(execution.Payload.Bytes.Span);
    }

    /// <summary>
    /// Stessa costruzione, sui soli dati che la determinano: modello e confine del workspace. E'
    /// l'unica forma verificabile senza fabbricare un'autorizzazione, che nessun test puo' creare.
    /// </summary>
    /// <param name="model">Modello dichiarato dal registro, quando c'e'.</param>
    /// <param name="workspace">Workspace autorizzato, che decide il confine dei permessi.</param>
    /// <param name="extra">
    /// Argomenti dichiarati dal registro: si sommano a quelli certificati e non li sostituiscono.
    /// Un argomento vietato non arriva fin qui, e se arrivasse verrebbe scartato adesso.
    /// </param>
    public static IReadOnlyList<string> ArgumentsFor(
        string? model,
        WorkspaceDescriptor? workspace,
        IReadOnlyList<string>? extra = null,
        NativeSession? session = null)
    {

        List<string> arguments = [.. BaseArguments];

        // Sempre in modalita' ristretta, anche con un workspace scrivibile: ignora le impostazioni utente, di progetto e
        // locali — un repository clonato con un suo `.claude/settings.json` non puo' aggiungere hook o permessi —,
        // confina gli strumenti sui file alle cartelle di lavoro e rifiuta bypassPermissions (help della versione
        // certificata, evidence/S13). Le scritture sui file restano possibili con acceptEdits.
        arguments.Add("--restricted");

        if (workspace is { Shell: true })
        {
            // I comandi di sistema li ha concessi l'operatore con il recinto, e la modalita' ristretta non registra gli
            // strumenti che li eseguono «unless --tools **names** them»: nominarli e' l'unica forma che li riporta.
            // `--tools default` non li nomina — dice «tutti gli strumenti», che sotto `--restricted` sono gia' quelli
            // senza shell — ed e' stato osservato fallire con il programma reale: il modello ha risposto di non avere
            // ne' Bash ne' PowerShell (evidence/S54). I nomi qui sotto sono quelli scritti nell'help della versione
            // certificata, `--tools <tools...>` («e.g. "Bash,Edit,Read"») e `--restricted` (Bash, WebFetch).
            //
            // Nominare una lista la rende **esclusiva**: la sessione ha questi tre strumenti e non altri. E' il prezzo
            // della shell in modalita' ristretta, ed e' dichiarato in 03-specs/ADAPTERS.md.
            arguments.Add("--tools");
            arguments.Add("Bash,Edit,Read");
            arguments.Add("--allowed-tools");
            arguments.Add("Bash");
            arguments.Add("--disallowed-tools");
            arguments.Add("WebFetch");
        }

        arguments.Add("--permission-mode");
        arguments.Add(workspace is { Writable: true } ? "acceptEdits" : "manual");

        if (workspace is not null)
        {
            arguments.Add("--add-dir");
            arguments.Add(workspace.RootPath);
        }

        if (model is { Length: > 0 })
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        // La configurazione dell'utente non deve poter aggiungere server MCP a un turno governato.
        arguments.Add("--strict-mcp-config");

        if (session is null)
        {
            // Il programma conserva ogni conversazione in chiaro nella propria cartella, a meno che non gli si dica
            // di no. Fino a S116 non glielo si diceva: ogni turno lasciava sul disco richiesta e risposta, anche con
            // `contentMode: memory_only`. Opzione dell'help certificato («only works with --print», che e' il nostro modo).
            arguments.Add("--no-session-persistence");
        }
        else
        {
            // L'identificatore lo ha assegnato il router: mai `--continue`, mai «l'ultima» (ADR-022, S15).
            arguments.Add(session.Resume ? "--resume" : "--session-id");
            arguments.Add(session.Id);
        }

        // Gli argomenti del registro si sommano in coda: nessuno di essi puo' sostituire quelli
        // certificati sopra, che restano nella lista e vengono letti per primi.
        if (extra is not null && AgentArgumentPolicy.FirstForbidden(AdapterKind.ClaudeCodeCli, extra) is null)
        {
            arguments.AddRange(extra);
        }

        return arguments;
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
            // Senza contenimento dell'albero un turno cancellato potrebbe lasciare processi vivi:
            // il controllo manca, e manca prima di avviare qualcosa.
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
                    bool started = false;

                    Task<CliOutcome> RunAsync(NativeSession? session) => CliProcessLauncher.RunAsync(
                        guards,
                        executable,
                        ArgumentsFor(execution.Model, execution.Workspace, execution.ExtraArguments, session),
                        workspace.RootPath,
                        PromptFor(execution, session is { Resume: true }),
                        Merged(),
                        line =>
                        {
                            if (session is { Resume: true } && !started && NothingRan(line))
                            {
                                // L'esito di una sessione che non c'e': non e' un fatto del turno, e non si traduce.
                                return Task.CompletedTask;
                            }

                            started = true;
                            return TranslateAsync(line, events.Writer);
                        },
                        cancellationToken);

                    CliOutcome outcome = await RunAsync(execution.NativeSession).ConfigureAwait(false);

                    if (execution.NativeSession is { Resume: true } lost
                        && outcome.ExitCode is not 0
                        && !started
                        && !cancellationToken.IsCancellationRequested)
                    {
                        // La sessione da proseguire non c'e' piu' — transcript cancellato, cartella di lavoro cambiata.
                        // Il programma lo dice ed esce **prima** di chiedere qualcosa al modello (osservato, evidence/S116):
                        // una sola riga di esito con zero turni e costo zero, nessun effetto. Si riparte con lo stesso
                        // identificatore e il contesto intero, che e' cio' che sarebbe successo al primo turno. Se e'
                        // comparsa qualunque altra riga non si riprova: qualcosa e' partito, e un secondo giro potrebbe
                        // ripeterne gli effetti.
                        outcome = await RunAsync(lost with { Resume = false }).ConfigureAwait(false);
                    }

                    if (outcome.Truncated)
                    {
                        await events.Writer.WriteAsync(
                            new AdapterFailed(ErrorCode.OutputLimit, "The program produced more output than allowed."),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    else if (cancellationToken.IsCancellationRequested)
                    {
                        // Il programma e' uscito perche' e' stato ucciso dalla cancellazione, e un
                        // codice di uscita diverso da zero e' la conseguenza, non la causa.
                        // Classificarlo come «programma non disponibile» direbbe la cosa sbagliata
                        // su un target che funziona benissimo. L'adapter non sa **perche'** sia
                        // stata chiesta la cancellazione - scadenza o operatore - e quella
                        // distinzione appartiene al coordinatore, come in ogni altro percorso.
                        //
                        // Il caso si presenta come corsa: la lettura del codice di uscita puo'
                        // arrivare prima che la cancellazione venga osservata, e sotto carico
                        // succede davvero.
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
    /// Vero per l'esito con cui il programma dice di non aver eseguito nulla: un errore con zero turni. E' cio' che
    /// stampa quando la sessione da proseguire non esiste, prima di qualunque chiamata al modello.
    /// </summary>
    internal static bool NothingRan(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            return root.ValueKind is JsonValueKind.Object
                && root.TryGetProperty("type", out JsonElement type) && type.GetString() == "result"
                && root.TryGetProperty("is_error", out JsonElement error) && error.ValueKind is JsonValueKind.True
                && root.TryGetProperty("num_turns", out JsonElement turns) && turns.TryGetInt32(out int count) && count == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ambiente aggiuntivo. Le variabili di API di altri provider vengono rimosse: un programma che
    /// usa la propria autenticazione nativa non deve trovarne una concorrente.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Environment() =>
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ANTHROPIC_API_KEY"] = null,
            ["ANTHROPIC_AUTH_TOKEN"] = null,
            ["OPENAI_API_KEY"] = null,
            ["CLAUDE_CODE_ENTRYPOINT"] = "pa-router",
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
    /// Traduce una riga di <c>stream-json</c> in eventi normalizzati.
    ///
    /// Conserva soltanto cio' che serve al router: il testo dell'assistente, le modifiche ai file e
    /// l'uso dichiarato. Il ragionamento interno non viene propagato ne' conservato.
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

            if (root.ValueKind is not JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement type))
            {
                return;
            }

            switch (type.GetString())
            {
                case "assistant":
                    await WriteTextAsync(root, writer).ConfigureAwait(false);
                    break;

                case "result":
                    await WriteUsageAsync(root, writer).ConfigureAwait(false);
                    break;

                case "user":
                case "system":
                    // Eventi di contesto: il router non li conserva.
                    break;

                default:
                    break;
            }
        }
    }

    private static async Task WriteTextAsync(JsonElement root, System.Threading.Channels.ChannelWriter<AdapterEvent> writer)
    {
        if (!root.TryGetProperty("message", out JsonElement message)
            || !message.TryGetProperty("content", out JsonElement content)
            || content.ValueKind is not JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind is not JsonValueKind.Object || !block.TryGetProperty("type", out JsonElement blockType))
            {
                continue;
            }

            switch (blockType.GetString())
            {
                case "text" when block.TryGetProperty("text", out JsonElement text) && text.ValueKind is JsonValueKind.String:
                    await writer.WriteAsync(new AdapterOutputChunk(text.GetString()!), CancellationToken.None).ConfigureAwait(false);
                    break;

                case "tool_use" when block.TryGetProperty("input", out JsonElement input):
                    if (input.TryGetProperty("file_path", out JsonElement path) && path.ValueKind is JsonValueKind.String)
                    {
                        // Una modifica dichiarata e' un effetto possibile: viene registrata come tale.
                        await writer.WriteAsync(new AdapterArtifactChanged(path.GetString()!), CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    break;

                default:
                    // thinking e altri blocchi restano fuori: non sono osservazioni del router.
                    break;
            }
        }
    }

    private static async Task WriteUsageAsync(JsonElement root, System.Threading.Channels.ChannelWriter<AdapterEvent> writer)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        long? input = Count(usage, "input_tokens");
        long? output = Count(usage, "output_tokens");

        decimal? cost = root.TryGetProperty("total_cost_usd", out JsonElement value) && value.ValueKind is JsonValueKind.Number
            ? value.GetDecimal()
            : null;

        if (input is not null || output is not null || cost is not null)
        {
            await writer.WriteAsync(new AdapterUsage(new UsageReport(input, output, cost)), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static long? Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.Number
            ? value.GetInt64()
            : null;
}
