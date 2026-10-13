using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace PA.Router.Infrastructure.Storage;

/// <summary>
/// Uno script di migrazione con il suo checksum. Il testo e' quello normativo di
/// 06-contracts/storage, incluso come risorsa: non esiste una seconda copia da tenere allineata.
/// </summary>
/// <param name="Version">Numero di versione, crescente e forward-only.</param>
/// <param name="ResourceName">Nome del file di origine, per le diagnosi.</param>
/// <param name="Pragmas">PRAGMA iniziali, da eseguire fuori transazione.</param>
/// <param name="Body">DDL e DML da eseguire dentro la transazione.</param>
/// <param name="Checksum">SHA-256 esadecimale del testo normalizzato.</param>
public sealed record MigrationScript(
    int Version,
    string ResourceName,
    string Pragmas,
    string Body,
    string Checksum)
{
    private const string PragmaKeyword = "PRAGMA ";

    /// <summary>Migrazioni note a questa build, in ordine di applicazione.</summary>
    public static IReadOnlyList<MigrationScript> All { get; } =
    [
        Load(1, "001_initial.sql"),
        Load(2, "002_memory_extensions.sql"),
        Load(3, "003_memory_routing.sql"),
        Load(4, "004_memory_demotion.sql"),
        Load(5, "005_embedding_dimension.sql"),
        Load(6, "006_tool_attestations.sql"),
        Load(7, "007_tool_calling_state.sql"),
        Load(8, "008_session_names.sql"),
        Load(9, "009_denial_decisions.sql"),
        Load(10, "010_grants_without_permissions.sql"),
    ];

    /// <summary>Versione di schema attesa da questa build.</summary>
    public static int LatestVersion => All[^1].Version;

    /// <summary>SHA-256 esadecimale minuscolo del testo normalizzato.</summary>
    public static string ComputeChecksum(string normalizedText)
    {
        ArgumentNullException.ThrowIfNull(normalizedText);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText)));
    }

    private static MigrationScript Load(int version, string fileName)
    {
        string resourceName = typeof(MigrationScript).Namespace + ".Migrations." + fileName;
        Assembly assembly = typeof(MigrationScript).Assembly;

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Missing embedded migration: " + resourceName);
        using StreamReader reader = new(stream, Encoding.UTF8);

        // Normalizzato prima dell'hash: il checksum non deve cambiare per i soli fine riga.
        string text = reader.ReadToEnd().ReplaceLineEndings("\n");
        (string pragmas, string body) = SplitLeadingPragmas(text);

        return new MigrationScript(version, fileName, pragmas, body, ComputeChecksum(text));
    }

    /// <summary>
    /// Separa i PRAGMA iniziali dal resto. Solo i PRAGMA in testa vengono estratti: un PRAGMA
    /// in mezzo al DDL resterebbe nel corpo e fallirebbe in transazione, che e' il segnale
    /// corretto di uno script non conforme alla specifica.
    /// </summary>
    private static (string Pragmas, string Body) SplitLeadingPragmas(string text)
    {
        StringBuilder pragmas = new();
        string[] lines = text.Split('\n');
        int index = 0;

        while (index < lines.Length)
        {
            string line = lines[index].Trim();

            if (line.Length == 0)
            {
                index++;
                continue;
            }

            if (!line.StartsWith(PragmaKeyword, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            pragmas.Append(line).Append('\n');
            index++;
        }

        return (pragmas.ToString(), string.Join('\n', lines.Skip(index)));
    }
}
