using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Commands;
using PA.Router.Hosting.Runtime;

namespace PA.Router.Cli.Repl;

/// <summary>Esito di una richiesta di conferma all'operatore.</summary>
internal enum ReplChoice
{
    /// <summary>Tutto cio' che non e' un si' esplicito. E' il valore predefinito di proposito.</summary>
    No = 0,

    /// <summary>Concesso per questa richiesta soltanto.</summary>
    Once,

    /// <summary>Concesso per il resto della sessione.</summary>
    Always,
}

/// <summary>
/// Che cosa l'operatore sta autorizzando, in parole, prima di rispondere (S60).
///
/// Fino alla S59 la conferma era una riga: «L'agente potra' modificare file in &lt;cartella&gt;.
/// Confermi? (si/no)». Diceva *che cosa* in modo generico e non diceva **entro quali limiti**: le
/// capacita' concesse, il fatto che la cartella diventi un repository git, i limiti che il piano
/// aveva gia' applicato. Chi conferma senza vedere sta firmando un foglio bianco.
///
/// L'anteprima non aggiunge dati nuovi: mostra quelli che <see cref="TurnPlan"/> aveva gia' e che
/// nessuno metteva davanti agli occhi al momento della scelta.
/// </summary>
internal static class ReplConfirmation
{
    /// <summary>Righe dell'anteprima per la prima scrittura della sessione.</summary>
    internal static IReadOnlyList<IReadOnlyList<string>> Write(TurnPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        List<IReadOnlyList<string>> rows =
        [
            [Ui.Text("cartella"), plan.Workspace?.RootPath ?? Ui.Text("nessuna")],
            [Ui.Text("effetto"), Ui.Text("l'agente potra' creare e modificare file dentro quella cartella")],
            ["git", Ui.Text("la cartella diventa un repository con un checkpoint, se non lo e' gia'")],
            [Ui.Text("capacita' concesse"), Capabilities(plan)],
            [Ui.Text("validita'"), Ui.Text("«si» vale per il resto della sessione; «una volta» solo per questa richiesta")],
        ];

        AddLimits(rows, plan);

        return rows;
    }

    /// <summary>Righe dell'anteprima per i comandi di sistema.</summary>
    internal static IReadOnlyList<IReadOnlyList<string>> Shell(TurnPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        List<IReadOnlyList<string>> rows =
        [
            [Ui.Text("cartella"), plan.Workspace?.RootPath ?? Ui.Text("nessuna")],
            [Ui.Text("effetto"), Ui.Text("l'agente potra' eseguire comandi di sistema in quella cartella")],
            [Ui.Text("quali comandi"), Ui.Text("li sceglie l'agente: il router non li conosce prima e non li limita a uno a uno")],
            [Ui.Text("rete"), Ui.Text("non fa parte della concessione")],
            [Ui.Text("capacita' concesse"), Capabilities(plan)],
            [Ui.Text("validita'"), Ui.Text("questa richiesta soltanto: i comandi si confermano ogni volta")],
        ];

        AddLimits(rows, plan);

        return rows;
    }

    /// <summary>
    /// Servizi che il profilo lascia raggiungere solo con la conferma dell'operatore (ADR-029, decisione I). Vuoto se il
    /// turno non e' stato negato per questo motivo.
    /// </summary>
    internal static IReadOnlyList<(ServiceDenial Service, DenialFact Fact)> ExternalServices(ReplTurnResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Denial is not DenialDetail denial)
        {
            return [];
        }

        string code = PolicyReason.ExternalConfirmationRequired.ToWire();

        return [.. denial.Services
            .Select(service => (Service: service, Fact: service.Facts.FirstOrDefault(fact => fact.Code == code)))
            .Where(entry => entry.Fact is not null)
            .Select(entry => (entry.Service, entry.Fact!))];
    }

    /// <summary>Righe dell'anteprima per l'uscita dal perimetro.</summary>
    internal static IReadOnlyList<IReadOnlyList<string>> External(
        IReadOnlyList<(ServiceDenial Service, DenialFact Fact)> services,
        Func<string, string> named)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(named);

        List<IReadOnlyList<string>> rows =
        [
            .. services.Select(entry => (IReadOnlyList<string>)
                [Ui.Text("servizio"), named(entry.Service.ServiceId) + " — " + Zone(entry.Fact.Observed)]),
            [Ui.Text("effetto"), Ui.Text("la richiesta e il suo contesto escono dal perimetro del tuo uso")],
            [Ui.Text("perche' chiede"), Ui.Text("lo impone il profilo per questa classe di dati: non lo decide la richiesta")],
            [Ui.Text("validita'"), Ui.Text("questa richiesta soltanto: l'uscita si conferma ogni volta")],
        ];

        return rows;
    }

    /// <summary>Nome leggibile di una zona di fiducia.</summary>
    internal static string Zone(string? wire) =>
        wire is not null && Ui.Contains("zone." + wire) ? Ui.Text("zone." + wire) : wire ?? "-";

    /// <summary>
    /// Interpreta la risposta. Tutto cio' che non e' un si' esplicito resta un no.
    ///
    /// Un «si» vale per la **sessione**, come `03-specs/CLI-TUI.md` specifica dalla S41: la prima
    /// scrittura si conferma una volta. S60 non cambia quel contratto — aggiunge «una volta», che
    /// e' piu' **restrittivo**, per chi vuole autorizzare solo la richiesta che ha davanti.
    ///
    /// Invertire i due esiti sarebbe stato piu' severo sulla carta e peggiore in pratica: chiedere
    /// a ogni scrittura porta a rispondere senza leggere, e una conferma che non si legge non
    /// protegge nessuno.
    /// </summary>
    internal static ReplChoice Read(string? answer)
    {
        string normalised = (answer ?? string.Empty).Trim().ToLowerInvariant();

        if (normalised.Length > 0 && normalised.Equals(Ui.Text("Si"), StringComparison.OrdinalIgnoreCase)) { return ReplChoice.Always; }
        if (normalised.Length > 0 && normalised.Equals(Ui.Text("una volta"), StringComparison.OrdinalIgnoreCase)) { return ReplChoice.Once; }
        return normalised switch
        {
            "una volta" or "solo questa volta" or "once" => ReplChoice.Once,
            "si" or "s" or "sì" or "yes" or "y" => ReplChoice.Always,
            _ => ReplChoice.No,
        };
    }

    /// <summary>Capacita' concesse al turno, in forma leggibile.</summary>
    private static string Capabilities(TurnPlan plan) =>
        plan.Capabilities.Count == 0
            ? Ui.Text("nessuna oltre la chat")
            : string.Join(", ", plan.Capabilities.Select(capability => capability.ToWire()).Order(StringComparer.Ordinal));

    /// <summary>
    /// I limiti che il piano ha gia' applicato. Vanno mostrati **qui**: sono la differenza fra cio'
    /// che la richiesta chiedeva e cio' che sta per ricevere.
    /// </summary>
    private static void AddLimits(List<IReadOnlyList<string>> rows, TurnPlan plan)
    {
        foreach (string limit in plan.Limits)
        {
            rows.Add([Ui.Text("limite gia' applicato"), limit]);
        }

        _ = Formats.Invariant;
    }
}
