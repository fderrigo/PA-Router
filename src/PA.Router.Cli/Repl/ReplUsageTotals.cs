using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Commands;

namespace PA.Router.Cli.Repl;

/// <summary>
/// Consumo della sessione, sommato da cio' che i servizi **dichiarano** (S59).
///
/// Il router non misura token e non stima costi a consuntivo: somma quello che arriva negli eventi
/// del turno, e dichiara quando un servizio non ha dichiarato nulla. La differenza conta: un totale
/// inventato sarebbe peggio di un totale assente, perche' sembrerebbe una misura.
///
/// Il costo **stimato prima** dell'invio resta un'altra cosa, e si vede con <c>/explain</c>: quello
/// e' un preventivo sul prezzo del registro, questo e' cio' che il servizio ha detto di aver speso.
/// </summary>
internal sealed class ReplUsageTotals
{
    /// <summary>Turni che hanno dichiarato almeno un valore.</summary>
    internal int Declared { get; private set; }

    /// <summary>Turni conclusi senza alcuna dichiarazione di consumo.</summary>
    internal int Undeclared { get; private set; }

    /// <summary>Token di ingresso dichiarati.</summary>
    internal long InputTokens { get; private set; }

    /// <summary>Token di uscita dichiarati.</summary>
    internal long OutputTokens { get; private set; }

    /// <summary>Costo dichiarato dai servizi, nella valuta che dichiarano loro.</summary>
    internal decimal BilledCost { get; private set; }

    /// <summary>Costo calcolato dal router con il prezzo del registro, per valuta (23 settembre 2026).</summary>
    private readonly SortedDictionary<string, decimal> priced = new(StringComparer.Ordinal);

    /// <summary>Somma un consumo dichiarato.</summary>
    internal void Add(UsageReport usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        if (usage.InputTokens is null && usage.OutputTokens is null && usage.BilledCost is null)
        {
            Undeclared++;
            return;
        }

        Declared++;
        InputTokens += usage.InputTokens ?? 0;
        OutputTokens += usage.OutputTokens ?? 0;
        BilledCost += usage.BilledCost ?? 0;

        if (usage.PricedCost is decimal cost && usage.Currency is string currency)
        {
            priced[currency] = priced.GetValueOrDefault(currency) + cost;
        }
    }

    /// <summary>Un turno concluso senza alcun evento di consumo: il servizio non ha dichiarato.</summary>
    internal void NothingDeclared() => Undeclared++;

    /// <summary>Righe da mostrare, gia' pronte per la resa.</summary>
    internal IReadOnlyList<IReadOnlyList<string>> Rows() =>
    [
        [Ui.Text("token in ingresso"), InputTokens.ToString(Formats.Invariant)],
        [Ui.Text("token in uscita"), OutputTokens.ToString(Formats.Invariant)],
        [
            Ui.Text("costo dichiarato"),
            BilledCost == 0
                ? Ui.Text("nessun servizio lo ha dichiarato")
                : BilledCost.ToString("0.####", Formats.Invariant)
        ],
        [
            Ui.Text("usage.pricedRow"),
            priced.Count == 0
                ? Ui.Text("usage.pricedNone")
                : string.Join(" + ", priced.Select(entry => entry.Value.ToString("0.######", Formats.Invariant) + " " + entry.Key))
        ],
        [Ui.Text("turni con consumo dichiarato"), Declared.ToString(Formats.Invariant)],
        [Ui.Text("turni senza dichiarazione"), Undeclared.ToString(Formats.Invariant)],
    ];
}
