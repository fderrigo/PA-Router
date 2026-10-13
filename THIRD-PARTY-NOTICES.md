# Componenti di terze parti e attribuzioni

Il codice originale PA Router è distribuito secondo [LICENSE](LICENSE), PolyForm Noncommercial 1.0.0. Questa licenza non sostituisce né restringe le licenze delle dipendenze. I diritti sulle opere di terzi restano ai rispettivi titolari.

Questo inventario descrive i componenti di produzione della **v0.2.0-beta.3**, inclusi i runtime nei download autonomi Windows e Linux. I pacchetti acquisiti solo per sviluppo o test non sono presentati come dipendenze distribuite. Non è una certificazione legale o una prova completa dell'autorialità di ogni riga.

## Apache 2.0: SQLitePCLRaw

I seguenti pacchetti **2.1.12** dichiarano **Apache-2.0**:

- `SQLitePCLRaw.bundle_e_sqlite3`
- `SQLitePCLRaw.core`
- `SQLitePCLRaw.lib.e_sqlite3`
- `SQLitePCLRaw.provider.e_sqlite3`

Nei pacchetti PA Router sono inclusi il testo Apache 2.0 e il NOTICE di SQLitePCLRaw, con l'attribuzione SourceGear. Questi avvisi devono accompagnare le redistribuzioni alle condizioni applicabili. SQLite, contenuto nativo nel bundle, ha uno status distinto dai wrapper SQLitePCLRaw: consultare anche gli avvisi del pacchetto.

La [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0) permette anche usi commerciali, a condizione di rispettarne i termini. PA Router non revoca tali diritti sui componenti Apache, né la loro presenza rende automaticamente Apache il codice originale PA Router. Non è dichiarata alcuna affiliazione o approvazione da parte della Apache Software Foundation.

## Modello facoltativo RizzoPII

PA Router può usare il modello esterno [`rizzoaiacademy/rizzo-pii-0.3B`](https://huggingface.co/rizzoaiacademy/rizzo-pii-0.3B) per il rilevamento locale di entità personali. Autore: **Simone Rizzo / Rizzo AI Academy**. La [scheda del modello](https://huggingface.co/rizzoaiacademy/rizzo-pii-0.3B) dichiara **MIT © 2026 Simone Rizzo**. Questo modello **non è incluso** nel sorgente o negli archivi binari PA Router e quindi non compare fra i 22 componenti effettivamente distribuiti o nell'SBOM. L'operatore lo acquisisce e abilita separatamente, rispettando i termini applicabili ai pesi.

Il [repository dell'applicazione autonoma RizzoPII](https://github.com/Rizzo-AI-Academy/rizzo-pii) dichiara MIT per il proprio sorgente e AGPL-3.0 per i suoi binari che incorporano PyMuPDF. PA Router non distribuisce quell'applicazione né PyMuPDF. La licenza Apache 2.0 elencata sopra riguarda SQLitePCLRaw, **non** il modello RizzoPII. Nessuna licenza di un terzo trasferisce la titolarità del codice originale PA Router.

## Inventario della release

| Componente | Versione | Licenza / testi distribuiti |
|---|---|---|
| Google.Protobuf | 3.30.2 | BSD-3-Clause |
| Humanizer.Core | 3.0.10 | MIT e avvisi inclusi |
| Json.More.Net | 3.0.1 | Sorgente MIT; OSMFEULA per la distribuzione binaria |
| JsonPointer.Net | 7.0.2 | Sorgente MIT; OSMFEULA per la distribuzione binaria |
| JsonSchema.Net | 9.4.0 | Sorgente MIT; OSMFEULA per la distribuzione binaria |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.12 | MIT |
| Microsoft.ML.OnnxRuntime / Microsoft.ML.OnnxRuntime.Managed | 1.30.0 | MIT e avvisi delle dipendenze incorporate |
| Microsoft.ML.Tokenizers | 2.0.0 | MIT e avvisi inclusi |
| Microsoft.AspNetCore.App.Runtime, variante win-x64 o linux-x64 | 10.0.12 | MIT e avvisi runtime |
| Microsoft.NETCore.App.Runtime, variante win-x64 o linux-x64 | 10.0.12 | MIT e avvisi runtime |
| Spectre.Console / Spectre.Console.Ansi | 0.57.2 | MIT |
| SQLitePCLRaw.bundle_e_sqlite3 / core / lib.e_sqlite3 / provider.e_sqlite3 | 2.1.12 | Apache-2.0, LICENSE e NOTICE |
| System.CommandLine | 2.0.12 | MIT |
| System.Numerics.Tensors | 9.0.0 | MIT e avvisi inclusi |
| System.Security.Cryptography.ProtectedData | 10.0.12 | MIT e avvisi inclusi |
| YamlDotNet | 18.1.0 | MIT |

Le righe raggruppate rappresentano complessivamente **22 componenti per piattaforma**. Gli avvisi completi di ONNX, Tokenizers e dei runtime possono elencare ulteriori opere incorporate: questa tabella non li sostituisce.

### Caso json-everything

`Json.More.Net`, `JsonPointer.Net` e `JsonSchema.Net` dichiarano `OSMFEULA.txt` nei metadati NuGet. L'accordo descrive una maintenance fee per gli utilizzatori delle distribuzioni binarie nelle condizioni di attività e ricavi specificate dal suo testo. Indica inoltre che il sorgente è MIT e che la redistribuzione deve rispettare le relative attribuzioni. Non descriviamo quindi i pacchetti binari come soggetti al solo MIT.

Conserviamo **entrambi** il testo OSMFEULA ricevuto nei pacchetti e il testo MIT con copyright `.NET Foundation and Contributors`, recuperato dalle revisioni upstream dichiarate nei rispettivi metadati. L'autorizzazione commerciale del titolare di PA Router non concede esenzioni da accordi con altri titolari.

## Dove trovare i testi completi

In ciascun [archivio della release](https://github.com/fderrigo/PA-Router/releases/tag/v0.2.0-beta.3) sono presenti:

- `SBOM.json`: componenti effettivamente pubblicati, versioni, identificatori, hash e riferimenti ai testi;
- `THIRD-PARTY-NOTICES.md`: inventario specifico di quell'archivio con collegamenti ai file;
- `licenses/`: licenze, avvisi, metadati upstream e provenienza dei supplementi;
- `CHECKSUMS.txt`: digest dei file dell'archivio.

La correzione delle attribuzioni del 13 ottobre 2026 aggiunge i testi MIT json-everything mancanti nelle prime confezioni beta.3, conservando l'EULA. **Se hai scaricato un archivio precedente alla correzione, scarica quello aggiornato e usa i nuovi checksum.** Il codice eseguibile non cambia; i digest degli archivi cambiano per l'aggiunta degli avvisi.

Restano dichiarati quattro avvisi di provenienza per SQLitePCLRaw: i testi provengono da un tag upstream fissato per commit e digest, che non è un'attestazione indipendente della corrispondenza fra binario e sorgente.

Ollama, le CLI degli agenti e i pesi dei modelli **non sono inclusi** nei download PA Router: hanno termini propri da verificare quando vengono installati o utilizzati.
