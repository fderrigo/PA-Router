namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// La forma in cui Ollama riceve la permanenza di un modello in memoria (<c>keep_alive</c>): una durata. Una durata negativa
/// vuol dire «finche' Ollama e' acceso». Un punto solo per chat, embedding e analizzatore.
/// </summary>
public static class OllamaKeepAlive
{
    /// <summary>Minuti dell'operatore nella forma di Ollama.</summary>
    public static string Format(int minutes) =>
        minutes < 0 ? "-1m" : minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m";
}
