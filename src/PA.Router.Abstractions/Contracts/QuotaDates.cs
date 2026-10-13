using System.Globalization;
using System.Text.RegularExpressions;

namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// La data in cui un fornitore dice che il servizio torna disponibile, letta dal messaggio d'errore (24 settembre 2026).
/// Anthropic, al limite di spesa: «You will regain access on 2026-10-01 at 00:00 UTC.». Solo la forma osservata; un
/// messaggio diverso non da' una data, e la pausa resta finche' l'operatore non riattiva il servizio.
/// </summary>
public static partial class QuotaDates
{
    /// <summary>Istante indicato dal messaggio, in UTC; null se il messaggio non ne indica uno.</summary>
    public static DateTimeOffset? RegainAccess(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        Match match = RegainPattern().Match(message);

        if (!match.Success
            || !DateTime.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
        {
            return null;
        }

        TimeSpan time = TimeSpan.Zero;

        if (match.Groups["time"].Success
            && !TimeSpan.TryParseExact(match.Groups["time"].Value, "hh\\:mm", CultureInfo.InvariantCulture, out time))
        {
            return null;
        }

        return new DateTimeOffset(day.Add(time), TimeSpan.Zero);
    }

    [GeneratedRegex(@"regain access on (?<date>\d{4}-\d{2}-\d{2})(?: at (?<time>\d{2}:\d{2}) UTC)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex RegainPattern();
}
