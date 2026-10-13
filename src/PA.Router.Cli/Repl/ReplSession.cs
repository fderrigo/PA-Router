using System.CommandLine;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Commands;
using PA.Router.Cli.Output;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Storage;
using PA.Router.Policy;
using PA.Router.Registry.Loading;
using PA.Router.Registry.Probing;
using PA.Router.Runtime.Ipc;

namespace PA.Router.Cli.Repl;

/// <summary>
/// REPL del router: una chat da riga di comando davanti alla stessa decisione di instradamento
/// che usano <c>pa run</c> e l'API.
///
/// Una riga che comincia con <c>/</c> e' un comando del REPL e non lascia mai la macchina. Ogni
/// altra riga e' **testo della richiesta**: attraversa analisi locale, privacy, policy, ranking e
/// dispatch, e finisce al servizio che quella catena ha scelto. Non esiste un percorso che salti
/// quella catena perche' l'invito e' interattivo.
///
/// Non e' un emulatore di terminale ne' un editor a schermo intero: e' un ciclo di righe con
/// un'intestazione e comandi espliciti. Durante un turno mostra solo stati, e la risposta compare
/// quando lo screening e' concluso (03-specs/CLI-TUI.md).
///
/// Parte solo con un terminale interattivo: in automazione si usa <c>run --session</c>, cosi' uno
/// script non resta appeso su un prompt che nessuno leggera'.
/// </summary>
internal sealed class ReplSession
{
    private readonly IReplInput input;
    private readonly ReplInterrupt interruptions;
    private readonly IReplRenderer renderer;
    private readonly ReplChannel channel;
    private readonly PolicyDocument profile;

    /// <summary>Turno in corso, quando ce n'e' uno: serve a Ctrl+C.</summary>
    private CancellationTokenSource? turn;

    /// <summary>Vero se la prossima riga va solo analizzata, senza invocare nulla.</summary>
    private bool explainNext;

    /// <summary>
    /// Servizio di destinazione di un handover gia' chiesto, in attesa dell'obiettivo.
    ///
    /// L'obiettivo si scrive nella riga successiva invece che sulla stessa: una capsule descrive
    /// il lavoro, e comprimerlo in un argomento della riga di comando produrrebbe obiettivi di tre
    /// parole. E' la stessa forma di <c>/explain</c>.
    /// </summary>
    private string? handoverPending;

    /// <summary>
    /// Comando che deve attendere un'operazione asincrona: lo smistamento dei comandi resta
    /// sincrono e lo registra, il ciclo lo esegue subito dopo senza bloccare un thread sull'I/O.
    /// </summary>
    private Func<CancellationToken, Task>? deferred;

    /// <summary>Servizio che ha risposto all'ultimo turno: la barra dice chi ha risposto, non chi rispondera'.</summary>
    private string? answeredBy;

    /// <summary>
    /// Livello di dettaglio della resa, per questa sessione. Si cambia con Ctrl+O o con
    /// <c>/verbose</c>. Non e' una scelta di configurazione: finche' non si sa se qualcuno lo tiene
    /// acceso sempre, un campo nuovo nel contratto sarebbe una promessa senza prove.
    /// </summary>
    private bool verbose;

    /// <summary>
    /// Livello di diagnosi (23 settembre 2026): oltre al racconto, i dati grezzi del turno — fasi con durata ed esito come
    /// li scrive il motore, token, e quali modelli Ollama erano in memoria prima e dopo. Serve a chi prova il router a
    /// capire un tempo senza farselo raccontare.
    /// </summary>
    private bool debugging;

    /// <summary>Modelli Ollama in memoria prima del turno in corso, quando la diagnosi e' accesa.</summary>
    private IReadOnlyList<ResidentModel>? residentBefore;

    /// <summary>Esito dell'ultimo turno, per ristamparlo senza rifare la richiesta.</summary>
    private ReplTurnResult? lastTurn;

    /// <summary>Fasi dell'ultimo turno, gia' rese leggibili.</summary>
    private IReadOnlyList<(string Phase, string Detail)> lastPhases = [];

    /// <summary>L'ultimo turno da raccontare nel livello verboso, con il modo di dire l'esito di una fase.</summary>
    private TurnStory? lastStory;

    /// <summary>
    /// Barra di stato sopra l'invito. Si toglie con <c>/status off</c> e vale per questa sessione:
    /// non e' una scelta di configurazione, perche' un campo nuovo nel contratto si aggiunge quando
    /// si sa quali campi l'operatore tiene e quali toglie.
    /// </summary>
    private bool statusBar = true;

    /// <summary>
    /// L'area fissa in fondo al terminale (ADR-023): riquadro dell'invito e barra di stato. Null
    /// quando il terminale non la regge — senza ANSI, con l'uscita rediretta, dentro un copione — e
    /// allora la resa resta quella a righe di sempre.
    /// </summary>
    internal ReplBottomRegion? region { get; init; }

    /// <summary>
    /// Stato mostrato sopra l'invito. Si ricalcola a ogni invito, mai a ogni tasto: viene dalla
    /// configurazione gia' in mano e non tocca lo store, che con un worker vivo non e' nostro.
    /// </summary>
    private StatusView Status() => ReplStatus.For(
        Snapshot,
        ProfileId,
        answeredBy,
        fence is SessionFence active ? new WorkspaceDescriptor(active.WorkspaceRoot, active.Writable, active.ShellAllowed) : null);

    /// <summary>
    /// Cambia il livello di dettaglio. Se l'ultimo turno e' stato **negato o si e' fermato** lo ristampa subito:
    /// altrimenti per vedere i dettagli di un rifiuto appena arrivato bisognerebbe rifare la richiesta, cioe'
    /// pagarla una seconda volta.
    ///
    /// Un turno riuscito non si ristampa, e con un turno **in corso** non si ristampa niente (S117): il racconto del
    /// turno precedente finiva fra la domanda appena scritta e la sua risposta, con lo stesso titolo, e sembrava lo
    /// stesso turno raccontato due volte. Il turno in corso si racconta alla fine; il precedente si rivede con /why.
    /// </summary>
    private void SetVerbose(bool value)
    {
        verbose = value;

        // La barra dice il livello: se aspettasse l'invito successivo, Ctrl+O sembrerebbe non fare
        // niente proprio nel momento in cui lo si preme per vedere un effetto.
        if (region is not null)
        {
            region.Verbose = verbose;
            region.Refresh();
        }

        renderer.Info(Ui.Text(verbose ? "verbose.on" : "verbose.off"));

        if (turn is null
            && lastTurn is not null
            && (lastTurn.Denial is not null || !string.Equals(lastTurn.State, ExecutionState.Completed.ToWire(), StringComparison.Ordinal)))
        {
            Why();
        }
    }

    /// <summary>
    /// Come si chiama un servizio per chi legge: nome dell'istanza e modello, mai l'identificatore.
    /// L'identificatore resta opaco perche' ci si appoggiano conferme e permessi; a schermo ci va il
    /// nome (S117).
    /// </summary>
    private string Named(string serviceId) => ServiceNames.Display(Snapshot, serviceId);

    /// <summary>Ristampa la spiegazione dell'ultimo turno, senza rimandare niente a nessuno.</summary>
    private void Why()
    {
        if (lastTurn is null)
        {
            renderer.Info(Ui.Text("why.nothing"));
            return;
        }

        if (lastTurn.Denial is DenialDetail denial) { ReplDenialView.Write(renderer, denial, verbose, Named, quotaOf: Output.QuotaText.StateOf(Snapshot.DataDirectory)); }

        // /why e' una richiesta esplicita di spiegazione: il racconto si stampa sempre, anche nel livello breve (25 settembre
        // 2026, prova reale: dopo un turno riuscito il comando non stampava niente). Rivisto dopo, il racconto dice di quale
        // turno parla: stampato a ridosso di una richiesta nuova sembrava suo.
        if (lastPhases.Count > 0) { WritePhases("narr.titlePrevious"); }
    }

    /// <summary>Ultimo stato della configurazione detto all'operatore: si ripete solo quando cambia.</summary>
    private ConfigurationState shownState = ConfigurationState.Current;

    /// <summary>Recinto della sessione, fissato con <c>/workspace</c>. Senza, ogni riga resta una chat.</summary>
    private SessionFence? fence;

    /// <summary>Vero dopo che l'operatore ha confermato la prima scrittura della sessione.</summary>
    private bool writeConfirmed;

    /// <summary>
    /// Consumo dichiarato dai servizi in questa sessione (S59). Vive quanto la sessione, non quanto
    /// il turno: e' la somma che <c>/cost</c> mostra.
    /// </summary>
    private ReplUsageTotals totals = new();

    /// <summary>Vero se la risposta del turno appena eseguito e' gia' comparsa a blocchi (S58).</summary>
    private bool shownAsBlocks;

    /// <summary>
    /// Richieste scritte **mentre un turno girava** (S65).
    ///
    /// Entrano nel turno successivo, mai in quello gia' autorizzato: il turno in corso e' partito con
    /// un piano, delle conferme e una classe di dati decisi su cio' che era stato scritto allora, e
    /// aggiungerci del testo dopo significherebbe eseguire una richiesta che nessuno ha autorizzato.
    /// </summary>
    private readonly Queue<string> queued = new();

    /// <summary>
    /// Durata oltre la quale un turno concluso avvisa con un segnale acustico.
    ///
    /// Sotto questa soglia il segnale sarebbe rumore: chi ha appena scritto sta ancora guardando lo
    /// schermo. Sopra, verosimilmente e' andato a fare altro.
    /// </summary>
    private static readonly TimeSpan NotifyAfter = TimeSpan.FromSeconds(10);

    /// <summary>Testo gia' comparso a blocchi: serve a mostrare solo la coda trattenuta.</summary>
    private string blocksShown = string.Empty;

    /// <summary>
    /// Ingresso a righe. Resta la forma che usano le prove e l'automazione: una pipe non compone
    /// righe a tasti, e pretenderlo qui significherebbe provare il REPL in una forma che nessuno
    /// esegue.
    /// </summary>
    internal ReplSession(
        TextReader input,
        IReplRenderer renderer,
        ConfigurationSnapshot snapshot,
        ReplChannel channel,
        PolicyDocument profile)
        : this(new TextReaderReplInput(input), renderer, snapshot, channel, profile)
    {
        ArgumentNullException.ThrowIfNull(input);
    }

    internal ReplSession(
        IReplInput input,
        IReplRenderer renderer,
        ConfigurationSnapshot snapshot,
        ReplChannel channel,
        PolicyDocument profile)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(profile);

        this.input = input;
        interruptions = input.Interruptions ?? ReplTerminalScope.Current?.Interruptions ?? new();
        this.renderer = renderer;
        this.channel = channel;
        this.profile = profile;

        ProfileId = profile.ProfileId;
    }

    /// <summary>Profilo attivo.</summary>
    internal string ProfileId { get; }

    /// <summary>
    /// Configurazione in uso. Non e' una copia presa all'avvio: il canale la allinea ai file prima di
    /// ogni riga, e un ordine dei modelli scritto dal setup o da un altro terminale vale dalla riga dopo.
    /// </summary>
    private ConfigurationSnapshot Snapshot => channel.Snapshot;

    /// <summary>Target imposto dall'operatore, se presente.</summary>
    internal string? TargetId { get; private set; }

    /// <summary>
    /// Skill scelta dall'operatore per i turni successivi, se presente. Entra nel payload come
    /// testo selezionato e non concede permessi.
    /// </summary>
    internal string? SkillId { get; private set; }

    /// <summary>
    /// Sessione della conversazione.
    ///
    /// Viene scelta **prima** del primo turno e non cambia: e' cio' che fa di una sequenza di
    /// righe una conversazione invece di N richieste scollegate. Il primo turno la crea, gli altri
    /// la continuano e ritrovano la cronologia.
    /// </summary>
    internal string SessionId { get; private set; } = Guid.NewGuid().ToString("D", Formats.Invariant);

    /// <summary>Costruisce e avvia il REPL su una configurazione valida.</summary>
    /// <summary>
    /// Lo stesso REPL con le righe lette da un testo invece che da tastiera (23 settembre 2026): richieste e comandi, uno
    /// per riga, resi in testo semplice. Serve a chi prova il router a fare le domande e leggere le risposte complete —
    /// racconto e diagnosi compresi — senza un terminale. Nessuna conferma interattiva: dove il REPL chiederebbe, la
    /// risposta e' no, come per ogni ingresso rediretto.
    /// </summary>
    /// <summary>L'etichetta indicata, oppure la predefinita; null, con il motivo scritto, se non esiste.</summary>
    private static PolicyDocument? ChosenProfile(ConfigurationSnapshot snapshot, string? profileId, TextWriter error)
    {
        string id = string.IsNullOrWhiteSpace(profileId) ? snapshot.Config.DefaultProfileId : profileId.Trim();

        if (snapshot.Profiles.TryGetValue(id, out PolicyDocument? profile)) { return profile; }

        error.WriteLine(Ui.Format("repl.profileUnknown {0} {1}", id, string.Join(", ", snapshot.Profiles.Keys.Order(StringComparer.Ordinal))));
        return null;
    }

    internal static async Task<int> RunScriptAsync(
        ConfigurationSnapshot snapshot,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        string? profileId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Ui.SetLanguage(snapshot.Config.Language);

        if (ChosenProfile(snapshot, profileId, error) is not PolicyDocument profile)
        {
            return ExitCodes.InvalidInput;
        }

        ReplChannel channel;

        try
        {
            channel = await ReplChannel.OpenAsync(snapshot, profile, cancellationToken).ConfigureAwait(false);
        }
        catch (PaRouterException exception)
        {
            error.WriteLine(Ui.Text("Impossibile aprire la directory dati: ") + exception.Code.ToWire());
            return ExitCodes.ForStorage(exception.Code);
        }

        await using (channel)
        {
            ReplSession session = new(input, new PlainReplRenderer(output, error), snapshot, channel, profile);
            return await session.LoopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<int> RunAsync(
        ParseResult parseResult,
        ConfigurationSnapshot snapshot,
        string? resumeSessionId,
        CancellationToken cancellationToken,
        string? profileId = null)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(snapshot);
        Ui.SetLanguage(snapshot.Config.Language);

        TextWriter output = parseResult.InvocationConfiguration.Output;
        TextWriter error = parseResult.InvocationConfiguration.Error;

        // Un'etichetta che non esiste si rifiuta, non si sostituisce con la predefinita: il REPL non parte con regole scelte
        // da lui (la stessa regola di `pa run`).
        if (ChosenProfile(snapshot, profileId, error) is not PolicyDocument profile)
        {
            return ExitCodes.InvalidInput;
        }

        ReplChannel channel;

        try
        {
            channel = await ReplChannel.OpenAsync(snapshot, profile, cancellationToken).ConfigureAwait(false);
        }
        catch (PaRouterException exception)
        {
            error.WriteLine(Ui.Text("Impossibile aprire la directory dati: ") + exception.Code.ToWire());
            return ExitCodes.ForStorage(exception.Code);
        }

        using ReplBottomRegion bottom = new(AnsiConsoleOrNull(), output, ReplTerminalScope.Current?.Keys);
        TextWriter surface = bottom.Writer;

        await using (channel)
        {
            ReplSession session = new(Input(surface), Renderer(surface, error), snapshot, channel, profile)
            {
                region = bottom.Enabled ? bottom : null,
            };

            if (resumeSessionId is not null)
            {
                session.SessionId = resumeSessionId;
            }

            return await session.LoopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resa da usare. Senza terminale o con <c>NO_COLOR</c> niente pannelli ne' animazioni: la
    /// specifica lo impone e un pannello ridisegnato dentro una pipe sarebbe illeggibile.
    /// </summary>
    /// <summary>
    /// Comandi del REPL, per il completamento con Tab.
    ///
    /// E' lo stesso elenco che <see cref="Handle"/> riconosce e che <c>/help</c> mostra: un
    /// completamento che proponesse un comando inesistente sarebbe peggio di nessun completamento.
    /// </summary>
    /// <summary>
    /// Comandi riconosciuti ma non proposti dal menu (24 settembre 2026): gli omonimi dei comandi di `pa` che nel REPL hanno
    /// gia' un nome — `/session`, `/plugin`, `/registry` — e quelli che si usano fuori da una conversazione. Esistono per la
    /// regola dell'operatore «ogni `pa nome` ha il suo `/nome`»; nel menu farebbero scivolare in basso i comandi di tutti i
    /// giorni (`/se` proponeva `/serve` prima di `/sessions`).
    /// </summary>
    internal static IReadOnlyList<string> Unlisted { get; } =
    [
        "/attach", "/completion", "/init", "/maintenance", "/plugin", "/registry", "/run", "/runtime",
        "/script", "/serve", "/session",
    ];

    internal static IReadOnlyList<string> Commands { get; } =
    [
        "/archive",
        "/audit",
        "/clear",
        "/config",
        "/context",
        "/cost",
        "/debug",
        "/diff",
        "/doctor",
        "/exit",
        "/explain",
        "/fork",
        "/handover",
        "/help",
        "/jobs",
        "/memory",
        "/model",
        "/models",
        "/name",
        "/new",
        "/plugins",
        "/policy",
        "/resume",
        "/services",
        "/sessions",
        "/setup",
        "/skill",
        "/status",
        "/verbose",
        "/why",
        "/target",
        "/tools",
        "/transcript",
        "/undo",
        "/vim",
        "/workspace",
    ];

    /// <summary>
    /// Ingresso da usare. Con un terminale la riga si compone a tasti — cronologia, completamento,
    /// multilinea, incolla come una sola richiesta; con l'ingresso rediretto resta la lettura a
    /// righe, perche' in una pipe non ci sono tasti da comporre.
    /// </summary>
    private static IReplInput Input(TextWriter output)
    {
        if (!PaRootCommand.Interactive) { return new TextReaderReplInput(Console.In); }

        Spectre.Console.IAnsiConsole surface = Surface(output);

        return new ConsoleReplInput(
            ReplTerminalScope.Current?.Keys ?? new ConsoleKeySource(),
            output,
            new ReplCompletion(Commands),
            new ReplHistory(),
            Console.Clear,
            new ReplCommandMenu(surface),
            Environment.GetEnvironmentVariable("NO_COLOR") is null ? surface : null,
            new ReplFileMenu(surface));
    }

    /// <summary>
    /// Una console Spectre che scrive sul flusso indicato invece che sulla console vera. Le dimensioni
    /// restano quelle del terminale: servono a impaginare, e prenderle da un flusso darebbe ottanta
    /// colonne fisse.
    /// </summary>
    private static Spectre.Console.IAnsiConsole Surface(TextWriter output)
    {
        Spectre.Console.IAnsiConsole real = Spectre.Console.AnsiConsole.Console;

        Spectre.Console.IAnsiConsole surface = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Ansi = real.Profile.Capabilities.Ansi ? Spectre.Console.AnsiSupport.Yes : Spectre.Console.AnsiSupport.No,
            ColorSystem = Spectre.Console.ColorSystemSupport.Detect,
            Out = new Spectre.Console.AnsiConsoleOutput(output),
        });

        surface.Profile.Width = real.Profile.Width;
        surface.Profile.Height = real.Profile.Height;

        return surface;
    }

    private static IReplRenderer Renderer(TextWriter output, TextWriter error) =>
        PaRootCommand.Interactive && Environment.GetEnvironmentVariable("NO_COLOR") is null
            ? new SpectreReplRenderer(Surface(output), output, error)
            : new PlainReplRenderer(output, error);

    internal async Task<int> LoopAsync(CancellationToken cancellationToken)
    {
        renderer.Welcome(ProfilePresentation.Name(Snapshot, ProfileId), channel.Location);

        // L'analizzatore si scalda mentre l'operatore scrive la prima domanda (ADR-031 D): a freddo quel turno aspetterebbe
        // quindici secondi di lettura del prompt di sistema, e nel modo decisivo oltre il limite diventava un profilo prudente
        // che mandava «qual e' la capitale della Francia?» al modello piu' grande. Vale anche per `pa script`.
        ConfigurationSnapshot warming = Snapshot;
        _ = Task.Run(
            () => channel is LocalReplChannel local ? TurnWarmUp.RunAsync(local.Stack, cancellationToken) : TurnWarmUp.RunAsync(warming, cancellationToken),
            cancellationToken);

        if (Snapshot.Config.ContentMode is not ContentMode.Encrypted)
        {
            // In memory_only i turni non vengono conservati, quindi il turno successivo non
            // ritrova il precedente: ogni riga e' una richiesta a se'. E' una scelta di
            // riservatezza e resta quella dell'operatore, ma una chat che non lo dicesse farebbe
            // credere a un contesto che non esiste.
            // Una frase sola, e il posto dove si cambia (24 settembre 2026): prima nominava `memory_only` e mandava a
            // rifare `pa init`, mentre la scelta si cambia dal setup.
            renderer.Warn(Ui.Text("repl.notKept"));
        }
        else if (MemoryQueries.NeverRecords(Snapshot))
        {
            // Detto all'avvio: altrimenti si scopre dopo settimane, cercando qualcosa che non e' mai stato scritto.
            renderer.Warn(Ui.Text("memory.neverRecords"));
        }
        else if (MemoryQueries.LexicalOnly(Snapshot))
        {
            // La conversazione funziona lo stesso: i ricordi si ritrovano per parole. Chi si
            // aspetta una ricerca per significato deve saperlo prima, non dedurlo dai risultati.
            renderer.Warn(
                Ui.Text("Memoria senza modello di embedding: i ricordi si ritrovano solo per parole. ")
                + Ui.Text("Per una ricerca per significato configura memory.embeddingServiceId."));
        }

        // Una conferma non scade (ADR-032, fase 4): data da un anno o piu', si ricorda di ricontrollarla. Continua a valere.
        foreach (ConfirmationReminder reminder in ServiceAttestations.Reminders(Snapshot.Registry.Services, SystemClock.Instance.UtcNow))
        {
            renderer.Warn(Ui.Format(
                "doctor.confirmationReminder {0} {1} {2}",
                Named(reminder.ServiceId),
                reminder.Control,
                reminder.ConfirmedAt.ToString("yyyy-MM-dd", Formats.Invariant)));
        }

        // Un servizio in pausa per quota finita si dice all'apertura (24 settembre 2026): chi lo aspettava vedrebbe
        // rispondere un altro modello senza sapere perche'.
        foreach (QuotaPause pause in QuotaPauses.Active(StorePaths.For(Snapshot.DataDirectory), SystemClock.Instance.UtcNow))
        {
            renderer.Warn(Output.QuotaText.Paused(Named(pause.ServiceId), pause));
        }

        interruptions.CancelCurrent = () =>
        {
            try { turn?.Cancel(); }
            catch (ObjectDisposedException) { /* Turn completed concurrently. */ }
        };

        if (region is not null)
        {
            if (input is ConsoleReplInput console) { console.Region = region; }
            renderer.Framed();
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested && !interruptions.ExitRequested)
            {
                input.ProfileName = ProfilePresentation.Name(Snapshot, ProfileId);
                input.Status = statusBar ? Status() : null;
                input.ToggleVerbose = () => SetVerbose(!verbose);

                if (region is not null)
                {
                    region.Status = statusBar ? Status() : null;
                    region.Verbose = verbose;
                }
                if (!input.OwnsPrompt)
                {
                    // Con un terminale l'invito lo ridisegna il compositore di riga a ogni tasto:
                    // stamparlo anche qui lascerebbe due inviti sulla stessa riga.
                    renderer.Prompt();
                }

                // Cio' che e' stato scritto mentre il turno precedente girava viene prima: e' la
                // richiesta successiva, nell'ordine in cui e' stata scritta (S65).
                string? line;

                if (queued.Count > 0)
                {
                    line = queued.Dequeue();
                    renderer.Info(Ui.Text("Dalla coda: ") + line);
                }
                else
                {
                    line = ReadInput();
                }

                if (line is null)
                {
                    // Fine dello standard input: il REPL termina come farebbe con /exit.
                    return ExitCodes.Success;
                }

                line = line.Trim();

                if (line.Length == 0)
                {
                    continue;
                }

                // Prima di ogni riga la configurazione si allinea ai file: una richiesta o un comando
                // non usano mai una regola che l'operatore ha gia' cambiato.
                ObserveConfiguration();

                if (line.StartsWith('/'))
                {
                    if (Handle(line, out int exitCode))
                    {
                        return exitCode;
                    }

                    if (deferred is Func<CancellationToken, Task> action)
                    {
                        deferred = null;

                        try
                        {
                            await action(cancellationToken).ConfigureAwait(false);
                        }
                        catch (PaRouterException exception)
                        {
                            // Una lettura rifiutata — memoria o estensioni disattivate, profilo
                            // sconosciuto — si dichiara e il REPL resta vivo.
                            renderer.Warn(Ui.Text("Comando non eseguito: ") + exception.Code.ToWire());
                        }
                    }

                    continue;
                }

                await TurnWhileTypingAsync(line, cancellationToken).ConfigureAwait(false);
            }

            return interruptions.ExitRequested ? ExitCodes.Success : ExitCodes.Cancelled;
        }
        finally
        {
            interruptions.CancelCurrent = null;
        }
    }

    /// <summary>La console ANSI, quando il terminale la offre e i colori non sono stati spenti.</summary>
    private static Spectre.Console.IAnsiConsole? AnsiConsoleOrNull() =>
        PaRootCommand.Interactive && Environment.GetEnvironmentVariable("NO_COLOR") is null
            ? Spectre.Console.AnsiConsole.Console
            : null;

    /// <summary>
    /// Esegue il turno e intanto lascia scrivere (S65).
    ///
    /// Cio' che l'operatore scrive mentre il turno gira **non entra in quel turno**: finisce in coda
    /// ed e' la richiesta successiva. Il turno in corso e' partito con un piano, delle conferme e una
    /// classe di dati decisi su cio' che era stato scritto allora; aggiungerci testo dopo
    /// significherebbe eseguire qualcosa che nessuno ha autorizzato, ed e' il gate di questo step.
    ///
    /// Non e' nemmeno una correzione a meta' turno: gli adapter non interattivi ricevono il prompt
    /// una volta sola, e due turni insieme sulla stessa sessione sono vietati dall'ammissione di S49.
    /// Chi vuole cambiare idea cancella con Ctrl+C e riscrive — la riga che ha gia' digitato e'
    /// **gia' in coda**, quindi non la perde.
    ///
    /// Senza terminale non c'e' nessuno che digita: <c>Typing</c> resta falsa e questo percorso si
    /// comporta come prima, cioe' attende il turno e basta.
    /// </summary>
    private string? ReadInput(bool remember = true)
    {
        renderer.BeginInput();
        try { return input.Read(remember); }
        finally { renderer.EndInput(); }
    }

    private async Task TurnWhileTypingAsync(string text, CancellationToken cancellationToken)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;

        renderer.BeginTurn();

        // Che il turno sia in corso lo dice un pallino che lampeggia sotto la conversazione (S117).
        if (region is not null) { region.Busy = true; }

        try
        {
            Task turn = TurnAsync(text, cancellationToken);

            while (!turn.IsCompleted)
            {
                if (!interruptions.ExitRequested && input.Typing && ReadInput(remember: true) is string typed && typed.Trim().Length > 0)
                {
                    queued.Enqueue(typed.Trim());
                    renderer.Info(Ui.Text("In coda per il turno successivo: ") + typed.Trim());

                    continue;
                }

                // Senza tasti si aspetta il turno, con un risveglio breve per tornare a guardare.
                await Task.WhenAny(turn, Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken))
                    .ConfigureAwait(false);
            }

            await turn.ConfigureAwait(false);

            Notify(DateTimeOffset.UtcNow - started);
        }
        finally
        {
            if (region is not null) { region.Busy = false; }
            renderer.EndTurn();
        }
    }

    /// <summary>
    /// Avvisa che un turno lungo e' finito.
    ///
    /// Un segnale acustico e nient'altro: nessuna notifica di sistema, che richiederebbe un permesso
    /// e un canale che questo prodotto non ha. Senza terminale non suona niente.
    /// </summary>
    private void Notify(TimeSpan elapsed)
    {
        if (elapsed < NotifyAfter || !PaRootCommand.Interactive)
        {
            return;
        }

        try
        {
            Console.Write('\a');
        }
        catch (IOException)
        {
            // Un terminale che non sa suonare non e' un guasto del turno.
        }
    }

    /// <summary>
    /// Un turno completo. Durante l'attesa compaiono solo stati: la risposta si mostra quando lo
    /// screening e' concluso, mai prima.
    /// </summary>
    private async Task TurnAsync(string text, CancellationToken cancellationToken)
    {
        if (explainNext)
        {
            explainNext = false;

            try
            {
                await ExplainAsync(text, cancellationToken).ConfigureAwait(false);
            }
            catch (PaRouterException exception)
            {
                renderer.Warn(Ui.Text("Analisi non eseguita: ") + exception.Code.ToWire());
            }

            return;
        }

        if (handoverPending is string destination)
        {
            handoverPending = null;

            await HandoverAsync(destination, text, cancellationToken).ConfigureAwait(false);

            return;
        }

        using CancellationTokenSource running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        turn = running;

        try
        {
            // Il router classifica la riga e sceglie il minimo dentro il recinto; le conferme le chiede
            // il REPL, che e' l'unico a parlare con l'operatore (ADR-019).
            long classifyStart = System.Diagnostics.Stopwatch.GetTimestamp();
            TurnPlan plan = await TurnPlanning
                .PlanAsync(Snapshot, ProfileId, text, fence, writeConfirmed, running.Token)
                .ConfigureAwait(false);

            // Il modello del classificatore puo' dover entrare in memoria: e' tempo del router, e il racconto lo dice.
            TimeSpan classifying = System.Diagnostics.Stopwatch.GetElapsedTime(classifyStart);

            // Nello schermo del REPL la classificazione non si stampa mentre il turno parte (S117): la racconta il
            // livello verboso, con chi l'ha decisa e perche'. Senza schermo resta la riga di avanzamento.
            if (region is null) { renderer.Phase(Ui.Text("Classificazione: ") + PlanText.Describe(plan)); }

            foreach (string limit in plan.Limits)
            {
                renderer.Info("  " + limit);
            }

            if (plan.NeedsWriteConfirmation)
            {
                ReplChoice choice = Confirm(
                    Ui.Text("Stai per autorizzare la scrittura di file. Ecco che cosa comporta:"),
                    ReplConfirmation.Write(plan));

                if (choice is ReplChoice.No)
                {
                    renderer.Warn(Ui.Text("Modifica non confermata: nessuna richiesta inviata."));
                    return;
                }

                // «Sempre» e' l'unico modo di non risentire la domanda: un si' vale per questa
                // richiesta, e la volta dopo si torna a chiedere.
                writeConfirmed = choice is ReplChoice.Always;
            }

            if (plan.NeedsShellConfirmation
                && Confirm(
                    Ui.Text("Stai per autorizzare l'esecuzione di comandi di sistema. Ecco che cosa comporta:"),
                    ReplConfirmation.Shell(plan)) is ReplChoice.No)
            {
                renderer.Warn(Ui.Text("Comandi di sistema non confermati: nessuna richiesta inviata."));
                return;
            }

            // Le fasi arrivano una per una con esito e durata, e alla fine il riepilogo: la riga fissa che le
            // elencava tutte e cinque senza dirne nessuna e' stata tolta (S116).

            // Chi guarda il turno: le fasi arrivano con la loro durata, e la risposta compare a
            // blocchi **gia' esaminati** (S58). Nulla raggiunge lo schermo senza essere passato
            // dallo screening: e' la regola di rilascio di 03-specs/PRIVACY.md.
            // Nel livello verboso il consumo lo dice il racconto: stamparlo anche qui era la stessa riga due volte.
            ReplTurnObserver watcher = new(renderer, totals, Named) { ShowPhases = region is null, ShowUsage = !verbose };

            // Cio' che l'operatore ha allegato con `@percorso` e' **contenuto**: entra nel payload
            // come dato con la sua provenienza e riattraversa privacy e policy come ogni altro testo
            // (S63). La menzione resta nella richiesta: toglierla cambierebbe la domanda scritta.
            IReadOnlyList<ContextItem>? attachments = Attach(text);

            residentBefore = debugging ? await OllamaResidency.ObserveAsync(Snapshot, running.Token).ConfigureAwait(false) : null;

            ReplTurnResult result = await channel
                .RunAsync(text, SessionId, TargetId, SkillId, plan.Capabilities, plan.Workspace, running.Token, watcher, attachments)
                .ConfigureAwait(false);

            // Il profilo sottopone a conferma l'uscita dal perimetro (ADR-029, decisione I): il turno e' stato negato
            // prima di qualunque invio, e qui c'e' qualcuno a cui chiedere. Un si' vale per questa richiesta soltanto;
            // il turno si ripete con la conferma, e nessun byte e' uscito prima della risposta.
            if (ReplConfirmation.ExternalServices(result) is { Count: > 0 } outside)
            {
                SessionId = result.SessionId;

                if (Confirm(
                        Ui.Text("Il tuo uso chiede una conferma prima di mandare questi dati fuori dal perimetro. Ecco che cosa comporta:"),
                        ReplConfirmation.External(outside, Named)) is ReplChoice.No)
                {
                    renderer.Warn(Ui.Text("Uscita non confermata: nessun dato inviato."));
                    return;
                }

                watcher = new ReplTurnObserver(renderer, totals, Named) { ShowPhases = region is null, ShowUsage = !verbose };
                result = await channel
                    .RunAsync(text, SessionId, TargetId, SkillId, plan.Capabilities, plan.Workspace, running.Token, watcher, attachments, externalConfirmed: true)
                    .ConfigureAwait(false);
            }

            SessionId = result.SessionId;
            answeredBy = watcher.ChosenServiceId ?? answeredBy;
            lastPhases = watcher.Rendered;
            lastStory = new TurnStory(
                plan.Classification,
                watcher.Phases,
                watcher.Usage,
                result.MemoryInjected,
                result.MemoryBytes,
                Snapshot.Config.Limits.HistoryTurns,
                watcher.Elapsed,
                classifying);
            shownAsBlocks = watcher.ShownAnything;
            blocksShown = watcher.Shown;

            // Nel livello verboso i ricordi entrati li dice il racconto.
            if (result.MemoryInjected > 0 && !verbose)
            {
                renderer.Event(Ui.Format(
                    result.MemoryInjected == 1 ? "memory.injectedOne {0}" : "memory.injected {0} {1}",
                    result.MemoryInjected == 1
                        ? result.MemoryBytes.ToString(Formats.Invariant)
                        : result.MemoryInjected.ToString(Formats.Invariant),
                    result.MemoryBytes.ToString(Formats.Invariant)));
            }

            // Il riepilogo in una riga non c'e' piu' (S117): nel livello verboso il turno si racconta per intero.
            Present(result);

            if (debugging) { await WriteDebugAsync(result).ConfigureAwait(false); }
        }
        catch (OperationCanceledException)
        {
            renderer.Warn(Ui.Text("Turno cancellato."));
        }
        catch (PaRouterException exception)
        {
            renderer.Error(Ui.Text("Turno non eseguito: ") + exception.Code.ToWire());

            // Una conversazione nata sotto un'altra modalita' non si continua: il codice da solo non dice che cosa fare.
            if (exception.Metadata.TryGetValue("reason", out string? reason) && reason is "session_profile_mismatch")
            {
                renderer.Warn("  " + Ui.Text(TurnRunner.SessionProfileMismatch));
            }
        }
        finally
        {
            turn = null;
        }
    }

    /// <summary>
    /// Mostra l'esito. Su deny la specifica chiede una ragione concreta e i target esclusi, mai il
    /// suggerimento di abbassare la privacy: il rimedio e' cambiare la regola, consapevolmente.
    /// </summary>
    private void Present(ReplTurnResult result)
    {
        // Se la risposta e' gia' comparsa a blocchi, qui si mostra solo la coda trattenuta: la
        // finestra di ritenzione non arriva mai fino in fondo da sola. Ristampare tutto
        // significherebbe farla leggere due volte.
        string? answer = result.Output;

        if (shownAsBlocks && result.Output is string streamed && streamed.Length > 0)
        {
            string shown = blocksShown;

            // La coda trattenuta e' l'unica cosa che resta da mostrare: la finestra di ritenzione
            // non arriva mai fino in fondo da sola, e ristampare tutto la farebbe leggere due volte.
            // Se il testo finale non comincia con cio' che e' comparso — puo' succedere con la
            // redazione applicata all'intero — si ripete: meglio ripetere che mostrare meno.
            answer = streamed.StartsWith(shown, StringComparison.Ordinal)
                ? streamed[shown.Length..]
                : streamed;

            shownAsBlocks = false;
            blocksShown = string.Empty;
        }

        ReplRendering.Present(renderer, result, answer, verbose, Named, Output.QuotaText.StateOf(Snapshot.DataDirectory));

        // L'ultimo turno resta a portata di `/why` e di Ctrl+O: ristampare una spiegazione non deve
        // costare una seconda richiesta a nessuno.
        lastTurn = result;

        if (verbose && lastPhases.Count > 0) { WritePhases("narr.title"); }
    }

    /// <summary>
    /// I dati grezzi del turno, per la diagnosi: nessuna frase, nessun contenuto oltre a cio' che il turno ha gia' mostrato.
    /// Le fasi sono le coppie chiave=valore del motore, cosi' come sono; i modelli Ollama dicono chi era in memoria prima e
    /// chi dopo, con la finestra — un modello ricaricato fra due turni si vede qui.
    /// </summary>
    private async Task WriteDebugAsync(ReplTurnResult result)
    {
        IReadOnlyList<ResidentModel> after = await OllamaResidency.ObserveAsync(Snapshot, CancellationToken.None).ConfigureAwait(false);
        List<string> lines = ["── diagnosi ──"];

        if (lastStory is { } story)
        {
            lines.Add("totale " + Ms(story.Elapsed) + " ms · classificazione " + Ms(story.Classifying) + " ms");

            foreach ((string phase, string outcome, TimeSpan elapsed) in story.Phases)
            {
                lines.Add("fase " + phase + " · " + Ms(elapsed) + " ms · " + outcome);
            }

            if (story.Usage is { } usage)
            {
                lines.Add("uso · ingresso " + (usage.InputTokens?.ToString(Formats.Invariant) ?? "?") + " token · uscita "
                    + (usage.OutputTokens?.ToString(Formats.Invariant) ?? "?") + " token"
                    + (usage.LoadMilliseconds is long load ? " · caricamento " + load.ToString(Formats.Invariant) + " ms" : string.Empty)
                    + " · ragionamento nascosto "
                    + (usage.ReasoningCharacters?.ToString(Formats.Invariant) ?? "0") + " caratteri");
            }

            lines.Add("memoria · " + story.MemoryInjected.ToString(Formats.Invariant) + " ricordi, " + story.MemoryBytes.ToString(Formats.Invariant)
                + " byte · cronologia fino a " + story.HistoryTurns.ToString(Formats.Invariant) + " turni");

            if (story.Classification is { } classification)
            {
                // I campi, non il ToString del record: quello stampava il nome del tipo della lista al posto dei segnali.
                lines.Add("classificazione · " + classification.Intent.ToWire() + " · shell " + (classification.NeedsShell ? "sì" : "no")
                    + " · banda minima " + classification.BandFloor.ToWire() + " · " + classification.Version
                    + (classification.Signals.Count == 0 ? string.Empty : " · segnali " + string.Join(", ", classification.Signals)));
            }
        }

        lines.Add("esito · " + result.State + (result.ErrorCode is string code ? " · " + code : string.Empty)
            + " · risposto da " + (answeredBy ?? "-")
            + (result.WeakPerimeter ? " · perimetro debole" : string.Empty)
            + (result.BelowDemand ? " · sotto il livello richiesto" : string.Empty)
            + " · risposta " + (result.Output?.Length.ToString(Formats.Invariant) ?? "0") + " caratteri");

        lines.AddRange(result.Reasons.Select(reason => "escluso · " + reason));
        lines.Add("ollama prima · " + Resident(residentBefore));
        lines.Add("ollama dopo  · " + Resident(after));

        foreach (string line in lines) { renderer.Info(line); }

        static string Ms(TimeSpan span) => ((long)span.TotalMilliseconds).ToString(Formats.Invariant);

        static string Resident(IReadOnlyList<ResidentModel>? models) => models is null
            ? "non letto"
            : models.Count == 0
                ? "nessun modello"
                : string.Join(", ", models.Select(model => model.Model + " (" + (model.Bytes / 1024d / 1024 / 1024).ToString("0.0", Formats.Invariant)
                    + " GB, finestra " + (model.ContextTokens?.ToString(Formats.Invariant) ?? "?")
                    + (model.ExpiresAt is { } expires ? ", scade " + expires.ToLocalTime().ToString("HH:mm:ss", Formats.Invariant) : string.Empty) + ")"));
    }

    /// <summary>
    /// Come e' andato il turno, riga per riga. Durante il turno le fasi vivono in **una riga sola**
    /// che si riscrive e che l'invito cancella: e' il disegno giusto mentre si aspetta, ed e' il
    /// motivo per cui la giustificazione della scelta spariva appena si ricominciava a scrivere.
    /// Qui le stesse fasi diventano righe permanenti, che restano nello scrollback.
    /// </summary>
    private void WritePhases(string title)
    {
        // Il turno raccontato (S117): per ogni decisione chi l'ha presa e perche', con i modelli per nome.
        if (lastStory is { } story)
        {
            renderer.Story(Ui.Text(title), ReplTurnNarrative.Sections(story, new StoryLookups(Named, DetectorName, Answers, CalibrationOrigin)));
            return;
        }

        renderer.Info(Ui.Text("why.phases"));

        foreach ((string phase, string detail) in lastPhases)
        {
            renderer.Info("  " + phase + (detail.Length == 0 ? string.Empty : ": " + detail));
        }
    }

    /// <summary>
    /// Il nome del modello di un rilevatore, dalla versione che il motore riporta; null per i deterministici. Il
    /// rilevatore a modello si chiama come il servizio che lo porta nel registro, cioe' con il modello installato.
    /// </summary>
    private bool Answers(string serviceId) =>
        Snapshot.Registry.Services.FirstOrDefault(service => string.Equals(service.Id, serviceId, StringComparison.Ordinal))
            is not { } found || ServiceSetupCatalog.AnswersRequests(found.Capability);

    /// <summary>
    /// Da dove viene una componente del punteggio di un servizio: misurata o scritta nel registro. Solo la latenza ha
    /// un'origine propria; costo e quota vengono dal prezzo e dalla sonda (ADR-032).
    /// </summary>
    private CalibrationSource? CalibrationOrigin(string serviceId, string component) =>
        component == "latency"
        && Snapshot.Registry.Services.FirstOrDefault(service => string.Equals(service.Id, serviceId, StringComparison.Ordinal)) is { } found
            ? found.Latency?.Source
            : null;

    private string? DetectorName(string version)
    {
        if (!version.StartsWith("onnx-", StringComparison.Ordinal)) { return null; }

        return Snapshot.Registry.Services.FirstOrDefault(service => service is
        { Enabled: true, Adapter: AdapterKind.OnnxLocal, Capability: ServiceCapability.PiiDetection }) is { } detector
            ? ServiceNames.Display(detector)
            : version;
    }

    /// <summary>
    /// Prepara un handover verso un servizio del registro. L'obiettivo arriva con la riga
    /// successiva, e senza di esso non parte nulla.
    /// </summary>
    private void StartHandover(string? argument)
    {
        if (argument is null)
        {
            renderer.Warn(Ui.Text("Serve il servizio di destinazione: /handover <id>."));
            return;
        }

        if (Snapshot.FindService(argument) is not ServiceDescriptor service)
        {
            renderer.Warn(Ui.Text("Servizio sconosciuto: ") + argument);
            return;
        }

        if (channel is not LocalReplChannel)
        {
            // L'handover apre una sessione nuova e scrive: serve la proprieta' dello store, che
            // adesso e' del worker.
            renderer.Warn(
                Ui.Text("L'handover ha bisogno della directory dati, che adesso e' del worker. ")
                + Ui.Text("Usa 'pa session handover' oppure ferma il worker."));

            return;
        }

        handoverPending = service.Id;

        renderer.Info(
            Ui.Text("Handover verso ") + ServiceNames.Display(service) + Ui.Text(": scrivi nella riga successiva l'obiettivo che la ")
            + Ui.Text("destinazione deve portare avanti."));

        renderer.Info(
            Ui.Text("La destinazione ricevera' una sessione nuova e una capsule: continuita', non ripresa ")
            + "identica.");
    }

    /// <summary>
    /// Consegna il lavoro con una capsule. L'operazione e' quella di `pa session handover`: qui c'e'
    /// solo la resa.
    /// </summary>
    private async Task HandoverAsync(string destination, string objective, CancellationToken cancellationToken)
    {
        if (channel is not LocalReplChannel local)
        {
            renderer.Warn(Ui.Text("L'handover non e' disponibile con un worker vivo."));
            return;
        }

        renderer.Phase(Ui.Text("Capsule -> Checkpoint -> Policy -> ") + destination);

        HandoverOutcome outcome;

        try
        {
            outcome = await SessionHandover.RunAsync(
                local.Stack,
                profile,
                new HandoverRequest(SessionId, destination, objective, Environment.CurrentDirectory),
                cancellationToken).ConfigureAwait(false);
        }
        catch (PaRouterException exception)
        {
            renderer.Warn(Ui.Text("Handover non eseguito: ") + exception.Code.ToWire());
            return;
        }

        if (outcome.Refusal is ErrorCode refusal)
        {
            renderer.Warn(Ui.Text("Handover rifiutato: ") + refusal.ToWire());

            if (outcome.RefusalDetail is string detail)
            {
                renderer.Warn("  " + detail);
            }

            return;
        }

        renderer.Info(Ui.Text("Continuita' da capsule, non ripresa identica."));
        renderer.Table(
            [Ui.Text("campo"), Ui.Text("valore")],
            [
                [Ui.Text("sessione nuova"), outcome.DestinationSessionId!],
                [Ui.Text("da esecuzione"), outcome.SourceExecutionId!],
                [Ui.Text("capsule digest"), outcome.CapsuleDigest!],
            ]);

        // La conversazione continua nella sessione della destinazione: restare su quella di prima
        // significherebbe scrivere le righe successive a un servizio che non ha la capsule.
        SessionId = outcome.DestinationSessionId!;

        Present(new ReplTurnResult(
            SessionId,
            outcome.Turn!.Result!.State.ToWire(),
            outcome.Turn.Result.Output,
            outcome.Turn.Result.ErrorCode?.ToWire(),
            [.. outcome.Turn.Result.Decisions.Where(decision => !decision.Allowed).Select(decision => decision.ToString())],
            outcome.Turn.Memory.Selected.Count,
            outcome.Turn.Memory.UsedBytes));
    }

    /// <summary>
    /// File cambiati nel recinto rispetto all'ultima istantanea del router (S61).
    ///
    /// E' una lettura: `WorkspaceRepository.DiffAsync` confronta gli alberi con gli stessi vincoli
    /// del checkpoint — nessun `add`, nessun filtro, l'indice dell'utente non viene toccato — e
    /// restituisce stato e percorso, mai il contenuto delle modifiche.
    ///
    /// Va distinto da cio' che l'agente **dichiara** di aver toccato, che compare durante il turno
    /// come evento: quella e' un'affermazione del programma, questa e' cio' che risulta sul disco.
    /// </summary>
    private async Task DiffAsync(CancellationToken cancellationToken)
    {
        if (fence is not SessionFence active)
        {
            renderer.Warn(Ui.Text("Nessun recinto attivo: usa /workspace <cartella> per dichiararne uno."));
            return;
        }

        IReadOnlyList<WorkspaceChange> changes;

        try
        {
            changes = await WorkspaceChanges.ListAsync(active.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (PaRouterException exception)
        {
            renderer.Warn(Ui.Text("Confronto non eseguito: ") + exception.Code.ToWire());
            return;
        }

        if (changes.Count == 0)
        {
            renderer.Info(Ui.Text("Nessuna differenza rispetto all'ultima istantanea del router."));
            return;
        }

        renderer.Table(
            [Ui.Text("stato"), Ui.Text("file")],
            [.. changes.Select(change => (IReadOnlyList<string>)[change.Status, change.Path])]);

        renderer.Info(
            Ui.Text("Sono le differenze sul disco. Per il contenuto: git diff ") + WorkspaceChanges.CheckpointRef + " --");
    }

    /// <summary>
    /// Come tornare all'istantanea precedente (S61).
    ///
    /// Il router **non ripristina**: non fa `reset`, `checkout` o `stash`, e questa non e' una
    /// mancanza ma la regola di ADR-019 — un comando che riscrive il lavoro dell'operatore non puo'
    /// partire da una riga digitata in fretta. Qui si dice quale istantanea esiste e con quale
    /// comando, esatto e copiabile, l'operatore puo' tornarci **se** lo decide.
    /// </summary>
    private void WriteUndo()
    {
        if (fence is not SessionFence active)
        {
            renderer.Warn(Ui.Text("Nessun recinto attivo: usa /workspace <cartella> per dichiararne uno."));
            return;
        }

        renderer.Info(Ui.Text("Il router non annulla al posto tuo: non esegue reset, checkout o stash (ADR-019)."));

        renderer.Table(
            [Ui.Text("cosa"), Ui.Text("comando")],
            [
                .. WorkspaceChanges
                    .RestoreCommands(active.WorkspaceRoot)
                    .Select(entry => (IReadOnlyList<string>)[entry.What, entry.Command]),
            ]);

        renderer.Warn(Ui.Text("Il ripristino sovrascrive il lavoro non salvato: guarda /diff prima di eseguirlo."));
    }

    /// <summary>
    /// Consumo della sessione (S59).
    ///
    /// Somma cio' che i servizi **dichiarano** negli eventi del turno. Il router non misura token e
    /// non stima costi a consuntivo: un totale inventato sarebbe peggio di un totale assente,
    /// perche' sembrerebbe una misura. Il preventivo prima dell'invio e' un'altra cosa e sta in
    /// <c>/explain</c>.
    /// </summary>
    private void WriteCost()
    {
        renderer.Table([Ui.Text("campo"), Ui.Text("valore")], totals.Rows());

        if (channel is not LocalReplChannel)
        {
            // Con un worker vivo gli eventi non attraversano il canale: il totale resterebbe a zero
            // e sembrerebbe un turno gratis.
            renderer.Warn(
                Ui.Text("Il turno gira nel worker e il canale non trasporta ancora gli eventi: ")
                + Ui.Text("questi totali contano solo i turni eseguiti in questo processo."));
        }
    }

    /// <summary>
    /// Quanto contesto occupa la conversazione (S59), con i numeri dell'ultimo turno (23 settembre 2026).
    ///
    /// Prima analizzava una riga vuota fuori dalla sessione: diceva «0 token» dopo qualunque conversazione e «8 turni» —
    /// il massimo della configurazione — anche dopo tre. Il contesto vero lo compone il turno, e il motore ne scrive la
    /// misura nella fase «Analisi locale»: qui la si legge, non la si ricalcola. Le finestre sono quelle dei soli servizi
    /// accesi che rispondono: embedding e rilevatori non hanno una conversazione da contenere.
    /// </summary>
    private Task ContextAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        IReadOnlyDictionary<string, string>? measured = lastStory?.Phases
            .Where(phase => phase.Phase == "Analisi locale")
            .Select(phase => (IReadOnlyDictionary<string, string>)phase.Outcome.Split(';')
                .Select(part => part.Split('=', 2))
                .Where(pair => pair.Length == 2)
                .GroupBy(pair => pair[0], StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First()[1], StringComparer.Ordinal))
            .LastOrDefault();

        if (lastStory is not { } story || measured is null)
        {
            renderer.Info(Ui.Text("context.none"));
        }
        else
        {
            renderer.Info(Ui.Format("context.last {0} {1}",
                measured.GetValueOrDefault("totalTokens", "?"), measured.GetValueOrDefault("windowTokens", "?")));

            if (story.Usage?.InputTokens is long counted)
            {
                renderer.Info(Ui.Format("context.counted {0}", counted.ToString(Formats.Invariant)));
            }

            renderer.Info(Ui.Format("context.history {0} {1} {2}",
                measured.GetValueOrDefault("historyTurns", "0"),
                Snapshot.Config.Limits.HistoryTurns.ToString(Formats.Invariant),
                measured.GetValueOrDefault("memoryItems", story.MemoryInjected.ToString(Formats.Invariant))));
        }

        string[] windows = [.. Snapshot.Registry.Services
            .Where(service => service.Enabled && ServiceSetupCatalog.AnswersRequests(service.Capability))
            .OrderBy(ServiceNames.Display, StringComparer.OrdinalIgnoreCase)
            .Select(service => ServiceNames.Display(service) + " "
                + (service.ContextWindowTokens > 0 ? service.ContextWindowTokens.ToString(Formats.Invariant) : Ui.Text("context.undeclared")))];

        renderer.Info(Ui.Text("context.windows"));

        foreach (string window in windows) { renderer.Info("  " + window); }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Analisi locale della riga appena scritta: nessun grant, nessun adapter, nessun byte fuori.
    /// </summary>
    private async Task ExplainAsync(string text, CancellationToken cancellationToken)
    {
        if (channel is not LocalReplChannel local)
        {
            renderer.Warn(
                Ui.Text("L'analisi locale ha bisogno della directory dati, che adesso e' del worker. ")
                + Ui.Text("Usa 'pa explain --stdin' oppure ferma il worker."));

            return;
        }

        TurnPlan plan = await TurnPlanning
            .PlanAsync(Snapshot, ProfileId, text, fence, writeConfirmed, cancellationToken)
            .ConfigureAwait(false);

        LocalAnalysisResult analysis = LocalAnalysis.Evaluate(
            ProfileId, text, local.Stack, TargetId, plan.Capabilities);

        Output.ExplainText.Write(analysis, plan, renderer.Info);

        if (analysis.Explain.IsEmpty)
        {
            renderer.Warn(Ui.Text("Nessun servizio ammissibile per questa richiesta con il profilo attivo."));
        }
    }

    private bool Handle(string command, out int exitCode)
    {
        exitCode = ExitCodes.Success;

        string[] parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = parts[0];
        string? argument = parts.Length > 1 ? parts[1] : null;

        renderer.Command(verb);

        switch (verb)
        {
            case "/exit":
                return true;

            case "/help":
                WriteHelp(argument);
                return false;

            case "/debug":
                debugging = argument is "on" || (argument is not "off" && !debugging);
                if (debugging && !verbose) { SetVerbose(true); }
                renderer.Info(debugging ? "Diagnosi accesa: dopo ogni turno fasi grezze, token e modelli Ollama in memoria." : "Diagnosi spenta.");
                return false;

            case "/verbose" when argument is "off" or "on":
                SetVerbose(argument is "on");
                return false;

            case "/verbose":
                SetVerbose(!verbose);
                return false;

            case "/why":
                Why();
                return false;

            case "/status" when argument is "off" or "on":
                statusBar = argument is "on";
                renderer.Info(Ui.Text(statusBar ? "status.barOn" : "status.barOff"));
                return false;

            case "/status":
                renderer.Info(ReplStatusBar.Plain(Status()));
                WriteHeader();
                return false;

            case "/target":
                SetTarget(argument);
                return false;

            case "/explain":
                explainNext = true;
                renderer.Info(Ui.Text("La prossima riga verra' solo analizzata localmente, senza invocare alcun servizio."));
                return false;

            case "/new":
                SwitchSession(Guid.NewGuid().ToString("D", Formats.Invariant));
                renderer.Info(Ui.Text("Nuova sessione: ") + SessionId);
                return false;

            // Ogni `pa nome` ha il suo `/nome` (regola dell'operatore, 24 settembre 2026). Dove il REPL aveva gia' la stessa
            // operazione con un altro nome, i due nomi fanno la stessa cosa.
            case "/session":
            case "/sessions":
                deferred = token => SessionsAsync(argument, token);
                return false;

            case "/policy":
                WritePolicies();
                return false;

            // Comandi che dentro una conversazione non hanno senso — avviano un servizio, preparano una directory, stampano
            // uno script per la shell: il nome esiste, e dice a che cosa serve e come si usa.
            case "/attach":
            case "/completion":
            case "/init":
            case "/maintenance":
            case "/run":
            case "/runtime":
            case "/script":
            case "/serve":
                renderer.Info(Ui.Text("repl.outside." + verb[1..]));
                return false;

            case "/resume":
                deferred = token => ResumeAsync(argument, token);
                return false;

            case "/name":
                deferred = token => NameAsync(argument, token);
                return false;

            case "/fork":
                deferred = ForkAsync;
                return false;

            case "/transcript":
                deferred = token => TranscriptAsync(argument, token);
                return false;

            case "/archive":
                deferred = token => ArchiveAsync(argument, token);
                return false;

            case "/services":
                WriteServices();
                return false;

            case "/workspace":
                SetFence(argument);
                return false;

            case "/memory":
                deferred = token => MemoryAsync(argument, token);
                return false;

            case "/jobs":
                deferred = JobsAsync;
                return false;

            case "/cost":
                WriteCost();
                return false;

            case "/diff":
                deferred = DiffAsync;
                return false;

            case "/undo":
                WriteUndo();
                return false;

            case "/context":
                deferred = ContextAsync;
                return false;

            case "/plugin":
            case "/plugins":
                deferred = PluginsAsync;
                return false;

            case "/skill":
                deferred = token => SelectSkillAsync(argument, token);
                return false;

            case "/handover":
                StartHandover(argument);
                return false;

            case "/doctor":
                deferred = DoctorAsync;
                return false;

            case "/tools":
                deferred = ToolsAsync;
                return false;

            case "/audit":
                Audit(argument);
                return false;

            case "/config":
                WriteConfig();
                return false;

            case "/setup":
                // Il configuratore possiede il cursore: ci si scrive dentro i campi, e usa lo stesso
                // slot del terminale per salvarne la posizione. L'area fissa si fa da parte (ADR-023).
                using (region?.Modal(fromTop: true)) { Setup(); }
                return false;

            case "/model":
            case "/models":
            case "/registry":
                WriteModels();
                return false;

            case "/clear":
                Clear();
                return false;

            case "/vim":
                Vim(argument);
                return false;

            default:
                // Un errore di battitura non merita di rileggere l'intero elenco: se un comando
                // esistente e' a un passo da quello scritto, si dice quale.
                renderer.Warn(ReplSuggestion.Closest(verb, Commands) is string near
                    ? Ui.Text("Comando sconosciuto: ") + verb + Ui.Text(". Intendevi ") + near + Ui.Text("? Usa /help.")
                    : Ui.Text("Comando sconosciuto. Usa /help."));

                return false;
        }
    }

    /// <summary>
    /// Ricerca nei ricordi dell'ambito della sessione. E' una lettura, non un turno: la domanda non
    /// lascia la macchina. La risposta e' la stessa che la directory dati sia di questo processo o
    /// del worker, perche' in entrambi i casi la lettura e' <c>ReadRequests</c> (INV-22).
    /// </summary>
    private async Task MemoryAsync(string? query, CancellationToken cancellationToken)
    {
        // Senza testo, lo stato della memoria (24 settembre 2026): e' cio' che vuole sapere chi scrive /memory e basta.
        if (string.IsNullOrWhiteSpace(query))
        {
            await JobsAsync(cancellationToken).ConfigureAwait(false);
            renderer.Info(Ui.Text("repl.memoryHint"));
            return;
        }

        MemorySearchPayload result = await channel
            .SearchMemoryAsync(query, SessionId, cancellationToken)
            .ConfigureAwait(false);

        renderer.Info(MemoryText.Search(result.Mode, result.IndexStatus));

        // Un indice «in ricostruzione» che fallisce a ogni tentativo non si sta ricostruendo: il codice del guasto e'
        // l'unica cosa che distingue «aspetta» da «non succedera'».
        if (result.IndexError is string indexError)
        {
            renderer.Warn(Ui.Format("memory.indexStuck {0} {1}", indexError, MemoryText.Failed(result.IndexErrorAt)));
        }

        if (result.Hits.Count == 0)
        {
            renderer.Info(Ui.Text("memory.none"));
            // Vuota puo' voler dire «non c'e' ancora nulla» oppure «non ci sara' mai nulla»: sono due cose diverse, e
            // la seconda si corregge in un posto solo.
            if (MemoryQueries.NeverRecords(Snapshot)) { renderer.Warn(Ui.Text("memory.neverRecords")); }
            // Terza possibilita', ed e' quella che fa credere persi dei ricordi che ci sono ancora: esistono, ma
            // appartengono a un'altra conversazione.
            else if (MemoryQueries.SessionScopedOnly(Snapshot, ProfileId)) { renderer.Info(Ui.Text("memory.sessionOnly")); }
            return;
        }

        // Il titolo nasce da un turno: e' testo non fidato, e MemoryText lo passa dallo stesso filtro delle risposte.
        int position = 0;

        foreach (var hit in result.Hits)
        {
            foreach (string line in MemoryText.Hit(++position, hit.CreatedAt, hit.Kind, hit.Verification, hit.Title ?? string.Empty, hit.Id))
            {
                renderer.Info(line);
            }
        }
    }

    /// <summary>Coda di indicizzazione della memoria: quanto resta da fare e quanto e' fallito.</summary>
    private async Task JobsAsync(CancellationToken cancellationToken)
    {
        MemoryOverviewPayload status = await channel.MemoryOverviewAsync(SessionId, cancellationToken).ConfigureAwait(false);

        foreach (string line in MemoryText.Status(status.ModelFingerprint, status.Observations, status.JobsPending, status.JobsRunning, status.JobsFailed))
        {
            renderer.Info(line);
        }
    }

    /// <summary>Pacchetti installati, con lo stato e se sono abilitati in questo progetto.</summary>
    private async Task PluginsAsync(CancellationToken cancellationToken)
    {
        PluginListPayload list = await channel.ListPluginsAsync(cancellationToken).ConfigureAwait(false);

        if (list.Plugins.Count == 0)
        {
            renderer.Info(Ui.Text("Nessun pacchetto installato."));
            return;
        }

        renderer.Table(
            [Ui.Text("pacchetto"), Ui.Text("versione"), Ui.Text("stato"), Ui.Text("abilitato qui")],
            list.Plugins.Select(plugin => (IReadOnlyList<string>)new[]
            {
                plugin.Id,
                plugin.Version,
                plugin.State,
                // Abilitato in questo progetto, per questo digest: e' cio' che una skill pretende.
                plugin.EnabledHere ? Ui.Text("si") : Ui.Text("no"),
            }).ToList());
    }

    /// <summary>
    /// Sceglie la skill per i turni successivi. La scelta non concede nulla: al turno la skill
    /// entra solo se il pacchetto e' abilitato e ha il grant, e altrimenti il turno viene negato,
    /// esattamente come `pa run --skill`. Un nome inesistente e' rifiutato gia' qui, con o senza
    /// worker.
    /// </summary>
    private async Task SelectSkillAsync(string? argument, CancellationToken cancellationToken)
    {
        if (argument is null)
        {
            SkillId = null;
            renderer.Info(Ui.Text("Nessuna skill selezionata."));
            return;
        }

        SkillListPayload skills = await channel.ListSkillsAsync(cancellationToken).ConfigureAwait(false);

        if (!skills.Skills.Any(skill => string.Equals(skill.SkillId, argument, StringComparison.Ordinal)))
        {
            renderer.Warn(Ui.Text("Skill sconosciuta: ") + argument);
            return;
        }

        SkillId = argument;

        renderer.Info(
            "Skill " + argument + Ui.Text(" selezionata per i turni successivi: entra solo se il suo pacchetto ")
            + Ui.Text("e' abilitato."));
    }

    private void SetTarget(string? argument)
    {
        if (argument is null)
        {
            TargetId = null;
            renderer.Info(Ui.Text("Vincolo di target rimosso."));
            return;
        }

        ServiceDescriptor? service = Snapshot.FindService(argument);

        if (service is null)
        {
            renderer.Warn(Ui.Text("Servizio sconosciuto: ") + argument);
            return;
        }

        // Il vincolo restringe la scelta; non concede capacita' che il servizio non ha.
        TargetId = service.Id;
        renderer.Info(Ui.Text("Target vincolato a ") + ServiceNames.Display(service) + " (" + (service.Enabled ? Ui.Text("ui.enabled") : Ui.Text("ui.disabled")) + ").");
    }

    /// <summary>
    /// Allinea la configurazione ai file e dice all'operatore cio' che e' cambiato. Un file non valido
    /// ferma le richieste finche' non viene corretto: continuare con la regola di prima vorrebbe dire
    /// ignorare proprio la modifica che l'operatore stava facendo.
    /// </summary>
    private void ObserveConfiguration()
    {
        ConfigurationRefresh refresh = channel.Refresh();
        if (refresh.Reloaded) { Ui.SetLanguage(Snapshot.Config.Language); }

        if (refresh.State == shownState)
        {
            if (refresh.Reloaded)
            {
                renderer.Info(Ui.Text("Configurazione riletta: vale da questa riga."));
            }

            return;
        }

        shownState = refresh.State;

        switch (refresh.State)
        {
            case ConfigurationState.Current:
                renderer.Info(refresh.Reloaded
                    ? Ui.Text("Configurazione riletta: vale da questa riga.")
                    : Ui.Text("La configurazione sul disco torna quella in uso."));
                break;

            case ConfigurationState.Invalid:
                renderer.Warn(Ui.Text("La configurazione sul disco non e' valida: nessuna richiesta parte finche' non la correggi."));

                foreach (ValidationIssue issue in refresh.Issues.Take(5))
                {
                    renderer.Warn("  " + issue);
                }

                break;

            default:
                renderer.Warn(
                    Ui.Text("La configurazione sul disco cambia '") + refresh.RestartField + Ui.Text("', che vale solo ripartendo: ")
                    + (channel is LocalReplChannel ? Ui.Text("riavvia il REPL.") : Ui.Text("riavvia il worker e il REPL.")));
                break;
        }
    }

    /// <summary>
    /// Fissa il recinto della sessione. La cartella e' l'unico posto in cui un agente lavora;
    /// <c>scrittura</c> gli permette di modificare file, <c>comandi</c> di eseguire comandi di sistema,
    /// sempre con conferma. Cambiare recinto richiede di nuovo la conferma della scrittura.
    /// </summary>
    private void SetFence(string? argument)
    {
        writeConfirmed = false;

        if (string.IsNullOrWhiteSpace(argument))
        {
            fence = null;
            renderer.Info(Ui.Text("Nessun recinto: ogni riga resta una chat."));
            return;
        }

        List<string> parts = [.. argument.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
        bool writable = false;
        bool shell = false;

        while (parts.Count > 1 && parts[^1] is "scrittura" or "comandi")
        {
            writable |= parts[^1] is "scrittura";
            shell |= parts[^1] is "comandi";
            parts.RemoveAt(parts.Count - 1);
        }

        if (!SessionFence.TryCreate(string.Join(' ', parts), writable, shell, out SessionFence? declared) || declared is null)
        {
            renderer.Warn(Ui.Text("La cartella non esiste: ") + string.Join(' ', parts));
            return;
        }

        fence = declared;

        renderer.Info(
            Ui.Text("Recinto: ") + declared.WorkspaceRoot
            + (declared.Writable ? Ui.Text(", l'agente puo' modificare file (la cartella diventa un repository git)") : Ui.Text(", sola lettura"))
            + (declared.ShellAllowed ? Ui.Text(", comandi di sistema con conferma ogni volta.") : "."));
    }

    /// <summary>
    /// Chiede una conferma mostrando **prima** che cosa si sta autorizzando (S60).
    ///
    /// Fino alla S59 era una riga sola: diceva la cartella e nient'altro. Ora l'anteprima elenca
    /// effetto, capacita' concesse, validita' della concessione e i limiti che il piano ha gia'
    /// applicato — cioe' la differenza fra cio' che la richiesta chiedeva e cio' che sta per
    /// ricevere. Confermare senza vedere e' firmare un foglio bianco.
    ///
    /// Tutto cio' che non e' un si' esplicito resta un no, e il resto della sessione si concede
    /// solo scrivendo «sempre» per esteso: un tasto solo non puo' valere per tutta la sessione.
    /// </summary>
    private ReplChoice Confirm(string question, IReadOnlyList<IReadOnlyList<string>> preview)
    {
        // Una conferma e' conversazione: la domanda scorre sopra, la risposta si scrive nel riquadro (ADR-024).
        renderer.Confirmation(question, preview);

        if (!input.OwnsPrompt)
        {
            renderer.Prompt();
        }

        // La risposta non entra nella cronologia: non e' una richiesta.
        return ReplConfirmation.Read(ReadInput(remember: false));
    }

    // `/services` e `/models` sono la stessa resa di `pa registry list`, con l'etichetta di questa conversazione (23 settembre
    // 2026): prima una tabella troncata e una lista che giudicava con l'etichetta predefinita.
    private void WriteServices() => Output.RegistryText.Write(Snapshot, renderer.Info, ProfileId);

    // Per una persona (23 settembre 2026): prima comparivano l'identificatore dell'etichetta, «encrypted» e «target».
    private void WriteHeader() => renderer.Header(
        [
            (Ui.Text("profile.session"), ProfilePresentation.Name(Snapshot, ProfileId)),
            (Ui.Text("status2.conversation"), Ui.Format("status2.conversationId {0}", SessionId is { Length: > 8 } id ? id[..8] : SessionId ?? "-")),
            (Ui.Text("contenuti"), Snapshot.Config.ContentMode is ContentMode.Encrypted ? Ui.Text("status2.kept") : Ui.Text("status2.notKept")),
            (Ui.Text("status2.model"), TargetId is string target ? ServiceNames.Display(Snapshot, target) : Ui.Text("status2.routerChooses")),
            ("skill", SkillId ?? Ui.Text("nessuna")),
            (Ui.Text("status2.folder"), fence is null
                ? Ui.Text("status2.noFolder")
                : fence.WorkspaceRoot + (fence.Writable ? Ui.Text(", scrittura") : Ui.Text(", sola lettura")) + (fence.ShellAllowed ? Ui.Text(", comandi") : string.Empty)),
            (Ui.Text("esecuzione"), channel.Location),
        ]);

    /// <summary>
    /// Contenuti allegati con <c>@percorso</c> nella riga (S63).
    ///
    /// Un <c>@nome</c> che non corrisponde a un file **resta testo**: capita di scriverne uno in una
    /// frase, e trasformarlo in un errore renderebbe il terminale ostile. Un file che invece esiste e
    /// non si puo' allegare — binario, illeggibile, fuori dal budget del profilo — **ferma il turno**:
    /// l'operatore ha indicato una cosa precisa, e mandare la richiesta senza sarebbe una domanda
    /// diversa da quella che ha scritto.
    ///
    /// La lettura e' quella di <c>pa run --attach</c>: se il terminale accettasse un file che il
    /// comando rifiuta, la stessa richiesta darebbe due esiti (INV-22).
    /// </summary>
    private IReadOnlyList<ContextItem>? Attach(string text)
    {
        IReadOnlyList<string> mentions = TurnAttachments.Mentions(text);

        if (mentions.Count == 0)
        {
            return null;
        }

        int budget = PolicyCompiler.Compile(profile, Snapshot.Config).MaxContextBytes;
        List<ContextItem> attached = [];

        foreach (string path in mentions)
        {
            try
            {
                attached.Add(TurnAttachments.Read(path, budget));
            }
            catch (PaRouterException exception)
                when (exception.Metadata.TryGetValue("reason", out string? reason) && reason is "attachment_missing")
            {
                // Non e' un percorso: e' una parola che comincia con @.
                continue;
            }
            catch (PaRouterException exception)
            {
                renderer.Warn(
                    Ui.Text("Allegato non utilizzabile (") + path + "): "
                    + (exception.Metadata.TryGetValue("reason", out string? reason) ? reason : exception.Code.ToWire())
                    + Ui.Text(". La richiesta non e' stata inviata."));

                throw;
            }
        }

        if (attached.Count > 0)
        {
            renderer.Phase(
                Ui.Text("Allegati: ") + attached.Count.ToString(Formats.Invariant)
                + Ui.Text(" — passano da privacy e policy come ogni altro contenuto."));
        }

        return attached.Count == 0 ? null : attached;
    }

    /// <summary>
    /// Cambia la conversazione in corso (S62).
    ///
    /// Azzera cio' che apparteneva alla precedente. La conferma della prima scrittura vale **per la
    /// sessione** (S41): portarla in un'altra conversazione significherebbe eseguire senza chiedere
    /// dentro una storia che l'operatore non ha autorizzato. Era un difetto gia' di <c>/new</c>, che
    /// cambiava sessione e si teneva il permesso. Anche il consumo mostrato da <c>/cost</c> e' quello
    /// della sessione, non del terminale.
    /// </summary>
    private void SwitchSession(string sessionId)
    {
        SessionId = sessionId;
        writeConfirmed = false;
        totals = new ReplUsageTotals();
    }

    /// <summary>Conversazioni locali, con nome, turni e stato.</summary>
    private async Task SessionsAsync(string? argument, CancellationToken cancellationToken)
    {
        bool includeArchived = string.Equals((argument ?? string.Empty).Trim(), "tutte", StringComparison.OrdinalIgnoreCase);

        Show(await channel.ListSessionsAsync(includeArchived, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Elenco delle conversazioni, nella forma del selettore.</summary>
    private void Show(SessionListPayload list)
    {
        if (list.Sessions.Count == 0)
        {
            renderer.Info(Ui.Text("Nessuna conversazione conservata."));
        }
        else
        {
            (IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows) = SessionPicker.Table(list.Sessions, Snapshot);
            renderer.Table(headers, rows);
        }

        if (!list.PersistsHistory)
        {
            renderer.Warn(
                Ui.Text("Contenuti non conservati: le conversazioni elencate non hanno turni da riprendere."));
        }
    }

    /// <summary>
    /// Riprende una conversazione.
    ///
    /// Con un argomento sceglie direttamente; senza, mostra l'elenco e legge la scelta. La regola che
    /// traduce la risposta e' quella di <see cref="SessionPicker"/>, la stessa di
    /// <c>pa session resume</c>: e' cio' che rende la ripresa dal selettore identica alla ripresa per
    /// identificatore.
    /// </summary>
    private async Task ResumeAsync(string? argument, CancellationToken cancellationToken)
    {
        SessionListPayload list = await channel
            .ListSessionsAsync(includeArchived: false, cancellationToken)
            .ConfigureAwait(false);

        string? answer = argument;

        if (string.IsNullOrWhiteSpace(answer))
        {
            if (list.Sessions.Count == 0)
            {
                renderer.Info(Ui.Text("Nessuna conversazione da riprendere."));
                return;
            }

            Show(list);
            renderer.Info(Ui.Text("Quale riprendi? (numero, nome o identificatore; vuoto annulla)"));

            if (!input.OwnsPrompt)
            {
                renderer.Prompt();
            }

            // La scelta non entra nella cronologia della riga: non e' una richiesta.
            answer = ReadInput(remember: false);
        }

        SessionChoice choice = SessionPicker.Resolve(answer, list.Sessions);

        // Archiviare toglie dalla proposta, non dalla disponibilita': un identificatore o un nome
        // raggiungono comunque la conversazione. Un **numero** no, perche' indica una riga
        // dell'elenco appena mostrato e cercarlo in un elenco piu' ampio lo farebbe puntare altrove.
        if (choice.SessionId is null && !string.IsNullOrWhiteSpace(answer) && !SessionPicker.IsPosition(answer))
        {
            SessionListPayload all = await channel
                .ListSessionsAsync(includeArchived: true, cancellationToken)
                .ConfigureAwait(false);

            choice = SessionPicker.Resolve(answer, all.Sessions);
        }

        if (choice.SessionId is not string chosen)
        {
            renderer.Warn(choice.Problem ?? Ui.Text("Ripresa annullata: la conversazione resta quella di prima."));
            return;
        }

        SwitchSession(chosen);
        renderer.Info(Ui.Text("Conversazione ripresa: ") + chosen);
    }

    /// <summary>
    /// Da' un nome alla conversazione in corso, o lo toglie.
    ///
    /// Il nome esiste nello store solo dopo il primo turno, perche' e' il turno a creare la riga
    /// della sessione: dirlo e' meglio che lasciare un codice d'errore da interpretare.
    /// </summary>
    private async Task NameAsync(string? argument, CancellationToken cancellationToken)
    {
        string? name = string.IsNullOrWhiteSpace(argument) ? null : argument.Trim();

        try
        {
            await channel.NameSessionAsync(SessionId, name, cancellationToken).ConfigureAwait(false);
        }
        catch (PaRouterException exception)
            when (exception.Metadata.TryGetValue("reason", out string? reason) && reason is "session_unknown")
        {
            renderer.Warn(Ui.Text("Questa conversazione non ha ancora turni: il nome si da' dopo la prima domanda."));
            return;
        }
        catch (PaRouterException exception)
            when (exception.Metadata.TryGetValue("reason", out string? reason) && reason is "name_taken")
        {
            renderer.Warn(Ui.Text("Quel nome appartiene gia' a un'altra conversazione: riprenderlo ne sceglierebbe due."));
            return;
        }

        renderer.Info(name is null ? Ui.Text("Nome rimosso.") : Ui.Text("Nome assegnato: ") + name);
    }

    /// <summary>
    /// Continua da qui in una conversazione nuova, lasciando intatta quella di adesso.
    ///
    /// Non e' un handover: nessuna capsule, nessun altro servizio, nessun turno eseguito. Sono gli
    /// stessi contenuti gia' conservati da questo router, copiati in una sessione nuova.
    /// </summary>
    private async Task ForkAsync(CancellationToken cancellationToken)
    {
        string previous = SessionId;

        SessionAckPayload fork = await channel.ForkSessionAsync(previous, cancellationToken).ConfigureAwait(false);

        SwitchSession(fork.SessionId);

        renderer.Info(Ui.Text("Biforcata: si prosegue in ") + SessionId + ".");
        renderer.Info(Ui.Text("Quella di prima (") + previous + Ui.Text(") resta intatta e si riprende con /resume."));
    }

    /// <summary>
    /// Ultimi turni conservati della conversazione in corso.
    ///
    /// E' contenuto, non un riassunto: nessun servizio viene interrogato e nessuna quota consumata.
    /// Passa dalla resa non fidata come ogni altro testo che il router non ha generato.
    /// </summary>
    private async Task TranscriptAsync(string? argument, CancellationToken cancellationToken)
    {
        int limit = int.TryParse((argument ?? string.Empty).Trim(), Formats.Invariant, out int requested) ? requested : 0;

        SessionTranscriptPayload transcript = await channel
            .TranscriptAsync(SessionId, limit, cancellationToken)
            .ConfigureAwait(false);

        if (transcript.Turns.Count == 0)
        {
            renderer.Info(Ui.Text("Nessun turno conservato in questa conversazione."));
            return;
        }

        foreach (SessionTurnPayload turn in transcript.Turns)
        {
            renderer.Info(Ui.Text("— turno ") + turn.Ordinal.ToString(Formats.Invariant) + " —");
            renderer.BeginTurn();
            renderer.User(turn.Input);

            if (turn.Output is string reply)
            {
                renderer.Answer(reply);
            }
            renderer.EndTurn();
        }

        if (transcript.Turns.Count < transcript.Total)
        {
            renderer.Info(
                Ui.Text("Mostrati gli ultimi ") + transcript.Turns.Count.ToString(Formats.Invariant)
                + Ui.Text(" di ") + transcript.Total.ToString(Formats.Invariant) + Ui.Text(" turni."));
        }
    }

    /// <summary>
    /// Toglie una conversazione dall'elenco, o ce la riporta con <c>ripristina</c>.
    ///
    /// Archiviare **non** cancella: turni ed esecuzioni restano dove sono e la conversazione resta
    /// riprendibile. Cancellare e' <c>pa session delete</c>, ed e' un'altra decisione.
    /// </summary>
    private async Task ArchiveAsync(string? argument, CancellationToken cancellationToken)
    {
        const string restoreVerb = "ripristina";

        string text = (argument ?? string.Empty).Trim();
        bool restore = text.StartsWith(restoreVerb, StringComparison.OrdinalIgnoreCase);

        if (restore)
        {
            text = text[restoreVerb.Length..].Trim();
        }

        string target = SessionId;

        if (text.Length > 0)
        {
            // Un numero, un nome o un identificatore abbreviato si traducono con la regola del
            // selettore: una seconda interpretazione sceglierebbe una conversazione diversa.
            SessionListPayload all = await channel
                .ListSessionsAsync(includeArchived: true, cancellationToken)
                .ConfigureAwait(false);

            SessionChoice choice = SessionPicker.Resolve(text, all.Sessions);

            if (choice.SessionId is not string resolved)
            {
                renderer.Warn(choice.Problem ?? Ui.Text("Nessuna conversazione indicata."));
                return;
            }

            target = resolved;
        }

        await channel.ArchiveSessionAsync(target, !restore, cancellationToken).ConfigureAwait(false);

        renderer.Info(restore
            ? Ui.Text("Riportata in elenco: ") + target
            : Ui.Text("Archiviata: ") + target + Ui.Text(". Nulla e' stato cancellato; ricompare con /sessions tutte."));
    }

    /// <summary>
    /// Aiuto del terminale. Con un argomento mostra l'aiuto di quel comando, con un esempio vero
    /// (S63): l'elenco dice che cosa fa un comando, l'esempio dice come si scrive.
    /// </summary>
    private void WriteHelp(string? argument = null)
    {
        if (!string.IsNullOrWhiteSpace(argument))
        {
            if (ReplHelp.For(argument) is (string example, string note))
            {
                renderer.Info(example);
                renderer.Info("  " + note);
            }
            else
            {
                renderer.Warn(ReplSuggestion.Closest(argument, Commands) is string near
                    ? Ui.Text("Comando sconosciuto: ") + argument.Trim() + Ui.Text(". Intendevi ") + near + "?"
                    : Ui.Text("Comando sconosciuto: ") + argument.Trim() + Ui.Text(". Usa /help per l'elenco."));
            }

            return;
        }

        renderer.Info(Ui.Text("Una riga che non comincia con / e' una richiesta: viene analizzata,"));
        renderer.Info(Ui.Text("valutata dalla policy e inviata al servizio che il routing sceglie."));
        renderer.Info(string.Empty);
        renderer.Info(Ui.Text("/help              questo elenco"));
        renderer.Info(Ui.Text("/status            profilo, sessione, modo contenuti, target, esecuzione"));
        renderer.Info(Ui.Text("/services          servizi del registro e loro stato"));
        renderer.Info(Ui.Text("/workspace <cartella> [scrittura] [comandi]  recinto degli agenti; senza argomenti lo toglie"));
        renderer.Info(Ui.Text("/explain           analizza la prossima riga senza invocare"));
        renderer.Info(Ui.Text("/target <id>       vincola il target; senza id rimuove il vincolo"));
        renderer.Info(Ui.Text("/new               comincia una sessione nuova"));
        renderer.Info(Ui.Text("/sessions [tutte]  conversazioni locali; 'tutte' mostra anche le archiviate"));
        renderer.Info(Ui.Text("/resume [scelta]   riprende una conversazione; senza argomento apre il selettore"));
        renderer.Info(Ui.Text("/name <nome>       da' un nome a questa conversazione; senza nome lo toglie"));
        renderer.Info(Ui.Text("/fork              continua da qui in una conversazione nuova, senza toccare questa"));
        renderer.Info(Ui.Text("/transcript [n]    ultimi turni conservati di questa conversazione"));
        renderer.Info(Ui.Text("/archive [id]      toglie dall'elenco senza cancellare; 'ripristina <id>' la riporta"));
        renderer.Info(Ui.Text("/handover <id>     consegna il lavoro a un altro servizio con una capsule"));
        renderer.Info(Ui.Text("/memory <testo>    cerca nei ricordi dell'ambito della sessione"));
        renderer.Info(Ui.Text("/skill <id>        sceglie una skill per i turni successivi; senza id la toglie"));
        renderer.Info(Ui.Text("/plugins           pacchetti installati e permessi concessi"));
        renderer.Info(Ui.Text("/jobs              coda di indicizzazione della memoria"));
        renderer.Info(Ui.Text("repl.help.policy"));
        renderer.Info(Ui.Text("repl.help.aliases"));
        renderer.Info(Ui.Text("repl.help.outside"));
        renderer.Info(Ui.Text("/cost              token e costo dichiarati dai servizi in questa sessione"));
        renderer.Info(Ui.Text("/context           quanto contesto occupa la conversazione"));
        renderer.Info(Ui.Text("/diff              file cambiati nel recinto rispetto all'ultima istantanea"));
        renderer.Info(Ui.Text("/undo              come tornare all'istantanea precedente, con git"));
        renderer.Info(Ui.Text("/doctor            diagnosi locale: schema, protector, servizi, memoria, strumenti"));
        renderer.Info(Ui.Text("/tools             sorgenti di strumenti configurate e quali sono usabili"));
        renderer.Info(Ui.Text("/audit <id>        traccia di audit di una esecuzione"));
        renderer.Info(Ui.Text("/config            configurazione in uso, senza segreti"));
        renderer.Info(Ui.Text("/setup             configura policy, servizi, attestazioni e routing; salva alla fine"));
        renderer.Info(Ui.Text("/model, /models   modelli raggruppati per servizio, endpoint e stato rispetto al routing"));
        renderer.Info(Ui.Text("/clear             pulisce lo schermo; la conversazione resta quella di prima"));
        renderer.Info(Ui.Text("/vim [on|off]      modi normale e inserimento nel compositore di riga; spenta per default"));
        renderer.Info(Ui.Text("/exit              esce dal REPL"));
        renderer.Info(string.Empty);
        renderer.Info(Ui.Text("Un file si allega con @percorso dentro la richiesta."));
        renderer.Info(Ui.Text("Per l'esempio di un comando: /help <comando>, per esempio /help /resume"));

        // Nello schermo del REPL la conversazione la scorre il router, e per selezionare serve Maiuscole: va detto
        // dove si cerca come si usa il terminale (ADR-024).
        if (region is { Enabled: true })
        {
            renderer.Info(string.Empty);
            renderer.Info(Ui.Text("help.scroll"));
            renderer.Info(Ui.Text("help.select"));
        }
    }

    /// <summary>
    /// Diagnosi locale, con la stessa operazione di <c>pa doctor</c> (S63).
    ///
    /// E' una lettura senza lock: non prende la proprieta' della directory dati, quindi risponde
    /// allo stesso modo con o senza un worker vivo. Nessuna probe genera: osservano e basta, e un
    /// modello assente viene dichiarato, mai scaricato.
    /// </summary>
    private async Task DoctorAsync(CancellationToken cancellationToken)
    {
        DiagnosisReport report = await LocalDiagnosis
            .RunAsync(Snapshot.DataDirectory, cancellationToken)
            .ConfigureAwait(false);

        List<IReadOnlyList<string>> rows =
        [
            [Ui.Text("configurazione"), report.ConfigurationValid ? Ui.Text("valida") : Ui.Text("non valida")],
            [
                Ui.Text("schema store"),
                report.SchemaVersion == 0 ? Ui.Text("non inizializzato") : report.SchemaVersion.ToString(Formats.Invariant)
            ],
            [Ui.Text("protector chiavi"), report.KeyProtector ?? Ui.Text("non disponibile")],
            [
                Ui.Text("contenimento"),
                report.ProcessContainment
                    + (report.ProcessContainmentSupported ? string.Empty : Ui.Text(" — senza, nessun agente parte"))
            ],
            [Ui.Text("servizi abilitati"), report.EnabledServices.ToString(Formats.Invariant)],
            [Ui.Text("analizzatore"), report.Analyzer ?? Ui.Text("sconosciuto")],
            [
                Ui.Text("memoria"),
                (report.MemoryLexicalOnly
                    ? Ui.Text("ricerca solo per parole")
                    : report.MemoryConfigured ? Ui.Text("configurata") : Ui.Text("non configurata"))
                + (report.MemoryNeverRecords ? Ui.Text(" — nessun ricordo viene scritto") : string.Empty)
            ],
            [
                Ui.Text("coda memoria"),
                report.MemoryJobsPending.ToString(Formats.Invariant) + Ui.Text(" in attesa, ")
                + report.MemoryJobsFailed.ToString(Formats.Invariant) + Ui.Text(" falliti")
                + (report.EmbeddingMismatch ? Ui.Text(" (disallineamento: 'pa memory reindex')") : string.Empty)
            ],
        ];

        if (report.PiiModel is PiiModelDiagnosis pii)
        {
            rows.Add([
                Ui.Text("rilevatore PII"),
                (pii.Model ?? Ui.Text("senza modello dichiarato")) + (pii.Installed ? Ui.Text(" — installato") : Ui.Text(" — non installato")),
            ]);
        }

        renderer.Table([Ui.Text("voce"), Ui.Text("stato")], rows);

        // Zone e gradazione di ogni profilo (ADR-029): fin dove vanno i dati, e che cosa resta dentro.
        foreach (ProfileZonesDiagnosis profile in report.Profiles)
        {
            renderer.Info(ZoneText.Limits(profile));
            renderer.Info("  " + ZoneText.Rules(profile));
        }

        if (report.Services.Count == 0)
        {
            renderer.Info(Ui.Text("Nessun servizio locale abilitato."));
            return;
        }

        renderer.Table(
            [Ui.Text("servizio"), Ui.Text("stato"), Ui.Text("inferenza"), Ui.Text("zona"), "local_inference"],
            [.. report.Services.OrderBy(service => service.Service is { } described ? ServiceNames.Display(described) : Named(service.Id), StringComparer.OrdinalIgnoreCase)
                .Select(service => (IReadOnlyList<string>)new[]
            {
                service.Service is { } described ? ServiceNames.Display(described) : Named(service.Id),
                service.Probe.Health.ToWire() + (service.Probe.FailureCode is string failure ? " (" + failure + ")" : string.Empty),
                service.Service?.InferencePlacement.ToWire() ?? "—",
                service.Zone is TrustZone zone ? ReplConfirmation.Zone(zone.ToWire()) : "—",
                service.LocalInferenceGrade ?? Ui.Text("non dichiarato"),
            })]);

        foreach (ServiceDiagnosis service in report.Services)
        {
            if (service.PrivacyReady is not PrivacyReadyVerdict verdict || verdict.Ready)
            {
                continue;
            }

            // Un verdetto falso senza il motivo non si corregge.
            renderer.Warn(
                Ui.Text("privacy ready no: ") + Named(service.Id) + " ("
                + string.Join(", ", verdict.Failures.Select(failure => failure.Condition.ToWire())) + ")");
        }
    }

    /// <summary>
    /// Sorgenti di strumenti configurate e quali strumenti sarebbero offerti.
    ///
    /// Passa dalla stessa diagnosi di <c>/doctor</c> e di <c>pa doctor</c>: le attestazioni si
    /// risolvono con la regola del turno, non con una seconda copia (INV-22).
    /// </summary>
    private async Task ToolsAsync(CancellationToken cancellationToken)
    {
        DiagnosisReport report = await LocalDiagnosis
            .RunAsync(Snapshot.DataDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (report.Tools is not ToolDiagnosis tools)
        {
            renderer.Info(Ui.Text("Strumenti non configurati."));
            return;
        }

        renderer.Info(Ui.Text("Strumenti: ") + (tools.Enabled ? Ui.Text("accesi") : Ui.Text("spenti")));

        if (tools.Sources.Count == 0)
        {
            renderer.Info(Ui.Text("Nessuna sorgente configurata."));
        }
        else
        {
            renderer.Table(
                [Ui.Text("sorgente"), Ui.Text("ui.type"), Ui.Text("esito")],
                [.. tools.Sources.Select(source => (IReadOnlyList<string>)new[]
                {
                    source.Id,
                    source.Kind.ToWire(),
                    source.Problem is ToolProblem problem
                        ? problem.Code.ToWire()
                        : source.Tools.Count(tool => tool.Problem is null).ToString(Formats.Invariant)
                            + Ui.Text(" usabili su ") + source.Tools.Count.ToString(Formats.Invariant),
                })]);
        }

        if (tools.ClassesWithoutToolCapableModel.Count > 0)
        {
            renderer.Warn(
                Ui.Text("Nessun modello ammesso sa usare strumenti per: ")
                + string.Join(", ", tools.ClassesWithoutToolCapableModel.Select(dataClass => dataClass.ToWire()))
                + Ui.Text(" — le richieste con strumenti su questi dati verranno negate."));
        }
    }

    /// <summary>
    /// Traccia di audit di una esecuzione, con la stessa lettura di <c>pa audit show</c>.
    ///
    /// Gli eventi portano codici e metadata di insiemi chiusi: nessun contenuto della conversazione,
    /// nessun argomento, nessun risultato.
    /// </summary>
    private void Audit(string? argument)
    {
        string executionId = (argument ?? string.Empty).Trim();

        // Senza identificatore, l'ultima esecuzione: la stessa regola di `pa audit show` (24 settembre 2026).
        if (executionId.Length == 0)
        {
            if (AuditQueries.Latest(StorePaths.For(Snapshot.DataDirectory)) is not string latest)
            {
                renderer.Warn(Ui.Text("audit.none"));
                return;
            }

            executionId = latest;
        }

        if (AuditQueries.Read(StorePaths.For(Snapshot.DataDirectory), executionId) is not AuditTrail trail)
        {
            renderer.Warn(Ui.Text("Nessuna traccia di audit per questa esecuzione."));
            return;
        }

        // La stessa resa di `pa audit show`: frasi e nomi, non codici e identificatori (24 settembre 2026).
        Output.AuditText.Write(trail, Snapshot, renderer.Info);
    }

    /// <summary>
    /// Configurazione in uso. E' la vista che il canale allinea ai file prima di ogni riga, quindi
    /// dice cio' che vale adesso e non cio' che valeva all'avvio. Nessun segreto: il riferimento al
    /// token resta un nome, e il valore vive nel vault locale.
    /// </summary>
    /// <summary>
    /// Configuratore guidato, sulla directory dati che il REPL sta gia' usando.
    ///
    /// E' lo stesso componente che l'avvio apre quando non c'e' una configurazione utilizzabile: le
    /// domande e le scritture stanno in <see cref="ConfigurationSetup"/>, non qui (INV-22). La
    /// differenza fra i due casi e' solo il momento, non la logica.
    /// </summary>
    private void Setup()
    {
        if (!PaRootCommand.Interactive)
        {
            renderer.Warn(Ui.Text("INPUT_INVALID: /setup richiede un terminale interattivo. Usa pa config show --json per la diagnosi; avvia pa in un terminale per configurare."));
            return;
        }

        SetupScreen screen = new(
            input,
            renderer,
            SystemClock.Instance,
            null);

        // Il canale riallinea la configurazione ai file prima di ogni riga: quello che il
        // configuratore ha appena scritto vale gia' dal comando successivo, senza ricaricare nulla.
        Ui.SetLanguage(screen.Run(Snapshot.DataDirectory).Snapshot?.Config.Language);
    }

    /// <summary>
    /// Controllo della configurazione all'avvio del REPL.
    ///
    /// Prima della S88 l'avvio si fermava su un elenco di problemi: l'operatore sapeva che qualcosa
    /// mancava, non che cosa fare. I tre casi ora sono distinti, e lo sono per una ragione ciascuno.
    ///
    /// **Utilizzabile**: si parte, senza dire niente. **Incompleta**: si parte lo stesso — un
    /// servizio non ancora abilitato fara' fallire un turno con il proprio codice, e impedire
    /// l'avvio per un avviso toglierebbe all'operatore proprio il terminale da cui correggerlo — ma
    /// si dice quale pezzo manca e da dove si corregge. **Assente, illeggibile o in forma
    /// superata**: non c'e' nessun REPL da aprire, quindi si apre il configuratore.
    /// </summary>
    /// <param name="dataDirectory">Directory dati scelta per questo avvio.</param>
    /// <param name="output">Uscita del comando.</param>
    /// <param name="error">Errori del comando.</param>
    internal static ConfigurationReport EnsureConfigured(string dataDirectory, TextWriter output, TextWriter error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        ConfigurationReport report = ConfigurationHealth.Inspect(dataDirectory, SystemClock.Instance);
        Ui.SetLanguage(report.Snapshot?.Config.Language);

        if (report.CanStart && !report.NeedsAttention)
        {
            return report;
        }

        IReplRenderer renderer = Renderer(output, error);

        if (PaRootCommand.Interactive && report.NeedsAttention)
        {
            return new SetupScreen(
                Input(output),
                renderer,
                SystemClock.Instance,
                null)
                .Run(dataDirectory);
        }

        renderer.Warn(Ui.Text("La configurazione si carica, ma non e' completa:"));

        foreach (SectionReport section in report.Attention)
        {
            renderer.Warn(
                "  " + Ui.Diagnostic(section.Summary) + (section.Hint is string hint ? " - " + Ui.Diagnostic(hint) : string.Empty));
        }

        renderer.Info(Ui.Text("'/setup' apre il configuratore guidato; '/config' mostra lo stato per sezione."));

        return report;
    }

    /// <summary>
    /// Il configuratore da `pa setup`: la stessa schermata di /setup e del primo avvio, sulla directory dati scelta.
    /// </summary>
    internal static int RunSetup(string dataDirectory, TextWriter output, TextWriter error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        ConfigurationReport report = new SetupScreen(Input(output), Renderer(output, error), SystemClock.Instance, null)
            .Run(dataDirectory);

        Ui.SetLanguage(report.Snapshot?.Config.Language);

        return ExitCodes.Success;
    }

    /// <summary>Le etichette con la loro descrizione calcolata, come in `pa config show`: e' cio' che `pa policy` gestisce.</summary>
    private void WritePolicies()
    {
        foreach (PolicyDocument profile in Snapshot.Profiles.Values.OrderBy(item => ProfilePresentation.Name(item), StringComparer.CurrentCultureIgnoreCase))
        {
            renderer.Info(ProfilePresentation.Name(profile) + (profile.ProfileId == ProfileId ? Ui.Text("repl.policy.current") : string.Empty));
            renderer.Info("    " + ProfilePresentation.Summary(ProfileFacts.Describe(Snapshot, profile.ProfileId, SystemClock.Instance)));
        }

        renderer.Info(Ui.Text("repl.policy.hint"));
    }

    private void WriteConfig()
    {
        ConfigurationReport report = ConfigurationHealth.Inspect(Snapshot.DataDirectory, SystemClock.Instance);
        new SetupScreen(input, renderer, SystemClock.Instance).Show(report);
        string Shown(string id) => report.Snapshot?.FindService(id) is { } described ? ServiceNames.Display(described) : id;

        foreach (ServiceConfigurationReport service in report.Services.OrderBy(item => Shown(item.Id), StringComparer.OrdinalIgnoreCase))
        {
            renderer.SetupStatus(ToSetupVisualState(service.Severity),
                report.Snapshot?.FindService(service.Id) is { } described ? ServiceNames.Display(described) : service.Id,
                SetupScreen.ServiceSummary(service));
        }
        renderer.Table([Ui.Text("servizio"), Ui.Text("modello"), Ui.Text("stato"), Ui.Text("zona"), Ui.Text("credenziale")],
            [.. (report.Snapshot?.Registry.Services ?? []).OrderBy(ServiceNames.Display, StringComparer.OrdinalIgnoreCase).Select(service => (IReadOnlyList<string>)new[]
            {
                ServiceNames.Instance(service), SetupHelp.Value(System.Text.Json.Nodes.JsonValue.Create(service.Model ?? service.Adapter.ToWire())), SetupHelp.Name(report.Services.Single(item => item.Id == service.Id).State),
                // La zona che il servizio ha con le conferme date (ADR-032, fase 4): e' cio' che le regole confrontano.
                ReplConfirmation.Zone(ServiceSetupCatalog.ExpectedZone(service, SystemClock.Instance.UtcNow).ToWire()),
                service.SecretRef is null ? Ui.Text("non richiesta") : Ui.Text("*** (riferimento configurato)"),
            })]);
        renderer.Table(
        [Ui.Text("voce"), Ui.Text("valore")],
        [
            [Ui.Text("directory dati"), Snapshot.DataDirectory],
            [Ui.Text("profilo predefinito"), Snapshot.Config.DefaultProfileId],
            [SetupHelp.Name("language"), Ui.NativeName(Snapshot.Config.Language)],
            [Ui.Text("contenuti"), Snapshot.Config.ContentMode.ToWire()],
            ["digest", Snapshot.Digest],
            [
                "api",
                Snapshot.Config.Api.Host + ":" + Snapshot.Config.Api.Port.ToString(Formats.Invariant)
                + " (" + Snapshot.Config.Api.ServiceProfileId + ")"
            ],
            [Ui.Text("telemetria"), Snapshot.Config.Telemetry.Exporter.ToWire()],
            [
                Ui.Text("limiti"),
                Ui.Text("concorrenza ") + Snapshot.Config.Limits.MaxConcurrency.ToString(Formats.Invariant)
                + Ui.Text(", timeout ") + Snapshot.Config.Limits.TimeoutSeconds.ToString(Formats.Invariant) + "s"
                + Ui.Text(", tentativi ") + Snapshot.Config.Limits.MaxAttempts.ToString(Formats.Invariant)
            ],
            [
                Ui.Text("memoria"),
                Ui.Text("attiva ") + (Snapshot.Config.Memory.Enabled ? Ui.Text("si") : Ui.Text("no"))
                + Ui.Text(", obbligatoria ") + (Snapshot.Config.Memory.Required ? Ui.Text("si") : Ui.Text("no"))
                + Ui.Text(", servizio ") + (Snapshot.Config.Memory.EmbeddingServiceId ?? Ui.Text("nessuno"))
            ],
            [Ui.Text("registro"), Snapshot.Registry.Revision],
        ]);
    }

    /// <summary>
    /// Modelli dei servizi abilitati e vincolo attivo.
    ///
    /// E' una **lettura**: il servizio lo sceglie il routing, e vincolarlo resta <c>/target</c>, che
    /// e' un comando solo. Due modi di fissare la stessa cosa sarebbero due regole che divergono.
    /// </summary>
    private void WriteModels() => WriteServices();

    private static SetupVisualState ToSetupVisualState(SectionSeverity severity) => severity switch
    {
        SectionSeverity.Missing or SectionSeverity.Error => SetupVisualState.RequiredMissing,
        SectionSeverity.Warning => SetupVisualState.Warning,
        SectionSeverity.Ok => SetupVisualState.RequiredSatisfied,
        _ => SetupVisualState.Optional,
    };

    /// <summary>
    /// Accende o spegne i modi del compositore di riga (S67).
    ///
    /// Nasce spenta e **non persiste** fra sessioni: gli override d'ambiente del prodotto sono due,
    /// dichiarati e validati (S52), e aggiungerne un terzo per una preferenza di editing sarebbe
    /// sproporzionato. Chi la vuole sempre la accende con una riga all'inizio della conversazione.
    ///
    /// Senza un terminale non c'e' una riga da comporre: l'ingresso a righe la ignora, e il comando
    /// lo dice invece di far credere che sia attiva.
    /// </summary>
    private void Vim(string? argument)
    {
        string choice = (argument ?? string.Empty).Trim().ToLowerInvariant();

        bool wanted = choice switch
        {
            "" => !input.VimEnabled,
            "on" or "si" or "acceso" => true,
            _ => false,
        };

        input.VimEnabled = wanted;

        if (wanted && !input.VimEnabled)
        {
            renderer.Warn(Ui.Text("Senza un terminale non c'e' una riga da comporre: la modalita' resta spenta."));
            return;
        }

        renderer.Info(wanted
            ? Ui.Text("Modalita' vim accesa: Esc per il modo normale, i per tornare a scrivere. ")
                + Ui.Text("Vale per questa conversazione, non oltre.")
            : Ui.Text("Modalita' vim spenta: i tasti tornano a comportarsi come sempre."));
    }

    /// <summary>
    /// Pulisce lo schermo.
    ///
    /// **Non** cancella la conversazione: i turni restano dove sono e il turno successivo ritrova il
    /// precedente. Per cominciarne un'altra c'e' <c>/new</c>, e confondere le due cose farebbe
    /// credere cancellato un contesto che invece viaggia ancora nel payload.
    /// </summary>
    private void Clear()
    {
        if (region is { Enabled: true }) { region.ClearScreen(); }
        else if (PaRootCommand.Interactive)
        {
            try
            {
                Console.Clear();
            }
            catch (IOException)
            {
                // Un terminale che non sa pulirsi non e' un guasto del comando.
            }
        }

        renderer.Info(Ui.Text("Schermo pulito. La conversazione e' la stessa: /new ne comincia un'altra."));
    }
}
