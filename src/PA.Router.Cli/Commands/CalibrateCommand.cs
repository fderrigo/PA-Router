using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Output;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Commands;

/// <summary>
/// <c>pa registry calibrate</c>: misura la latenza dei servizi sulla storia delle esecuzioni e,
/// solo se richiesto, la scrive nel registro.
///
/// La resa sta qui, la misura sta in <see cref="ServiceCalibration"/> (INV-22).
///
/// Non scrive mai da solo: senza <c>--apply</c> mostra cio' che scriverebbe, esattamente come
/// <c>pa config migrate</c>. Un comando che riscrive il registro di nascosto e' un comando che
/// nessuno puo' controllare.
/// </summary>
internal static class CalibrateCommand
{
    /// <summary>Suffisso della copia dell'originale.</summary>
    internal const string BackupSuffix = ".pre-calibrate.bak";

    internal static Command Create(Option<DirectoryInfo?> dataOption)
    {
        Option<int> daysOption = new("--days")
        {
            Description = Ui.Text("Days of history to measure."),
            DefaultValueFactory = _ => (int)ServiceCalibration.DefaultWindow.TotalDays,
        };

        Option<bool> applyOption = new("--apply") { Description = Ui.Text("Write the measured values into the registry.") };
        Option<bool> jsonOption = new("--json") { Description = Ui.Text("Write a single JSON document on standard output.") };

        Command command = new("calibrate", Ui.Text("Measure service latency from past executions."))
        {
            dataOption,
            daysOption,
            applyOption,
            jsonOption,
        };

        command.SetAction(parseResult => ExecuteAsync(parseResult, dataOption, daysOption, applyOption, jsonOption));

        return command;
    }

    private static async Task<int> ExecuteAsync(
        ParseResult parseResult,
        Option<DirectoryInfo?> dataOption,
        Option<int> daysOption,
        Option<bool> applyOption,
        Option<bool> jsonOption)
    {
        bool json = parseResult.GetValue(jsonOption);
        bool apply = parseResult.GetValue(applyOption);
        TextWriter output = parseResult.InvocationConfiguration.Output;
        TextWriter error = parseResult.InvocationConfiguration.Error;

        int days = parseResult.GetValue(daysOption);

        if (days <= 0)
        {
            error.WriteLine(Ui.Text("La finestra deve essere di almeno un giorno."));
            return ExitCodes.InvalidInput;
        }

        string dataDirectory = CommandContext.ResolveDataDirectory(parseResult, dataOption);
        ConfigurationLoadResult configuration = new ConfigurationLoader(SystemClock.Instance).Load(dataDirectory);

        if (!configuration.IsValid)
        {
            return CommandContext.ReportIssues(parseResult, configuration.Issues);
        }

        ConfigurationSnapshot snapshot = configuration.Require();

        CalibrationReport report;

        try
        {
            // Sola lettura: la misura guarda la storia e non tocca nulla, quindi convive con un
            // worker vivo.
            await using RouterStack runtime = RouterStack.Open(snapshot, ownsStore: false);

            report = ServiceCalibration.Measure(
                runtime,
                TimeSpan.FromDays(days),
                SystemClock.Instance.UtcNow);
        }
        catch (PaRouterException exception)
        {
            error.WriteLine(Ui.Text("Misura non eseguita: ") + exception.Code.ToWire());
            return ExitCodes.ForStorage(exception.Code);
        }

        int failure = ExitCodes.Success;
        IReadOnlyList<string> written = apply ? Write(snapshot, report, error, out failure) : [];

        if (apply && failure != ExitCodes.Success)
        {
            return failure;
        }

        return json
            ? WriteJson(output, report, apply, written)
            : WriteText(output, error, report, apply, written, snapshot);
    }

    /// <summary>
    /// Scrive le misure nel registro, lasciando accanto l'originale. Dopo la scrittura la
    /// configurazione viene riletta: un registro che non si carica piu' va detto subito, non al
    /// turno successivo.
    /// </summary>
    private static IReadOnlyList<string> Write(
        ConfigurationSnapshot snapshot,
        CalibrationReport report,
        TextWriter error,
        out int failure)
    {
        failure = ExitCodes.Success;

        IReadOnlyList<string> updated = [];

        RegistryRewriteResult result = RegistryRewrite.Apply(
            snapshot,
            BackupSuffix,
            registry =>
            {
                updated = ServiceCalibration.Apply(registry, report, SystemClock.Instance.UtcNow);
                return updated.Count > 0;
            },
            SystemClock.Instance);

        switch (result.Status)
        {
            case RegistryRewriteStatus.Written:
                return updated;

            case RegistryRewriteStatus.Unchanged:
                return [];

            case RegistryRewriteStatus.Missing:
                error.WriteLine(Ui.Text("Il registro non esiste: ") + result.FileName);
                break;

            case RegistryRewriteStatus.Unreadable:
                error.WriteLine(Ui.Text("Il registro non e' leggibile: ") + result.FileName);
                break;

            default:
                // Un registro che non si carica non resta al suo posto: il router deve ripartire.
                error.WriteLine(Ui.Text("Il registro scritto non si caricava: l'originale e' stato rimesso al suo posto."));
                break;
        }

        failure = ExitCodes.InvalidInput;

        return [];
    }

    private static int WriteJson(TextWriter output, CalibrationReport report, bool apply, IReadOnlyList<string> written)
    {
        JsonArray measurements = [];

        foreach (ServiceMeasurement measurement in report.Measurements)
        {
            measurements.Add(new JsonObject
            {
                ["serviceId"] = measurement.ServiceId,
                ["samples"] = measurement.Samples,
                ["medianMs"] = measurement.MedianMs,
                ["p95Ms"] = measurement.P95Ms,
                ["latencyScore"] = measurement.LatencyScore,
            });
        }

        CliJson.Write(output, CliJson.Ok("registry calibrate", new JsonObject
        {
            ["windowDays"] = (int)report.Window.TotalDays,
            ["minimumSamples"] = report.MinimumSamples,
            ["measurements"] = measurements,
            ["applied"] = apply,
            ["written"] = new JsonArray([.. written.Select(id => (JsonNode)id)]),
            // Nessun numero di qualita' esce da qui: dichiararlo evita che un lettore automatico
            // creda che il comando abbia calibrato tutte e quattro le dimensioni.
            ["qualityMeasured"] = false,
        }));

        return ExitCodes.Success;
    }

    private static string Seconds(long milliseconds) =>
        (milliseconds / 1000.0).ToString(milliseconds >= 10000 ? "0" : "0.0", CultureInfo.InvariantCulture) + " s";

    private static int WriteText(
        TextWriter output,
        TextWriter error,
        CalibrationReport report,
        bool apply,
        IReadOnlyList<string> written,
        ConfigurationSnapshot snapshot)
    {
        // Nomi, non identificatori, e la velocita' in decimi come nel racconto del turno (24 settembre 2026). Un servizio
        // che non c'e' piu' nel registro resta con il suo identificatore: il nome non esiste.
        string Named(string id) => Repl.ServiceNames.Display(snapshot, id);

        output.WriteLine(
            Ui.Text("Finestra: ") + ((int)report.Window.TotalDays).ToString(Formats.Invariant)
            + Ui.Text(" giorni. Campioni minimi: ") + report.MinimumSamples.ToString(Formats.Invariant) + ".");

        output.WriteLine();

        // Le colonne le calcola la superficie dai dati. Prima erano quattro `PadRight` con numeri
        // scritti a mano e un'intestazione letterale mantenuta a parte: un identificatore di
        // servizio piu' lungo di ventisei caratteri sfondava la colonna in silenzio.
        IOutputSurface surface = Surfaces.For(output, error, Surfaces.CanDecorate);

        if (report.Measurements.Count == 0)
        {
            surface.Line(Ui.Text("  nessun tentativo completato nella finestra."));
        }
        else
        {
            // Un elenco, non una tabella: i nomi dei servizi sono lunghi e la colonna li troncava fino a renderli uguali.
            foreach (ServiceMeasurement measurement in report.Measurements)
            {
                output.WriteLine("  " + Named(measurement.ServiceId));
                output.WriteLine("      " + Ui.Format("calibrate.row {0} {1} {2} {3}",
                    measurement.Samples.ToString(Formats.Invariant),
                    Seconds(measurement.MedianMs),
                    Seconds(measurement.P95Ms),
                    measurement.LatencyScore is double score
                        ? Ui.Format("calibrate.speedValue {0}", (score * 10).ToString("0.#", CultureInfo.InvariantCulture))
                        : Ui.Text("campioni insufficienti")));
            }
        }

        output.WriteLine();

        // Il punto che un operatore deve leggere prima di fidarsi della classifica.
        // La qualita' e' l'ordine dei modelli dell'operatore (ADR-032, fase 3): qui non si misura e non si scrive.
        output.WriteLine(Ui.Text("calibrate.quality"));

        if (!apply)
        {
            output.WriteLine();
            output.WriteLine(Ui.Text("Nulla e' stato scritto. Per applicare: pa registry calibrate --apply"));

            return ExitCodes.Success;
        }

        output.WriteLine();

        if (written.Count == 0)
        {
            error.WriteLine(Ui.Text("Nessun servizio aveva campioni sufficienti: il registro non e' stato toccato."));
            return ExitCodes.Success;
        }

        output.WriteLine(Ui.Text("Latenza scritta per: ") + string.Join(", ", written.Select(Named)));
        output.WriteLine(Ui.Text("Originale conservato con suffisso ") + BackupSuffix + ".");

        return ExitCodes.Success;
    }
}
