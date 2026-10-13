using PA.Router.Cli.Localization;
using PA.Router.Memory;

namespace PA.Router.Cli.Repl;

/// <summary>
/// Frasi della memoria condivise dalle rese: il terminale e i comandi scrivono la stessa cosa (INV-22).
/// </summary>
internal static class MemoryText
{
    /// <summary>
    /// Quando e' fallito l'ultimo tentativo di indicizzazione.
    ///
    /// Senza il momento un codice si legge come «adesso»: un guasto registrato prima che l'operatore correggesse la
    /// configurazione faceva credere rotto cio' che era gia' stato sistemato. Il prodotto gira con la
    /// globalizzazione invariante, quindi l'ora si scrive in forma numerica e la data non compare: un guasto piu'
    /// vecchio di un giorno non ha un'ora utile da mostrare, e si dice soltanto che e' precedente.
    /// </summary>
    internal static string Failed(DateTimeOffset? at)
    {
        if (at is not DateTimeOffset moment) { return Ui.Text("memory.failedEarlier"); }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        return now - moment > TimeSpan.FromHours(24)
            ? Ui.Text("memory.failedEarlier")
            : Ui.Format("memory.failedAt {0}", moment.ToLocalTime().ToString("HH:mm", Formats.Invariant));
    }

    /// <summary>
    /// Come si e' cercato, in una frase (23 settembre 2026): prima si scriveva «Modo: hybrid  indice: ready».
    /// </summary>
    internal static string Search(string modeWire, string statusWire) =>
        Ui.Text("memory.mode." + modeWire) + " · " + Ui.Text("memory.index." + statusWire);

    /// <summary>
    /// Un ricordo trovato, in due righe: la data e il testo, poi che cos'e' e il codice che serve a
    /// <c>pa memory show</c> e <c>forget</c>. Niente punteggio: il numero della fusione non ha una scala che una persona
    /// possa leggere, e l'ordine dice gia' chi viene prima.
    /// </summary>
    internal static IReadOnlyList<string> Hit(int position, DateTimeOffset createdAt, string kindWire, string verificationWire, string title, string id)
    {
        string statement = ObservationFactory.Statement(title);
        if (statement.Length > TitleLength) { statement = statement[..TitleLength].TrimEnd() + "…"; }

        return
        [
            position.ToString(Formats.Invariant) + ". " + createdAt.ToString("yyyy-MM-dd", Formats.Invariant) + " — «" + ReplText.Sanitize(statement) + "»",
            "   " + Kind(kindWire) + ", " + Ui.Text("memory.verification." + verificationWire) + " · " + Ui.Format("memory.code {0}", id),
        ];
    }

    /// <summary>
    /// Un ricordo intero per una persona (24 settembre 2026): prima `pa memory show` stampava il contenuto grezzo, con
    /// «[note]» in testa e «Prove osservate:» con l'identificatore dell'esecuzione in coda. Qui la data, che cos'e', la
    /// classe dei dati, il testo che il ricordo porta, e come si toglie.
    /// </summary>
    internal static IReadOnlyList<string> Show(
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        string kindWire,
        string verificationWire,
        string classWire,
        string content,
        string id)
    {
        return
        [
            Ui.Format("memory.show.header {0} {1} {2} {3}",
                createdAt.ToString("yyyy-MM-dd", Formats.Invariant),
                Kind(kindWire),
                Ui.Text("memory.verification." + verificationWire),
                Ui.Contains("class." + classWire) ? Ui.Text("class." + classWire) : classWire),
            string.Empty,
            ReplText.Sanitize(ObservationFactory.Statement(content)),
            string.Empty,
            Ui.Format("memory.show.footer {0} {1}", expiresAt.ToString("yyyy-MM-dd", Formats.Invariant), id),
        ];
    }

    /// <summary>Caratteri del testo di un ricordo in un elenco.</summary>
    private const int TitleLength = 110;

    private static string Kind(string wire) => Ui.Contains("memory.kind." + wire) ? Ui.Text("memory.kind." + wire) : wire;

    /// <summary>
    /// Lo stato della memoria in frasi. Il modello si nomina con il suo nome, non con l'impronta interna
    /// (<c>ollama|embeddinggemma:latest|unknown|768|v1</c>).
    /// </summary>
    internal static IReadOnlyList<string> Status(string? fingerprint, int observations, int pending, int running, int failed)
    {
        string[] parts = fingerprint?.Split('|') ?? [];

        return
        [
            parts.Length >= 2 ? Ui.Format("memory.status.model {0}", parts[1]) : Ui.Text("memory.status.noModel"),
            observations == 1 ? Ui.Text("memory.status.one") : Ui.Format("memory.status.many {0}", observations),
            pending + running + failed == 0
                ? Ui.Text("memory.status.idle")
                : Ui.Format("memory.status.queue {0} {1} {2}", pending, running, failed),
        ];
    }
}
