# PA Router

**Un punto di accesso locale per usare più servizi AI, con regole su dati, strumenti, sessioni e memoria.**

PA Router è un'applicazione da terminale per Windows e Linux. Riceve la tua richiesta, prepara il contesto, applica i controlli di privacy e le policy configurate e sceglie fra i servizi ammessi. Puoi lavorare con modelli Ollama, API e programmi ufficiali di agenti senza ricostruire ogni volta l'intero contesto di lavoro.

Sviluppato da **D'Errigo Filippo Francesco**. Il codice PA Router è **source-available sotto PolyForm Noncommercial 1.0.0**: uso, studio, modifica e redistribuzione per gli scopi consentiti dalla licenza; per gli usi commerciali non coperti occorre un accordo scritto separato con l'autore. [Licenza completa](LICENSE) · [Licenze delle dipendenze](THIRD-PARTY-NOTICES.md).

> **Beta sperimentale:** il recupero della memoria non ha ancora raggiunto gli obiettivi di qualità. Può omettere ricordi utili o includere informazioni non pertinenti. Verifica sempre i fatti recuperati e gli output dei modelli.

## Scaricare la beta

I pacchetti includono il runtime .NET: **non serve installare l'SDK per eseguirli**. Ollama, modelli, programmi degli agenti ed eventuali credenziali dei provider vanno predisposti separatamente.

| Sistema | Download |
|---|---|
| Windows x64 | [Archivio ZIP](https://github.com/fderrigo/PA-Router/releases/download/v0.2.0-beta.3/pa-router-0.2.0-beta.3-win-x64.zip) |
| Linux x64, glibc | [Archivio tar.gz](https://github.com/fderrigo/PA-Router/releases/download/v0.2.0-beta.3/pa-router-0.2.0-beta.3-linux-x64.tar.gz) |
| Integrità e note | [Release, checksum SHA-256 e limitazioni](https://github.com/fderrigo/PA-Router/releases/tag/v0.2.0-beta.3) |

Linux è stato provato su Debian 12 con `libssl3` e `ca-certificates`. Il pacchetto è per x86_64/glibc, non per ARM o Alpine/musl. Gli archivi non sono firmati; su Windows non è presente una firma Authenticode.

## Cosa fa per te

### 1. Riunisce i servizi AI in un solo flusso

Usi una CLI e una sessione di lavoro per inviare richieste ai servizi configurati. Il registro descrive capacità, collocazione dell'inferenza e controlli disponibili. Il router sceglie fra i servizi consentiti dalle tue regole; nel terminale puoi vedere quelli disponibili e orientare la selezione.

Sono presenti integrazioni per **Ollama, API compatibili OpenAI, API Anthropic, Claude Code CLI, Codex CLI e OpenCode CLI**. La loro presenza nel codice non significa che siano già abilitate o certificate per ogni ambiente: contano configurazione, probe, compatibilità osservata e policy. Le CLI degli agenti usano i rispettivi programmi, account e controlli nativi. L'uso personale tramite subscription resta distinto dall'API chatbot: il profilo chatbot iniziale distribuito usa Ollama locale e il canale API esclude agenti e servizi con credenziali subscription.

### 2. Controlla dati e permessi prima dell'invio

La richiesta e il contesto, inclusi eventuali ricordi e skill, attraversano l'analisi di privacy prima della scelta del servizio. Le policy stabiliscono quali dati e capacità possono raggiungere quali destinazioni; un divieto non viene compensato da un servizio più veloce o economico.

Per riconoscere anche dati che una regola di formato può perdere, PA Router supporta facoltativamente il modello locale **[RizzoPII (`rizzo-pii-0.3B`)](https://huggingface.co/rizzoaiacademy/rizzo-pii-0.3B)** di **Simone Rizzo / Rizzo AI Academy**. Il rilevatore cerca entità come nomi, indirizzi, documenti e identificativi italiani. Gira nel processo del router tramite ONNX e affianca i controlli deterministici, che restano necessari per credenziali e identificativi verificabili. Il modello segnala i dati: sono le regole di PA Router a decidere classe e destinazioni ammesse. Non è l'applicazione autonoma RizzoPII e non esegue il suo flusso completo di anonimizzazione reversibile.

Il modello nasce **disabilitato**; i pesi non sono inclusi nei download e non vengono scaricati durante un turno. Per usarlo, l'operatore deve procurarsi i pesi dal titolare, convertirli nel formato ONNX supportato, installarli nella propria directory dati, abilitare il servizio `pii-detection` con adapter `onnx-local` e verificarlo con `pa doctor`. Una scansione avviata con il modello abilitato che non riesce a concludere è trattata come incompleta e blocca l'invio al cloud. Il rilevamento resta probabilistico: controlla i risultati prima di affidargli dati reali.

Ogni invio richiede un'autorizzazione valida per quel payload e la registrazione dell'intenzione nell'audit. L'audit e la telemetria usano metadati controllati, senza includere intenzionalmente prompt e risposte grezze. Questi controlli riducono il rischio, ma non rendono infallibile il riconoscimento dei dati sensibili e non sostituiscono firewall o isolamento del sistema operativo.

### 3. Organizza il lavoro in sessioni

Puoi creare, nominare, riprendere, biforcare e archiviare sessioni. Il router conserva un contesto strutturato e prepara il passaggio a un altro servizio senza fingere di trasferire una sessione nativa fra programmi diversi.

La modalità iniziale è `memory_only`: i contenuti non diventano una cronologia durevole. Per conservarli devi abilitare la persistenza cifrata. Su Windows la protezione usa DPAPI; su Linux usa una passphrase, richiesta interattivamente oppure fornita tramite `PA_ROUTER_PASSPHRASE`.

### 4. Recupera memoria pertinente al lavoro

La memoria integrata combina ricerca lessicale e vettoriale con embedding tramite un servizio Ollama configurato. Le ricerche rispettano progetto e sessione prima del recupero; i risultati vengono ricontrollati prima di essere inseriti nel contesto. Puoi consultare la memoria, esaminarne la sequenza temporale, richiedere la reindicizzazione e dimenticare osservazioni.

Le informazioni classificate come segrete non vengono memorizzate; i dati personali o sensibili restano confinati alla sessione prevista dalle regole. I contenuti persistenti e i vettori sono cifrati, mentre l'indice lessicale lavora in memoria. Se gli embedding non sono disponibili, il comportamento è dichiarato: ricerca solo lessicale oppure blocco del turno, secondo il profilo. Non è previsto un ripiego cloud nascosto per la memoria.

Questa funzione è utile per riutilizzare contesto, ma **la beta non certifica accuratezza, completezza o corretta astensione della memoria**. Un test di velocità o una build riuscita non dimostrano queste qualità.

### 5. Riutilizza skill e pacchetti di istruzioni

Puoi installare pacchetti verificati tramite manifesto e digest, ammetterli nel progetto e selezionare le skill testuali utili al turno. Il loro contenuto riattraversa privacy e policy: una skill non concede da sola permessi o accesso ai dati.

Le skill testuali sono distinte da plugin e integrazioni native dei singoli provider. La compatibilità nativa non è universale e non viene dedotta dal solo nome del pacchetto.

### 6. Offre automazione e un'API locale

La CLI legge il prompt da standard input e supporta risposte JSON. Un worker avviato esplicitamente permette ai client di condividere il runtime e lo store. `pa serve` espone un'API autenticata sul solo loopback, con token di servizio, token di sessione, idempotenza ed eventi di stato.

Lo stato è embedded in SQLite: non serve un database server. I backup comprendono i dati necessari e un manifesto di integrità; il comando di verifica controlla il backup, non esegue un ripristino. Il prodotto offre CLI e interfaccia interattiva da terminale, non una web UI.

## Primo avvio

Verifica il checksum dell'archivio scaricato, poi estrailo. Usa una directory dati dedicata. I servizi partono disabilitati: il setup permette di configurarli e scegliere modelli già installati, senza scaricarli automaticamente.

### Windows — PowerShell

Dalla cartella estratta:

```powershell
.\pa.exe --help
.\pa.exe init --data .\dati-pa --persist-content
.\pa.exe setup --data .\dati-pa
.\pa.exe doctor --data .\dati-pa
.\pa.exe --data .\dati-pa
```

### Linux — terminale interattivo

```bash
sha256sum -c pa-router-0.2.0-beta.3-linux-x64.tar.gz.sha256
tar -xzf pa-router-0.2.0-beta.3-linux-x64.tar.gz
cd pa-router-linux-x64
./pa --help
./pa init --data "$HOME/.local/share/pa-router" --persist-content
./pa setup --data "$HOME/.local/share/pa-router"
./pa doctor --data "$HOME/.local/share/pa-router"
./pa --data "$HOME/.local/share/pa-router"
```

Per la persistenza Linux conserva la passphrase: serve anche agli accessi successivi. Non inserirla nel repository o in una segnalazione pubblica. Per provare la modalità senza contenuti persistenti, ometti `--persist-content` quando inizializzi una directory nuova.

Una prima richiesta, dopo aver configurato e verificato il servizio:

```powershell
"Spiegami la differenza tra una lista e un insieme" | .\pa.exe run --stdin --data .\dati-pa --json
```

```bash
read -r -s -p 'Passphrase della directory dati: ' PA_ROUTER_PASSPHRASE
printf '\n'
export PA_ROUTER_PASSPHRASE
printf '%s\n' 'Spiegami la differenza tra una lista e un insieme' | ./pa run --stdin --data "$HOME/.local/share/pa-router" --json
unset PA_ROUTER_PASSPHRASE
```

Nell'esempio Linux la passphrase entra nell'ambiente senza scriverne il valore nella cronologia della shell. Quando stdin contiene il prompt della richiesta, il processo non può chiedere contemporaneamente la passphrase al terminale: deve riceverla tramite la variabile d'ambiente.

Con `--json`, stdout contiene il documento di risultato; progressi e diagnostica usano stderr. L'assenza di un servizio autorizzato produce un esito di errore o diniego: non avvia un provider alternativo aggirando le regole.

## Comandi utili durante il lavoro

Nel terminale interattivo usa `/help` per l'elenco aggiornato.

| Obiettivo | Comandi |
|---|---|
| Vedere stato e motivazioni della selezione | `/status`, `/why`, `/services` |
| Scegliere servizio o workspace | `/target`, `/workspace` |
| Organizzare le conversazioni | `/new`, `/sessions`, `/resume`, `/name`, `/transcript` |
| Lavorare con memoria e skill | `/memory`, `/skill` |
| Controllare configurazione e servizi | `/doctor`, `/setup` |
| Chiudere il terminale interattivo | `/exit` |

Dalla CLI trovi anche `pa session`, `pa memory`, `pa plugin`, `pa skill`, `pa config`, `pa runtime`, `pa serve` e `pa maintenance`. Aggiungi `--help` al comando per argomenti e opzioni; `pa --continue --data <directory>` riprende l'ultima sessione disponibile. Un'esecuzione con effetti incerti richiede una verifica e il recupero previsto dal prodotto, senza retry automatici che possano duplicare gli effetti.

## Sul blog

Scheda del progetto nella sezione Labs di [derrigo.it](https://www.derrigo.it): [PA Router](https://www.derrigo.it/Products/Info/11/pa-router)

Articoli correlati:

- [PA Router: dal model routing alla governance dell'esecuzione AI](https://www.derrigo.it/Articles/Info/11/pa-router-dal-model-routing-alla-governance-dell-esecuzione-ai)
- [Dall'algoritmo all'harness](https://www.derrigo.it/Articles/Info/9/dall-algoritmo-all-harness)
- [Claude Code, Codex e Planoro: come abbiamo organizzato il nostro flusso di sviluppo](https://www.derrigo.it/Articles/Info/13/claude-code-codex-e-planoro-come-abbiamo-organizzato-il-nostro-flusso-di)
- [Introdurre l'AI negli enti locali: oltre il chatbot per il cittadino](https://www.derrigo.it/Articles/Info/14/introdurre-l-ai-negli-enti-locali-oltre-il-chatbot-per-il-cittadino)
- [Vibe coding](https://www.derrigo.it/Articles/Info/3/vibe-coding)
- [Vibe coding: il codice lo mette l'AI, il giudizio no](https://www.derrigo.it/Articles/Info/5/vibe-coding-il-codice-lo-mette-l-ai-il-giudizio-no)
- [Oltre il vibe coding: quando l'intelligenza artificiale partecipa anche alla verifica del software](https://www.derrigo.it/Articles/Info/12/oltre-il-vibe-coding-quando-l-intelligenza-artificiale-partecipa-anche-alla)
- [Dove finisce la capacità che l'intelligenza artificiale libera](https://www.derrigo.it/Articles/Info/10/dove-finisce-la-capacita-che-l-intelligenza-artificiale-libera)

Chi sono: [Filippo D'Errigo](https://www.derrigo.it/Pages/Info/1/chi-sono)

## Licenza, proprietà e uso commerciale

Copyright © 2026 **D'Errigo Filippo Francesco**, per il codice originale PA Router di cui è titolare. La pubblicazione del sorgente concede i diritti indicati nella licenza, non trasferisce la titolarità del copyright.

- Puoi usare, studiare, modificare, creare fork e redistribuire PA Router per gli scopi ammessi dalla **[PolyForm Noncommercial 1.0.0](LICENSE)**, mantenendo termini e avvisi richiesti.
- Per vendere PA Router, includerlo in un prodotto commerciale o offrirlo come servizio commerciale, quando l'uso non è già coperto dai permessi della licenza, devi ottenere **un'autorizzazione scritta separata dall'autore prima dell'uso**. Una richiesta inviata o il silenzio dell'autore non sono un'autorizzazione.
- Per richiedere condizioni commerciali, contatta [fderrigo su GitHub](https://github.com/fderrigo) o apri una [richiesta nel repository](https://github.com/fderrigo/PA-Router/issues), senza pubblicare informazioni riservate.
- Le definizioni e le disposizioni su usi personali e organizzazioni non commerciali sono quelle del testo integrale della licenza. Questa sintesi non lo modifica.

**PA Router è source-available, non software open source con licenza approvata OSI.** Le dipendenze sono opere dei rispettivi autori e conservano le proprie condizioni, incluse Apache 2.0, MIT, BSD-3-Clause e gli accordi indicati nei pacchetti. La restrizione non commerciale di PA Router non viene imposta al codice di terzi; un'autorizzazione commerciale dell'autore di PA Router non sostituisce gli obblighi verso quei titolari.

In particolare, i quattro pacchetti **SQLitePCLRaw 2.1.12 sono Apache-2.0**. I pacchetti **Json.More.Net, JsonPointer.Net e JsonSchema.Net** includono un accordo OSMFEULA per le distribuzioni binarie; il sorgente è MIT. L'[inventario delle dipendenze](THIRD-PARTY-NOTICES.md) spiega questi casi. Nei download trovi `SBOM.json`, `THIRD-PARTY-NOTICES.md` e `licenses/`, con i testi e gli avvisi da conservare nella redistribuzione.

Il [modello RizzoPII](https://huggingface.co/rizzoaiacademy/rizzo-pii-0.3B) è attribuito a **Simone Rizzo / Rizzo AI Academy** e la sua scheda dichiara **MIT**. I pesi sono un'opera esterna da ottenere separatamente: la licenza PolyForm di PA Router non li copre. L'[applicazione autonoma RizzoPII](https://github.com/Rizzo-AI-Academy/rizzo-pii) non fa parte dei nostri archivi; i suoi binari distribuiti dal titolare hanno condizioni proprie.

## Compilare dal sorgente

Installa lo SDK .NET indicato in [global.json](global.json), quindi dalla radice del repository:

```text
dotnet restore PA.Router.sln --locked-mode
dotnet build PA.Router.sln -c Release --no-restore
dotnet run --project src/PA.Router.Cli -c Release --no-build -- --help
```

Gli schemi JSON, i modelli YAML e le migrazioni SQL in `resources/contracts` sono risorse usate dal programma. La CI pubblica controlla restore con dipendenze bloccate, build Release e help della CLI su Windows e Linux. Non esegue la suite privata né certifica la qualità della memoria. Le note della release riportano separatamente le prove eseguite sugli archivi.

## Supporto e sicurezza

- Problemi e suggerimenti: [GitHub Issues](https://github.com/fderrigo/PA-Router/issues).
- Vulnerabilità: [segnalazione privata GitHub](https://github.com/fderrigo/PA-Router/security/advisories/new).

Non allegare credenziali, database, backup, prompt privati o dati personali. La beta è distribuita senza SLA; garanzie e limitazioni sono definite nella licenza.
