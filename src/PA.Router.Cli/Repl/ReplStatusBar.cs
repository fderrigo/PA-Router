using System.Text;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Localization;
using PA.Router.Hosting.Runtime;
using Spectre.Console;

namespace PA.Router.Cli.Repl;

/// <summary>
/// Disegno della barra di stato: una riga sola, sopra l'invito, con i campi sempre nello stesso
/// ordine e sempre nello stesso posto.
///
/// Il calcolo sta in <see cref="ReplStatus"/> (INV-22): qui si decide soltanto come appare. Tre
/// regole la tengono onesta e la tengono intera:
///
/// - **Non va mai a capo.** Il compositore di riga conta le righe occupate dall'invito per gestire
///   il testo che si avvolge; una barra avvolta sfalserebbe quel conteggio, ed e' il difetto
///   dell'incolla chiuso in S116. Quando non entra si tolgono i campi dal fondo, e si dichiara.
/// - **Il colore dice lo stesso della parola.** Un terminale senza colori mostra le stesse
///   informazioni: il simbolo e il testo bastano da soli.
/// - **Niente che non si possa mostrare**: nome del modello, non l'indirizzo; nome della cartella,
///   non il percorso; nessun identificatore di sessione.
/// </summary>
internal static class ReplStatusBar
{
    /// <summary>Separatore fra i campi.</summary>
    private const string Divider = "  ";

    /// <summary>Campo pronto da disegnare: simbolo, testo e colore.</summary>
    internal readonly record struct Field(string Symbol, string Text, Color Color);

    /// <summary>
    /// Scrive la barra. Senza console ANSI non si disegna nulla: un copione e una prova devono
    /// vedere l'uscita di prima, e una riga di stato in mezzo a un'uscita rediretta e' rumore.
    /// </summary>
    internal static void Write(IAnsiConsole? console, StatusView status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (console is null) { return; }

        IReadOnlyList<Field> fields = Fields(status);
        int width = Math.Max(0, console.Profile.Width);

        console.Write(Compose(fields, width));
        console.WriteLine();
    }

    /// <summary>
    /// La stessa riga in testo semplice, per chi non disegna con i colori: <c>/status</c> la scrive
    /// cosi' anche quando la barra sopra l'invito e' spenta. Gli stessi campi, lo stesso ordine —
    /// il colore non porta mai un'informazione che il testo non abbia.
    /// </summary>
    internal static string Plain(StatusView status) =>
        string.Join(Divider, Fields(status).Select(field => field.Symbol + " " + field.Text));

    /// <summary>
    /// I campi che entrano nella larghezza, dal primo; quando ne restano fuori la barra finisce con
    /// un segno che lo dice, invece di far sparire in silenzio meta' dello stato.
    /// </summary>
    internal static Paragraph Compose(IReadOnlyList<Field> fields, int width)
    {
        Paragraph line = new();
        int used = 0;

        for (int index = 0; index < fields.Count; index++)
        {
            Field field = fields[index];
            string text = (index == 0 ? string.Empty : Divider) + field.Symbol + " " + field.Text;
            bool last = index == fields.Count - 1;
            int room = width - (last ? 0 : 2);

            if (width > 0 && used + text.Length > room)
            {
                line.Append(Divider + "…", new Style(ReplChatStyle.Secondary));
                break;
            }

            line.Append(text, new Style(field.Color));
            used += text.Length;
        }

        return line;
    }

    /// <summary>
    /// Ordine dei campi: prima cio' che cambia il significato di una risposta — chi risponde e dove
    /// vanno i dati — poi cio' che cambia il modo di lavorare, poi il resto. Chi viene troncato,
    /// viene troncato dal fondo.
    /// </summary>
    internal static IReadOnlyList<Field> Fields(StatusView status)
    {
        ArgumentNullException.ThrowIfNull(status);

        List<Field> fields =
        [
            status.Answering is string answering
                ? new Field("◆", answering, ReplChatStyle.Response)
                : new Field("◇", Ui.Text("status.noAnswerYet"), ReplChatStyle.Secondary),
            Reach(status),
            new Field("⬢", ProfilePresentation.Name(status.ProfileId, status.ProfileName), ReplChatStyle.Accent),
            MemoryField(status),
        ];

        if (status.MinimumClass is not DataClass.Public)
        {
            // La classe minima si mostra solo quando e' stata alzata: «public» e' l'assenza di una
            // restrizione, e un campo che dice «nessuna restrizione» occupa spazio per niente.
            fields.Add(new Field("⚑", Ui.Text("class." + status.MinimumClass.ToWire()), ReplChatStyle.Secondary));
        }

        fields.Add(status.ContentPersisted
            ? new Field("▣", Ui.Text("status.persisted"), ReplChatStyle.Secondary)
            : new Field("▢", Ui.Text("status.ephemeral"), ReplChatStyle.Warning));

        if (status.WorkspaceName is string workspace)
        {
            fields.Add(new Field("▭", workspace + Grants(status), status.Writable ? ReplChatStyle.Warning : ReplChatStyle.Secondary));
        }

        return fields;
    }

    /// <summary>Concessioni del recinto, accodate al nome della cartella: scrittura e comandi.</summary>
    private static string Grants(StatusView status)
    {
        StringBuilder grants = new();

        if (status.Writable) { grants.Append(" ✎"); }
        if (status.Shell) { grants.Append(" ⌘"); }

        return grants.ToString();
    }

    /// <summary>
    /// Dove puo' andare l'inferenza. E' il campo che nessuno deve dedurre: il verde dice «resta
    /// qui», il giallo «esce da questa macchina», e la parola lo ripete per chi non ha i colori.
    /// </summary>
    private static Field Reach(StatusView status) => status.Reach switch
    {
        StatusReach.ThisMachine => new Field("⌂", Ui.Text("status.reach.local"), ReplChatStyle.Success),
        StatusReach.LocalNetwork => new Field("⇄", Ui.Text("status.reach.lan"), ReplChatStyle.Warning),
        _ => new Field("☁", Ui.Text("status.reach.cloud"), ReplChatStyle.Warning),
    };

    /// <summary>
    /// Che cosa fa la memoria, e fin dove arriva. L'ambito e' la meta' che manca: una memoria che
    /// funziona ma resta nella conversazione sparisce aprendone una nuova, e chi non lo sa crede di
    /// aver perso dei ricordi che sono ancora li'.
    /// </summary>
    private static Field MemoryField(StatusView status)
    {
        Field field = status.Memory switch
        {
            StatusMemory.Semantic => new Field("✦", Ui.Text("status.memory.semantic"), ReplChatStyle.Success),
            StatusMemory.LexicalOnly => new Field("✧", Ui.Text("status.memory.lexical"), ReplChatStyle.Warning),
            StatusMemory.NeverRecords => new Field("✧", Ui.Text("status.memory.never"), ReplChatStyle.Error),
            _ => new Field("✧", Ui.Text("status.memory.off"), ReplChatStyle.Secondary),
        };

        return status.MemoryStaysInSession && status.Memory is not StatusMemory.Off and not StatusMemory.NeverRecords
            ? field with { Text = field.Text + " " + Ui.Text("status.memory.sessionOnly") }
            : field;
    }
}
