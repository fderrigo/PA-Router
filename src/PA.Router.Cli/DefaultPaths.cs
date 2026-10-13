using System.Globalization;

namespace PA.Router.Cli;

/// <summary>
/// Percorsi predefiniti del client. La directory dati sta nel profilo dell'utente: mai una
/// cartella condivisa e mai il repository di lavoro (03-specs/STORAGE-AUDIT.md).
/// </summary>
internal static class DefaultPaths
{
    /// <summary>Directory dati predefinita dell'operatore.</summary>
    internal static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PA.Router");
}

/// <summary>Formattazione culture-invariant per i valori stampati dalla CLI.</summary>
internal static class Formats
{
    /// <summary>Cultura usata per numeri e date sull'output della CLI.</summary>
    internal static CultureInfo Invariant => CultureInfo.InvariantCulture;
}
