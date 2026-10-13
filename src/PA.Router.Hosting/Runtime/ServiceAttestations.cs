using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Hosting.Runtime;

/// <summary>Dichiarazioni dell'operatore condivise da setup e CLI.</summary>
public static class ServiceAttestations
{
    /// <summary>
    /// Valida una conferma: chi l'ha data e quando, non nel futuro. Dal 25 settembre 2026 (ADR-032, fase 4) una conferma
    /// non ha scadenza: vale finche' l'operatore non la toglie, e ogni anno si ricorda di ricontrollarla.
    /// </summary>
    public static void Validate(PropertyAttestation attestation, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        ArgumentNullException.ThrowIfNull(clock);
        if (string.IsNullOrWhiteSpace(attestation.By)
            || attestation.By.Contains(ServiceDescriptor.PlaceholderPrefix, StringComparison.Ordinal)
            || attestation.ObservedAt is not DateTimeOffset observed || observed > clock.UtcNow)
        {
            throw new ArgumentException("Servono chi ha verificato e una data non futura.", nameof(attestation));
        }
    }

    /// <summary>
    /// Conferme valide date da almeno un anno (<see cref="PropertyAttestation.ReminderDue"/>), sui soli servizi abilitati:
    /// <c>doctor</c> e l'avvio del REPL chiedono di ricontrollarle. Un promemoria, non un rifiuto: la conferma continua a
    /// valere.
    /// </summary>
    public static IReadOnlyList<ConfirmationReminder> Reminders(IEnumerable<ServiceDescriptor> services, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(services);
        List<ConfirmationReminder> result = [];
        foreach (ServiceDescriptor service in services.Where(item => item.Enabled))
        {
            foreach ((Control control, PropertyAttestation attestation) in service.Attestations)
            {
                if (attestation.ReminderDue(now) && attestation.ObservedAt is DateTimeOffset observed)
                {
                    result.Add(new ConfirmationReminder(service.Id, control.ToWire(), observed));
                }
            }
        }
        return [.. result.OrderBy(item => item.ConfirmedAt).ThenBy(item => item.ServiceId, StringComparer.Ordinal)];
    }

    /// <summary>Scrive la dichiarazione nel documento di lavoro, mai abilita il servizio.</summary>
    public static bool Apply(JsonObject registry, string serviceId, Control control, PropertyAttestation attestation)
    {
        JsonObject service = registry["services"]!.AsArray().OfType<JsonObject>()
            .Single(item => item["id"]!.GetValue<string>() == serviceId);
        JsonArray controls = service["controls"]?.AsArray() ?? new JsonArray();
        if (service["controls"] is null) { service["controls"] = controls; }
        if (!controls.Any(value => value!.GetValue<string>() == control.ToWire())) { controls.Add(control.ToWire()); }
        JsonObject attestations = service["attestations"]?.AsObject() ?? new JsonObject();
        if (service["attestations"] is null) { service["attestations"] = attestations; }
        attestations[control.ToWire()] = ToJson(attestation);
        return true;
    }

    /// <summary>
    /// Toglie la conferma dell'operatore su un controllo, lasciando in piedi la dichiarazione del servizio: il
    /// controllo torna da confermare, non sparisce. Revocare non e' ritirare cio' che il servizio dichiara di
    /// fare — quello resta un fatto del servizio, e un'attestazione revocata semplicemente non lo copre piu'.
    /// </summary>
    public static bool Revoke(JsonObject registry, string serviceId, Control control)
    {
        ArgumentNullException.ThrowIfNull(registry);
        JsonObject service = registry["services"]!.AsArray().OfType<JsonObject>()
            .Single(item => item["id"]!.GetValue<string>() == serviceId);
        if (service["attestations"]?.AsObject() is not JsonObject attestations) { return false; }
        bool removed = attestations.Remove(control.ToWire());
        if (attestations.Count == 0) { service.Remove("attestations"); }
        return removed;
    }

    /// <summary>Forma del contratto esistente.</summary>
    public static JsonObject ToJson(PropertyAttestation attestation)
    {
        ArgumentNullException.ThrowIfNull(attestation);

        JsonObject entry = new()
        {
            ["by"] = attestation.By,
            ["observedAt"] = attestation.ObservedAt,
        };

        // Una data di scadenza non decide piu' nulla (ADR-032, fase 4): si scrive solo se c'era.
        if (attestation.ExpiresAt is DateTimeOffset expiry)
        {
            entry["expiresAt"] = expiry;
        }

        entry["note"] = attestation.Note;
        return entry;
    }
}

/// <summary>Una conferma dell'operatore da ricontrollare: servizio, controllo e data della conferma. Nessun contenuto.</summary>
public sealed record ConfirmationReminder(string ServiceId, string Control, DateTimeOffset ConfirmedAt);
