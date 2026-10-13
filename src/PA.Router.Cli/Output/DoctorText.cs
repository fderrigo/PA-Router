using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Localization;
using PA.Router.Cli.Repl;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Infrastructure.Storage;
using PA.Router.Registry.Probing;

namespace PA.Router.Cli.Output;

/// <summary>
/// La diagnosi scritta per una persona (23 settembre 2026). Prima era una colonna di etichette tecniche — «Protector chiavi:
/// dpapi-current-user», «Classificatore: rules+embedding (embedding-locale)», «healthy processo: loopback» — e i servizi
/// si chiamavano per identificatore. Qui: prima se il router e' pronto, poi i servizi con il loro nome, come sceglie, dove
/// vanno i dati, la memoria, e per ultimo che cosa sistemare, ognuno con dove si sistema. Il documento per i programmi resta
/// <c>pa doctor --json</c>; il calcolo resta <see cref="LocalDiagnosis"/>.
/// </summary>
internal static class DoctorText
{
    private const string Indent = "  ";

    internal static void Write(DiagnosisReport report, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);

        List<string> todo = [];

        ServiceDiagnosis[] answering = [.. report.Services.Where(service => service.Service is { } descriptor && ServiceSetupCatalog.AnswersRequests(descriptor.Capability))];
        ServiceDiagnosis[] helpers = [.. report.Services.Where(service => service.Service is not { } descriptor || !ServiceSetupCatalog.AnswersRequests(descriptor.Capability))];
        // Un servizio in pausa per quota finita risponde alla sonda ma il router non lo usa (24 settembre 2026): non conta fra
        // quelli che funzionano, e compare fra le cose da sapere con la data e il modo di riattivarlo.
        IReadOnlyList<QuotaPause> pauses = QuotaPauses.Active(StorePaths.For(report.DataDirectory), SystemClock.Instance.UtcNow);
        bool Paused(ServiceDiagnosis service) => pauses.Any(pause => string.Equals(pause.ServiceId, service.Id, StringComparison.Ordinal));
        int working = answering.Count(service => service.Probe.Health is ProbeHealth.Healthy && !Paused(service));

        // «Nessun modello funziona» e subito sotto «Da sistemare: niente» (24 settembre 2026, prova da utente nuovo): la prima
        // cosa da fare deve stare nella lista delle cose da fare, con il comando che la risolve.
        if (report.ConfigurationValid && working == 0)
        {
            todo.Add(Ui.Text(answering.Any(service => service.Service is { Enabled: true }) ? "doctor2.todo.noneWorking" : "doctor2.todo.noService"));
        }

        foreach (QuotaPause pause in pauses)
        {
            ServiceDiagnosis? paused = report.Services.FirstOrDefault(service => string.Equals(service.Id, pause.ServiceId, StringComparison.Ordinal));
            todo.Add(QuotaText.Paused(paused?.Service is ServiceDescriptor descriptor ? ServiceNames.Display(descriptor) : pause.ServiceId, pause));
        }

        // Prima di tutto, la risposta alla domanda che fa chi apre la diagnosi.
        output.WriteLine(Ui.Text("doctor2.title"));
        output.WriteLine(Indent + (!report.ConfigurationValid ? Ui.Text("doctor2.ready.invalid")
            : working == 0 ? Ui.Text("doctor2.ready.none")
            : working == 1 ? Ui.Text("doctor2.ready.one")
            : Ui.Format("doctor2.ready.many {0}", working)));

        output.WriteLine();
        output.WriteLine(Ui.Text("doctor2.sec.safety"));
        output.WriteLine(Indent + Ui.Format("doctor2.data {0}", report.DataDirectory));
        output.WriteLine(Indent + (report.KeyProtector is string protector
            ? Ui.Contains("doctor2.protector." + protector) ? Ui.Text("doctor2.protector." + protector) : Ui.Format("doctor2.protector.other {0}", protector)
            : Ui.Text("doctor2.protector.none")));
        output.WriteLine(Indent + (report.ProcessContainmentSupported ? Ui.Text("doctor2.containment.on") : Ui.Text("doctor2.containment.off")));

        if (report.KeyProtector is null) { todo.Add(Ui.Text("doctor2.todo.protector")); }

        Services(output, Ui.Text("doctor2.sec.answering"), answering, QuotaText.StateOf(report.DataDirectory));
        Services(output, Ui.Text("doctor2.sec.helpers"), helpers);

        if (report.PiiModel is PiiModelDiagnosis pii && !pii.Installed)
        {
            todo.Add(Ui.Format("doctor2.todo.pii {0}", Path.Combine(StorePaths.For(report.DataDirectory).ModelsDirectory, pii.Model ?? "<nome>")));
        }

        foreach (ServiceDiagnosis service in report.Services.Where(service => service.Probe.FailureCode is "model_not_installed"
            && service.Service?.Adapter is not AdapterKind.OnnxLocal))
        {
            string model = service.Service?.Model ?? service.Id;
            todo.Add(Ui.Format("doctor2.todo.ollamaPull {0} {1}", model, model));
        }

        output.WriteLine();
        output.WriteLine(Ui.Text("doctor2.sec.choice"));
        output.WriteLine(Indent + Analyzer(report.Analyzer));
        output.WriteLine(Indent + (report.MaxCostPerRequest is double cap
            ? Ui.Format("doctor2.costCap {0}", cap.ToString("0.######", Formats.Invariant) + (report.Currency is string currency ? " " + currency : string.Empty))
            : Ui.Text("doctor2.costCap.none")));

        if (report.OllamaProposal is OllamaMemoryProposal proposal)
        {
            output.WriteLine(Indent + (report.OllamaMaxLoaded is int current
                ? Ui.Format("doctor2.ollama.current {0} {1}", current, proposal.MaxLoadedModels)
                : Ui.Format("doctor2.ollama.default {0}", proposal.MaxLoadedModels)));

            if (report.OllamaMaxLoaded != proposal.MaxLoadedModels) { todo.Add(Ui.Format("doctor2.todo.ollama {0}", proposal.MaxLoadedModels)); }
        }

        if (report.Profiles.Count > 0)
        {
            output.WriteLine();
            output.WriteLine(Ui.Text("doctor2.sec.zones"));

            foreach (ProfileZonesDiagnosis profile in report.Profiles)
            {
                output.WriteLine(Indent + Ui.Format("doctor2.zones {0} {1}", ProfilePresentation.Name(profile.ProfileId, profile.ProfileName), ZoneText.Classes(profile)));
                output.WriteLine(Indent + Indent + ZoneText.Rules(profile));
            }
        }

        output.WriteLine();
        output.WriteLine(Ui.Text("doctor2.sec.memory"));
        output.WriteLine(Indent + (!report.MemoryConfigured ? Ui.Text("doctor2.memory.off")
            : report.MemoryLexicalOnly ? Ui.Text("doctor2.memory.words")
            : Ui.Text("doctor2.memory.on")));
        if (report.MemoryNeverRecords) { todo.Add(Ui.Text("doctor2.todo.neverRecords")); }
        if (report.MemorySessionScopedOnly) { output.WriteLine(Indent + Ui.Text("doctor2.memory.sessionOnly")); }
        if (report.EmbeddingMismatch) { todo.Add(Ui.Format("doctor2.todo.reindex {0}", report.MemoryJobsFailed)); }
        else if (report.MemoryJobsFailed > 0) { todo.Add(Ui.Format("doctor2.todo.jobsFailed {0}", report.MemoryJobsFailed)); }

        if (report.Tools is ToolDiagnosis tools)
        {
            output.WriteLine();
            output.WriteLine(Ui.Text("doctor2.sec.tools"));

            // Un avviso su strumenti spenti non serve a nessuno: con gli strumenti spenti nessuna richiesta li usa.
            if (!tools.Enabled)
            {
                output.WriteLine(Indent + Ui.Text("doctor2.tools.off"));
            }
            else
            {
                output.WriteLine(Indent + Ui.Format("doctor2.tools.on {0}", tools.Sources.Count));

                foreach (SourceDiagnosis source in tools.Sources)
                {
                    output.WriteLine(Indent + Indent + source.Id + ": " + (source.Problem is ToolProblem problem
                        ? Ui.Format("doctor2.tools.problem {0}", problem.Code.ToWire())
                        : Ui.Format("doctor2.tools.usable {0} {1}", source.Tools.Count(tool => tool.Problem is null), source.Tools.Count)));
                }

                if (tools.ClassesWithoutToolCapableModel.Count > 0)
                {
                    todo.Add(Ui.Format("doctor2.todo.tools {0}", string.Join(", ", tools.ClassesWithoutToolCapableModel.Select(dataClass =>
                        Ui.Contains("class." + dataClass.ToWire()) ? Ui.Text("class." + dataClass.ToWire()) : dataClass.ToWire()))));
                }
            }
        }

        // Una conferma non scade (ADR-032, fase 4): dopo un anno si ricorda di ricontrollarla.
        foreach (ConfirmationReminder reminder in report.ConfirmationReminders)
        {
            todo.Add(Ui.Format("doctor.confirmationReminder {0} {1} {2}", reminder.ServiceId, reminder.Control,
                reminder.ConfirmedAt.ToString("yyyy-MM-dd", Formats.Invariant)));
        }

        output.WriteLine();
        output.WriteLine(Ui.Text("doctor2.sec.todo"));

        if (todo.Count == 0) { output.WriteLine(Indent + Ui.Text("doctor2.todo.none")); }

        foreach (string item in todo) { output.WriteLine(Indent + "- " + item); }

        output.WriteLine();
        output.WriteLine(Ui.Text("doctor2.hint"));
    }

    private static void Services(TextWriter output, string title, IReadOnlyList<ServiceDiagnosis> services, Func<string, string?>? quotaOf = null)
    {
        if (services.Count == 0) { return; }

        output.WriteLine();
        output.WriteLine(title);

        foreach (ServiceDiagnosis service in services)
        {
            string name = service.Service is ServiceDescriptor descriptor ? ServiceNames.Display(descriptor) : service.Id;
            string? pausedState = quotaOf?.Invoke(service.Id);
            bool healthy = service.Probe.Health is ProbeHealth.Healthy && pausedState is null;
            List<string> facts = [pausedState ?? (healthy ? Ui.Text("doctor2.service.ok") : Problem(service.Probe.FailureCode))];

            if (service.LocalInferenceGrade is string grade && Ui.Contains("doctor2.local." + grade))
            {
                facts.Add(Ui.Text("doctor2.local." + grade));
            }

            if (service.PrivacyReady is PrivacyReadyVerdict verdict)
            {
                facts.Add(verdict.Ready ? Ui.Text("doctor2.private.ready") : Ui.Format("doctor2.private.not {0}",
                    string.Join(", ", verdict.Failures.Select(failure => failure.Condition.ToWire()))));
            }

            output.WriteLine(Indent + (healthy ? "● " : "○ ") + name + " — " + string.Join("; ", facts));
        }
    }

    private static string Problem(string? code) =>
        code is null ? Ui.Text("doctor2.service.down")
        : Ui.Contains("doctor2.failure." + code) ? Ui.Text("doctor2.failure." + code)
        : Ui.Format("doctor2.failure.other {0}", code);

    /// <summary>Lo stato dell'analizzatore comincia con il modo: <c>decisive</c>, <c>shadow</c> o <c>off</c>.</summary>
    private static string Analyzer(string? described) =>
        described is null || described.StartsWith("off", StringComparison.Ordinal) ? Ui.Text("doctor2.analyzer.off")
        : described.StartsWith("decisive", StringComparison.Ordinal) ? Ui.Text("doctor2.analyzer.decisive")
        : described.Contains("decisive needs", StringComparison.Ordinal) ? Ui.Text("doctor2.analyzer.notMeasured")
        : Ui.Text("doctor2.analyzer.shadow");
}
