using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Localization;
using PA.Router.Cli.Repl;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Output;

/// <summary>
/// I servizi del router scritti per una persona, una resa sola per `pa registry list`, `/services` e `/models` (23 settembre
/// 2026). Prima `pa registry list` elencava identificatori e parole del registro («request-analysis», «third_party»,
/// «latenza declared»), `/services` era una tabella troncata e `/models` giudicava con l'etichetta predefinita anche in una
/// conversazione aperta con un'altra. Qui: raggruppati per ruolo, con il nome che si legge nel REPL, acceso o spento, dove
/// lavora, costo, posto nell'ordine dei modelli; con un'etichetta, anche se questa lo ammette. Il formato per i programmi e' `--json`.
/// </summary>
internal static class RegistryText
{
    /// <param name="snapshot">Configurazione.</param>
    /// <param name="line">Dove scrivere una riga.</param>
    /// <param name="profileId">Etichetta della conversazione, per dire chi ammette; null fuori da una conversazione.</param>
    internal static void Write(ConfigurationSnapshot snapshot, Action<string> line, string? profileId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(line);

        // Una vista solo per la valutazione: l'etichetta della conversazione al posto della predefinita.
        ConfigurationSnapshot? view = profileId is not null && snapshot.Profiles.ContainsKey(profileId)
            ? snapshot with { Config = snapshot.Config with { DefaultProfileId = profileId } }
            : null;

        line(Ui.Format("list.title {0}", snapshot.Registry.Revision));

        // In pausa per quota finita: acceso, ma il router non lo usa fino alla data o finche' non lo riattivi (24 settembre 2026).
        Func<string, string?> paused = QuotaText.StateOf(snapshot.DataDirectory);

        (string Key, Func<ServiceDescriptor, bool> Belongs)[] groups =
        [
            ("list.group.chat", service => service.Capability is ServiceCapability.Chat),
            ("list.group.agents", service => service.Capability is ServiceCapability.CodeAgent),
            ("list.group.helpers", service => service.Capability is not (ServiceCapability.Chat or ServiceCapability.CodeAgent)),
        ];

        IReadOnlyDictionary<string, OrderPosition> order = ServiceOrder.Positions(snapshot);

        foreach ((string key, Func<ServiceDescriptor, bool> belongs) in groups)
        {
            // Accesi prima, poi in ordine di nome: si cerca un nome, non una posizione nel file.
            ServiceDescriptor[] members = [.. snapshot.Registry.Services
                .Where(belongs)
                .OrderByDescending(service => service.Enabled)
                .ThenBy(service => ServiceNames.Display(service), StringComparer.CurrentCultureIgnoreCase)];

            if (members.Length == 0) { continue; }

            line(string.Empty);
            line(Ui.Text(key));

            foreach (ServiceDescriptor service in members)
            {
                List<string> facts =
                [
                    service.Enabled ? Ui.Text("list.on") : Ui.Text("list.off"),
                    Ui.Contains("list.where." + service.InferencePlacement.ToWire())
                        ? Ui.Text("list.where." + service.InferencePlacement.ToWire())
                        : service.InferencePlacement.ToWire(),
                    service.Pricing is not null ? PricingText.Describe(service)
                        : service.MarginalPrice is null ? Ui.Text("list.price.unknown") : Ui.Text("list.price.free"),
                ];

                // La qualita' e' il posto nell'ordine dei modelli (ADR-032, fase 3): solo per chi risponde, e conta fra i
                // servizi accesi.
                if (ServiceSetupCatalog.AnswersRequests(service.Capability))
                {
                    facts.Add(OrderText.Describe(order.TryGetValue(service.Id, out OrderPosition place) ? place : null));
                }

                if (service.Enabled && paused(service.Id) is string state) { facts.Add(state); }
                if (service.HasPlaceholders) { facts.Add(Ui.Text("list.incomplete")); }

                // In pausa non si dice «ammesso»: l'etichetta lo ammetterebbe, ma adesso il router non lo usa.
                if (view is not null && service.Enabled && paused(service.Id) is null && ServiceSetupCatalog.AnswersRequests(service.Capability))
                {
                    facts.Add(ServiceSetupCatalog.Compatibility(view, service, SystemClock.Instance).Count == 0
                        ? Ui.Text("list.admitted")
                        : Ui.Text("list.excluded"));
                }

                line("  " + (service.Enabled ? "● " : "○ ") + ServiceNames.Display(service));
                line("      " + string.Join(" · ", facts));
            }
        }

        line(string.Empty);
        line(Ui.Text("list.hint"));
    }
}
