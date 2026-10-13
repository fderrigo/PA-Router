using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Localization;
using PA.Router.Cli.Repl;
using PA.Router.Hosting.Runtime;

namespace PA.Router.Cli.Output;

/// <summary>
/// Zone e gradazione di un profilo in parole (ADR-029), per <c>pa doctor</c> e <c>/doctor</c>: la diagnosi le calcola,
/// qui si scrivono. Zone e classi con il loro nome, mai con il codice.
/// </summary>
internal static class ZoneText
{
    /// <summary>Riga delle zone per classe.</summary>
    internal static string Limits(ProfileZonesDiagnosis profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return Ui.Format(
            "doctor.profile.zones {0} {1}",
            profile.ProfileId,
            string.Join("; ", PolicyZones.DeclaredClasses.Select(dataClass =>
                Class(dataClass) + " → " + ReplConfirmation.Zone(profile.MaxZone[dataClass].ToWire()))));
    }

    /// <summary>Riga di perimetro, conferme, allegati e gradazione.</summary>
    internal static string Rules(ProfileZonesDiagnosis profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return Ui.Format(
            "doctor.profile.rules {0} {1} {2} {3}",
            string.Join(", ", profile.Perimeter.Select(zone => ReplConfirmation.Zone(zone.ToWire()))),
            profile.ConfirmOutside.Count == 0 ? Ui.Text("doctor.profile.none") : string.Join(", ", profile.ConfirmOutside.Select(Class)),
            Ui.Text(profile.AttachmentsOutsidePerimeter ? "doctor.profile.attachmentsOut" : "doctor.profile.attachmentsIn"),
            Ui.Text("doctor.profile.grading." + profile.Grading.ToWire()));
    }

    /// <summary>Fin dove puo' arrivare ogni classe di dati, senza il nome del profilo davanti.</summary>
    internal static string Classes(ProfileZonesDiagnosis profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return string.Join("; ", PolicyZones.DeclaredClasses.Select(dataClass =>
            Class(dataClass) + " → " + ReplConfirmation.Zone(profile.MaxZone[dataClass].ToWire())));
    }

    private static string Class(DataClass dataClass) =>
        Ui.Contains("class." + dataClass.ToWire()) ? Ui.Text("class." + dataClass.ToWire()) : dataClass.ToWire();
}
