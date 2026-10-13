namespace PA.Router.Cli.Repl;

/// <summary>
/// Aiuto di un singolo comando, con un esempio vero (S63).
///
/// L'elenco di <c>/help</c> dice **che cosa** fa un comando; qui c'e' come si scrive. La differenza
/// conta per i comandi che prendono un argomento: Â«da' un nome a questa conversazioneÂ» non dice se
/// il nome va fra virgolette, e chi legge finisce per provare.
///
/// Gli esempi sono **reali**: si possono copiare nella riga e funzionano. Un esempio inventato che
/// fallisce e' peggio di nessun esempio, perche' manda a provare una cosa che non esiste â€” e' la
/// stessa regola che vale per i documenti (S52).
/// </summary>
internal static class ReplHelp
{
    /// <summary>Esempio e spiegazione, per comando.</summary>
    private static Dictionary<string, (string Example, string Note)> Entries =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["/status"] = ("/status", Ui.Text("profilo, conversazione, contenuti, recinto e dove gira il turno")),
            ["/verbose"] = ("/verbose on", Ui.Text("ogni motivo con i suoi parametri e le fasi del turno; anche Ctrl+O")),
            ["/debug"] = ("/debug on", Ui.Text("il racconto piu' i dati grezzi del turno: fasi con durata, token, modelli Ollama in memoria prima e dopo")),
            ["/why"] = ("/why", Ui.Text("ristampa la spiegazione dell'ultimo turno, senza rimandare la richiesta")),
            ["/services"] = ("/services", Ui.Text("servizi del registro con stato, inferenza e posto nell'ordine")),
            ["/model"] = ("/model", Ui.Text("modelli dei servizi dichiarati e stato rispetto al routing")),
            ["/models"] = ("/models", Ui.Text("modelli raggruppati per servizio, endpoint e stato utilizzabile")),
            ["/target"] = ("/target <servizio>", Ui.Text("vincola il servizio; senza argomento toglie il vincolo")),
            ["/workspace"] = (
                "/workspace C:\\lavoro\\progetto scrittura",
                Ui.Text("recinto dell'agente; aggiungi 'comandi' per i comandi di sistema")),
            ["/explain"] = ("/explain", Ui.Text("la riga successiva viene analizzata senza invocare nessun servizio")),
            ["/new"] = ("/new", Ui.Text("comincia una conversazione nuova; quella di prima resta e si riprende")),
            ["/sessions"] = ("/sessions tutte", Ui.Text("conversazioni locali; 'tutte' include le archiviate")),
            // Ogni `pa nome` ha il suo `/nome` (24 settembre 2026): gli omonimi fanno cio' che fa il comando gemello del REPL,
            // gli altri dicono come si usano fuori.
            ["/session"] = ("/session tutte", Ui.Text("conversazioni locali; 'tutte' include le archiviate")),
            ["/plugin"] = ("/plugin", Ui.Text("repl.help.aliases")),
            ["/registry"] = ("/registry", Ui.Text("modelli raggruppati per servizio, endpoint e stato utilizzabile")),
            ["/policy"] = ("/policy", Ui.Text("repl.help.policy")),
            ["/attach"] = ("/attach", Ui.Text("repl.outside.attach")),
            ["/completion"] = ("/completion", Ui.Text("repl.outside.completion")),
            ["/init"] = ("/init", Ui.Text("repl.outside.init")),
            ["/maintenance"] = ("/maintenance", Ui.Text("repl.outside.maintenance")),
            ["/run"] = ("/run", Ui.Text("repl.outside.run")),
            ["/runtime"] = ("/runtime", Ui.Text("repl.outside.runtime")),
            ["/script"] = ("/script", Ui.Text("repl.outside.script")),
            ["/serve"] = ("/serve", Ui.Text("repl.outside.serve")),
            ["/resume"] = ("/resume 2", Ui.Text("riprende la riga 2 dell'elenco; accetta anche nome o identificatore")),
            ["/name"] = ("/name rifattorizzazione", Ui.Text("nome leggibile per questa conversazione; senza argomento lo toglie")),
            ["/fork"] = ("/fork", Ui.Text("continua da qui in una conversazione nuova; questa resta intatta")),
            ["/transcript"] = ("/transcript 5", Ui.Text("ultimi cinque turni conservati; senza numero ne mostra venti")),
            ["/archive"] = ("/archive ripristina 3", Ui.Text("riporta in elenco la riga 3; senza 'ripristina' archivia")),
            ["/memory"] = ("/memory scadenza contratto", Ui.Text("cerca nei ricordi dell'ambito di questa conversazione")),
            ["/skill"] = ("/skill review-changes", Ui.Text("sceglie una skill per i turni successivi; non concede permessi")),
            ["/plugins"] = ("/plugins", Ui.Text("pacchetti installati e permessi concessi nel progetto")),
            ["/jobs"] = ("/jobs", Ui.Text("coda di indicizzazione della memoria")),
            ["/cost"] = ("/cost", Ui.Text("token e costo che i servizi hanno dichiarato in questa conversazione")),
            ["/context"] = ("/context", Ui.Text("quanto contesto occupa la conversazione")),
            ["/diff"] = ("/diff", Ui.Text("file cambiati nel recinto rispetto all'ultima istantanea")),
            ["/undo"] = ("/undo", Ui.Text("i comandi git per tornare indietro; eseguirli resta una tua decisione")),
            ["/doctor"] = ("/doctor", Ui.Text("diagnosi locale: schema, protector, servizi, memoria, strumenti")),
            ["/tools"] = ("/tools", Ui.Text("sorgenti di strumenti configurate e quali strumenti sarebbero usabili")),
            ["/audit"] = (
                "/audit 8f1c2d3e-0000-4000-8000-000000000001",
                Ui.Text("traccia di audit di una esecuzione: codici e tentativi, mai contenuti")),
            ["/config"] = ("/config", Ui.Text("configurazione in uso, senza segreti")),
            ["/setup"] = (
                "/setup",
                Ui.Text("configuratore del router: policy, piu' servizi, attestazioni e routing; salva o annulla")),
            ["/clear"] = ("/clear", Ui.Text("pulisce lo schermo; la conversazione resta quella di prima")),
            ["/vim"] = (
                "/vim on",
                Ui.Text("modi normale e inserimento nel compositore; senza argomento inverte, e non persiste")),
            ["/handover"] = ("/handover <servizio>", Ui.Text("consegna il lavoro a un altro servizio; l'obiettivo va nella riga dopo")),
            ["/help"] = ("/help /resume", Ui.Text("l'aiuto di un comando, con un esempio")),
            ["/exit"] = ("/exit", Ui.Text("esce dal REPL; la conversazione resta e si riprende con 'pa --continue'")),
        };

    /// <summary>
    /// Esempio e nota di un comando, oppure null se non esiste.
    ///
    /// Accetta il nome con o senza la barra: chi scrive <c>/help resume</c> intende <c>/resume</c>, e
    /// rispondere Â«comando sconosciutoÂ» per una barra sarebbe pedanteria.
    /// </summary>
    internal static (string Example, string Note)? For(string? command)
    {
        string name = (command ?? string.Empty).Trim();

        if (name.Length == 0)
        {
            return null;
        }

        if (name[0] is not '/')
        {
            name = "/" + name;
        }

        return Entries.TryGetValue(name, out (string Example, string Note) entry) ? entry : null;
    }
}
