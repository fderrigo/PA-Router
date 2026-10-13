using System.Text.RegularExpressions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Privacy.Detectors;

/// <summary>
/// Indirizzi di posta. Un match alza la classe a <see cref="DataClass.Personal"/>: la
/// sostituzione successiva non la riabbassa.
/// </summary>
public sealed partial class EmailDetector : RegexDetector
{
    /// <inheritdoc />
    public override string Name => "email";

    /// <inheritdoc />
    public override string Version => "email-v1";

    /// <inheritdoc />
    public override FindingType Type => FindingType.Email;

    /// <inheritdoc />
    public override DataClass ClassContribution => DataClass.Personal;

    /// <inheritdoc />
    protected override double Confidence => 0.9;

    /// <inheritdoc />
    protected override Regex Pattern => EmailPattern();

    [GeneratedRegex(
        @"[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]{1,63}(?:\.[A-Za-z0-9\-]{1,63}){1,4}",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        50)]
    private static partial Regex EmailPattern();
}

/// <summary>
/// Numeri di telefono plausibili. Il detector e' volutamente prudente sulla forma: un numero
/// riconosciuto alza la classe, un numero non riconosciuto non la abbassa, perche' la classe
/// dichiarata e il floor del profilo restano comunque applicati.
/// </summary>
public sealed partial class PhoneDetector : RegexDetector
{
    /// <inheritdoc />
    public override string Name => "phone";

    /// <inheritdoc />
    /// <remarks>
    /// v2 (24 settembre 2026): un numero attaccato a lettere non e' un telefono. Dentro un identificatore come
    /// <c>a6d50044-be41-4ac2-9024-3849c8dffd98</c> la v1 leggeva «2-9024-3849», nove cifre, e un ricordo che porta il suo
    /// identificatore rendeva sensibile un «ciao»: la domanda non poteva piu' uscire verso un servizio ammesso.
    /// </remarks>
    public override string Version => "phone-v2";

    /// <inheritdoc />
    public override FindingType Type => FindingType.Phone;

    /// <inheritdoc />
    public override DataClass ClassContribution => DataClass.Personal;

    /// <inheritdoc />
    protected override double Confidence => 0.6;

    /// <inheritdoc />
    protected override Regex Pattern => PhonePattern();

    /// <inheritdoc />
    protected override bool Accept(Match match)
    {
        string value = match.Value;
        int digits = value.Count(char.IsAsciiDigit);

        if (IsDottedQuad(value))
        {
            // 192.168.1.100 ha la stessa forma di un numero: un indirizzo IP non e' un contatto.
            return false;
        }

        // Con il prefisso internazionale bastano otto cifre; senza, sotto le nove si tratta quasi
        // sempre di date, importi o numeri di versione.
        int minimumDigits = value.StartsWith('+') ? 8 : 9;

        return digits >= minimumDigits && digits <= 15;
    }

    private static bool IsDottedQuad(string value)
    {
        string[] parts = value.Split('.');

        return parts.Length == 4
            && parts.All(part => part.Length is >= 1 and <= 3 && part.All(char.IsAsciiDigit));
    }

    [GeneratedRegex(
        @"(?<![0-9A-Za-z])\+?[0-9]{1,4}(?:[ .\-][0-9]{2,8}){1,4}(?![0-9A-Za-z])",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        50)]
    private static partial Regex PhonePattern();
}

/// <summary>
/// Codice fiscale italiano. Alla forma si aggiunge il carattere di controllo: senza checksum il
/// pattern accetterebbe qualunque sequenza della stessa lunghezza.
/// </summary>
public sealed partial class ItalianFiscalCodeDetector : RegexDetector
{
    private const string EvenTable = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private static readonly int[] OddValues =
    [
        1, 0, 5, 7, 9, 13, 15, 17, 19, 21, 2, 4, 18, 20, 11, 3, 6, 8, 12, 14, 16, 10, 22, 25, 24, 23,
    ];

    /// <inheritdoc />
    public override string Name => "italian-fiscal-code";

    /// <inheritdoc />
    public override string Version => "cf-v1";

    /// <inheritdoc />
    public override FindingType Type => FindingType.ItalianFiscalCode;

    /// <inheritdoc />
    public override DataClass ClassContribution => DataClass.Personal;

    /// <inheritdoc />
    protected override double Confidence => 0.95;

    /// <inheritdoc />
    protected override Regex Pattern => FiscalCodePattern();

    /// <summary>Verifica il carattere di controllo del codice fiscale.</summary>
    public static bool HasValidChecksum(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (code.Length != 16)
        {
            return false;
        }

        string upper = code.ToUpperInvariant();
        int sum = 0;

        for (int index = 0; index < 15; index++)
        {
            int value = CharacterValue(upper[index]);

            if (value < 0)
            {
                return false;
            }

            // Le posizioni sono contate da 1: gli indici pari della stringa sono posizioni dispari.
            sum += index % 2 == 0 ? OddValues[value] : value;
        }

        return EvenTable[sum % 26] == upper[15];
    }

    /// <inheritdoc />
    protected override bool Accept(Match match) => HasValidChecksum(match.Value);

    private static int CharacterValue(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'A' and <= 'Z' => character - 'A',
        _ => -1,
    };

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])[A-Za-z]{6}[0-9]{2}[ABCDEHLMPRSTabcdehlmprst][0-9]{2}[A-Za-z][0-9]{3}[A-Za-z](?![A-Za-z0-9])",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        50)]
    private static partial Regex FiscalCodePattern();
}
