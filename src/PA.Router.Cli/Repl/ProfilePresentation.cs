using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Localization;
using PA.Router.Cli.Output;
using PA.Router.Hosting.Runtime;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Repl;

/// <summary>
/// Come si mostra un profilo (un'«etichetta», una modalita') davanti a una persona. Nessun identificatore ne' nome di
/// profilo sta nel codice (24 settembre 2026): il nome e' il campo <c>name</c> del profilo, scritto dall'operatore, e senza
/// nome si mostra l'identificatore. La descrizione e' sempre quella calcolata dalle regole (<see cref="Summary"/>).
/// </summary>
internal static class ProfilePresentation
{
    /// <summary>Nome del profilo: quello dato dall'operatore, altrimenti l'identificatore. Unica implementazione.</summary>
    internal static string Name(string id, string? name) =>
        SafeText.Inline(string.IsNullOrWhiteSpace(name) ? id : name.Trim());

    /// <summary>Nome di un profilo letto.</summary>
    internal static string Name(PolicyDocument profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Name(profile.ProfileId, profile.Name);
    }

    /// <summary>
    /// Nome di un profilo per identificatore, cercato nella configurazione: una sessione di un profilo che non esiste piu'
    /// mostra il proprio identificatore.
    /// </summary>
    internal static string Name(ConfigurationSnapshot? snapshot, string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return snapshot is not null && snapshot.Profiles.TryGetValue(id, out PolicyDocument? profile) ? Name(profile) : Name(id, null);
    }

    /// <summary>Nome e descrizione calcolata, su una riga: per gli elenchi in cui si sceglie un profilo.</summary>
    internal static string Label(ConfigurationSnapshot snapshot, string id, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Name(snapshot, id) + " - " + Summary(ProfileFacts.Describe(snapshot, id, clock));
    }

    /// <summary>
    /// Che cosa comporta l'etichetta, in una frase composta dai fatti calcolati (<see cref="ProfileFacts"/>): chi puo'
    /// rispondere e fin dove possono arrivare i dati. Stesse regole, stessa frase; nessun testo scritto per un'etichetta.
    /// </summary>
    internal static string Summary(ProfileFactsReport facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        string[] who = [.. Enum.GetValues<AnswererKind>()
            .Where(kind => facts.Answerers.GetValueOrDefault(kind))
            .Select(kind => Ui.Text("label.who." + kind.ToString().ToLowerInvariant()))];

        string answers = who.Length == 0
            ? Ui.Text("label.who.none")
            : Ui.Format("label.who {0}", string.Join(", ", who));

        string Zone(TrustZone zone) => Ui.Text("zone." + zone.ToWire());

        string data = facts.Sensitive == facts.Personal && facts.Personal == facts.Public
            ? Ui.Format("label.dataAll {0}", Zone(facts.Public))
            : facts.Sensitive == facts.Personal
                ? Ui.Format("label.data {0} {1}", Zone(facts.Personal), Zone(facts.Public))
                : Ui.Format("label.dataSensitive {0} {1} {2}", Zone(facts.Sensitive), Zone(facts.Personal), Zone(facts.Public));

        return answers + " " + data;
    }
}
