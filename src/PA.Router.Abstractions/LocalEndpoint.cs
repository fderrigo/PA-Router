namespace PA.Router.Abstractions;

/// <summary>
/// Vincoli sull'endpoint di un servizio di modello locale.
///
/// Ammessi solo <c>127.0.0.1</c> e <c>::1</c> in forma numerica: un nome, anche
/// <c>localhost</c>, passa da una risoluzione che il router non controlla e che un file hosts o
/// un resolver possono spostare altrove. Nessun proxy e nessun redirect: entrambi trasformerebbero
/// una inferenza dichiarata locale in una chiamata verso un altro host senza che nulla lo dica
///
/// Sta nelle astrazioni perche' la regola e' una sola per tutti i trasporti locali — chat ed
/// embedding: duplicarla significherebbe poterla allentare in un posto solo
/// (03-specs/ADAPTERS.md).
/// </summary>
public static class LocalEndpoint
{
    /// <summary>Host numerici ammessi.</summary>
    public static IReadOnlySet<string> AllowedHosts { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "127.0.0.1", "[::1]", "::1" };

    /// <summary>Vero se l'endpoint rispetta il vincolo locale.</summary>
    public static bool IsAcceptable(Uri? endpoint) =>
        endpoint is not null
        && endpoint.IsAbsoluteUri
        && string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
        && AllowedHosts.Contains(endpoint.Host)
        && endpoint.AbsolutePath is "/" or ""
        && string.IsNullOrEmpty(endpoint.Query);

    /// <summary>Verifica l'endpoint o lancia un errore tipizzato.</summary>
    public static Uri Require(Uri? endpoint)
    {
        if (IsAcceptable(endpoint))
        {
            return endpoint!;
        }

        throw new PaRouterException(
            ErrorCode.ControlUnavailable,
            "A local model endpoint must be a numeric loopback origin without path or query.")
            .WithMetadata("reason", "endpoint_not_local");
    }

    /// <summary>
    /// Handler HTTP per il percorso locale: nessun proxy, nessun redirect seguito, nessuna
    /// riscrittura automatica.
    /// </summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        UseProxy = false,
        Proxy = null,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    };
}

/// <summary>
/// Vincoli sul nome del modello locale.
///
/// Ollama puo' essere configurato con modelli ospitati: compaiono nell'elenco locale come
/// qualunque altro, ma l'inferenza avviene altrove. Il nome e' l'unico segnale che il router
/// osserva davvero, quindi un alias cloud viene rifiutato prima di inviare il payload.
///
/// E' un riconoscimento per convenzione di nome, non una prova: se un'installazione instrada
/// altrove un modello dal nome ordinario, questo controllo non se ne accorge. Per questo
/// <c>local_inference</c> richiede anche l'attestazione dell'operatore e, per un profilo che lo esige, la
/// prova di egress bloccato a livello host (03-specs/ADAPTERS.md).
/// </summary>
public static class LocalModel
{
    /// <summary>Suffissi che nell'ecosistema Ollama indicano un modello ospitato.</summary>
    public static IReadOnlyList<string> CloudSuffixes { get; } = ["-cloud", ":cloud"];

    /// <summary>Vero se il nome dichiara un modello ospitato.</summary>
    public static bool IsCloudAlias(string? model) =>
        model is not null
        && CloudSuffixes.Any(suffix => model.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
