using System.Text;
using System.Text.RegularExpressions;

namespace PA.Router.Registry.Loading;

/// <summary>
/// Gli identificatori dei servizi che il router genera da solo.
///
/// Fino alla S117 un modello aggiunto dal setup nasceva come <c>model-17af1d65a13f4886babdb2d79cac433e</c>:
/// unico, ma illeggibile, e finiva davanti all'operatore in ogni elenco che non passasse dal nome. Ora
/// nasce da cio' che si sa quando lo si crea — l'istanza da cui viene e il modello scelto — e resta
/// comunque unico: <c>chat-locale-llama3-2-3b</c>.
///
/// L'identificatore non si cambia dopo, se non con una migrazione esplicita: a lui sono legate conferme,
/// profili e memoria, e la migrazione li riscrive tutti insieme (<see cref="ConfigMigration"/>).
/// </summary>
public static partial class ServiceIds
{
    /// <summary>Lunghezza massima ammessa dallo schema del registro.</summary>
    private const int MaximumLength = 64;

    /// <summary>La forma generata prima della S117: <c>model-</c> e trentadue cifre esadecimali.</summary>
    [GeneratedRegex("^model-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedForm();

    /// <summary>Vero per un identificatore generato nella forma opaca che la S117 ha tolto.</summary>
    public static bool IsOpaque(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return GeneratedForm().IsMatch(id);
    }

    /// <summary>
    /// Identificatore leggibile e libero: il prefisso dell'istanza, poi il modello. Se e' gia' preso si
    /// aggiunge un numero, mai un codice casuale.
    /// </summary>
    /// <param name="prefix">Da dove viene il servizio: di norma l'identificatore dell'istanza.</param>
    /// <param name="model">Il modello scelto.</param>
    /// <param name="taken">Identificatori gia' usati nel registro.</param>
    public static string Readable(string prefix, string model, IReadOnlyCollection<string> taken)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(taken);

        string head = Slug(prefix);
        string tail = Slug(model);
        string stem = head.Length == 0 ? tail : tail.Length == 0 ? head : head + "-" + tail;

        // Lo schema vuole una lettera in testa: un modello che comincia con una cifra non basta.
        if (stem.Length == 0 || stem[0] is < 'a' or > 'z') { stem = "service-" + stem; }

        stem = Trim(stem, MaximumLength - 4);

        string candidate = stem;

        for (int number = 2; taken.Contains(candidate); number++)
        {
            candidate = stem + "-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    /// <summary>Solo lettere minuscole, cifre e trattini singoli: la grammatica degli identificatori.</summary>
    private static string Slug(string value)
    {
        StringBuilder result = new(value.Length);

        foreach (char character in value.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9') { result.Append(character); }
            else if (result.Length > 0 && result[^1] != '-') { result.Append('-'); }
        }

        return result.ToString().Trim('-');
    }

    private static string Trim(string value, int length) =>
        value.Length <= length ? value : value[..length].TrimEnd('-');
}
