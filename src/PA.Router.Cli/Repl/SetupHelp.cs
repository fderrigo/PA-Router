using System.Text.Json.Nodes;

namespace PA.Router.Cli.Repl;

/// <summary>Vocabolario utente: descrive il contratto, non decide validita' o ammissibilita'.</summary>
internal static class SetupHelp
{
    internal static bool Contains(string key) => Ui.Contains("field." + key + ".name");
    internal static bool Known(string key) => Contains(key) || DependencyDescription(key) is not null;
    internal static string Name(string key) => Contains(key) ? Ui.Text("field." + key + ".name") : key;
    internal static string Description(string key) => DependencyDescription(key) is string dependency ? Ui.Text(dependency)
        : Contains(key) ? Ui.Text("field." + key + ".description") : Ui.Text("Elemento di questa configurazione; apri per vedere valore e vincoli.");
    private static string? DependencyDescription(string key) => key switch
    {
        "memory" => "assistDeps.memory",
        "embeddingServiceId" => "assistDeps.localEmbedding",
        "contentMode" => "assistDeps.contentMode",
        "retentionDays" => "assistDeps.retention",
        "serviceProfileId" => "assistDeps.apiProfile",
        "sources" => "assistDeps.sources",
        "environment" => "assistDeps.environment",
        "exporter" => "assistDeps.exporter",
        "allowExecutableTools" => "assistDeps.legacyTools",
        "expiresAt" => "assistDeps.expiresAt",
        _ => null,
    };
    internal static string Label(string key) => Name(key) + " - " + Description(key);
    internal static string Details(string key, string label)
    {
        string? explanation = key switch
        {
            "models" or "model" => Ui.Text("Un modello presente sul server non partecipa automaticamente al routing. Devi selezionarlo, completare i requisiti e salvare."),
            "attest" or "attestations" => Ui.Text("help.attestations.details"),
            "routing" => Ui.Text("Il router sceglie solo fra servizi che hanno gia' superato i controlli. La preferenza locale tra candidati equivalenti non e' supportata: la collocazione e' un vincolo di ammissibilita', non un criterio di ranking. Automatico non privilegia il locale; Preferisci abbonamento riguarda il tipo di contratto. Nessuna preferenza rende ammissibile un servizio escluso."),
            "processingRegions" => Ui.Text("help.processingRegions.details"),
            "memory" or "contentMode" => Ui.Text("assistDeps.memoryDetails"),
            "retentionDays" => Ui.Text("assistDeps.retentionDetails"),
            "embeddingServiceId" => Ui.Text("assistDeps.embeddingDetails"),
            "sources" or "source" or "fingerprint" => Ui.Text("assistDeps.toolsDetails"),
            "serviceProfileId" => Ui.Text("assistDeps.apiDetails"),
            "environment" or "exporter" or "allowExecutableTools" => Description(key),
            "controls" => Ui.Text("Dichiarare una garanzia non dimostra che sia disponibile: il runtime verifica osservazioni e attestazioni prima dell'utilizzo."),
            "secret" or "secretRef" => Ui.Text("Il riferimento e' un nome nella configurazione. Il valore della chiave si inserisce da Credenziale API e viene conservato nell'archivio cifrato."),
            "confirm" => Ui.Text("Puoi continuare a modificare altre sezioni. Finche' non usi Salva configurazione, queste modifiche non sono persistite."),
            "save" => Ui.Text("Il salvataggio scrive, rilegge, valida e confronta i dati. Un errore non viene presentato come un salvataggio riuscito."),
            _ => null,
        };
        return label + (explanation is null ? "" : " | " + explanation) + Ui.Text(" | Riferimento tecnico: ") + key;
    }

    internal static string BooleanDescription(string key, bool enabled) => (key, enabled) switch
    {
        ("required", true) => Ui.Text("La richiesta si ferma se questa funzione non e' disponibile."),
        ("required", false) => Ui.Text("La richiesta puo' proseguire anche senza questa funzione."),
        ("preferSubscription", true) => Ui.Text("Dai precedenza ai servizi in abbonamento ammessi dal profilo."),
        ("preferSubscription", false) => Ui.Text("Non dare precedenza ai servizi in abbonamento."),
        ("redactContacts", true) => Ui.Text("Oscura i recapiti rilevati; i dati restano soggetti ai vincoli di privacy."),
        ("redactContacts", false) => Ui.Text("Non applicare l'oscuramento dei recapiti richiesto da questa opzione."),
        ("allowLocalMemoryProcessing", true) => Ui.Text("Consenti l'elaborazione locale dei ricordi entro i vincoli di privacy."),
        ("allowLocalMemoryProcessing", false) => Ui.Text("Non autorizzare l'elaborazione locale dei ricordi con questo profilo."),
        ("nativeSession", true) => Ui.Text("help.nativeSession.on"),
        ("nativeSession", false) => Ui.Text("help.nativeSession.off"),
        ("thinking", true) => Ui.Text("help.thinking.on"),
        ("thinking", false) => Ui.Text("help.thinking.off"),
        ("promptCaching", true) => Ui.Text("help.promptCaching.on"),
        ("promptCaching", false) => Ui.Text("help.promptCaching.off"),
        ("toolCalling", true) => Ui.Text("Dichiara il supporto agli strumenti; serve anche la verifica del router."),
        ("toolCalling", false) => Ui.Text("Dichiara che il modello non supporta le chiamate a strumenti."),
        ("allowExecutableTools", _) => Ui.Text("Campo legacy: questa scelta non concede strumenti o permessi aggiuntivi."),
        _ => enabled ? Ui.Text("Usa questa funzione quando tutti i suoi requisiti sono soddisfatti.")
            : Ui.Text("Disattiva questa funzione conservando le altre impostazioni."),
    };
    internal static string Value(JsonNode? node)
    {
        if (node is null) { return Ui.Text("non impostato"); }
        if (node is JsonArray array) { return array.Count == 0 ? Ui.Text("value.none") : array.Count == 1 ? Ui.Text("value.one") : array.Count + Ui.Text(" elementi"); }
        if (node is JsonObject) { return Ui.Text("apri la scheda"); }
        string value = node.ToString();
        if (value.Contains("REPLACE_WITH", StringComparison.OrdinalIgnoreCase)) { return Ui.Text("da configurare"); }
        if (value == "true") { return Ui.Text("Si"); }
        if (value == "false") { return Ui.Text("No"); }
        return Name(value);
    }

}
