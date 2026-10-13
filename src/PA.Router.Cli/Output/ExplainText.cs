using PA.Router.Abstractions.Contracts;
using PA.Router.Cli.Localization;
using PA.Router.Cli.Repl;
using PA.Router.Hosting.Runtime;
using PA.Router.Infrastructure;
using PA.Router.Privacy;
using PA.Router.Registry.Loading;

namespace PA.Router.Cli.Output;

/// <summary>
/// L'analisi senza invio scritta per una persona, una resa sola per `pa explain` e `/explain` (23 settembre 2026). Prima
/// entrambi stampavano identificatori, parole del contratto e codici (`codex-cloud deny [AUTH_NOT_ALLOWED,…]`), digest del
/// payload e la versione di un ranking che il turno non usa piu'. Qui: che cosa sembra la richiesta, che dati porta, chi
/// potrebbe rispondere e perche' gli altri no, con le stesse frasi del racconto del turno. Il formato per i programmi e'
/// `--json`, che non cambia.
/// </summary>
internal static class ExplainText
{
    /// <param name="local">Analisi locale.</param>
    /// <param name="plan">Piano della richiesta.</param>
    /// <param name="line">Dove scrivere una riga.</param>
    internal static void Write(LocalAnalysisResult local, TurnPlan plan, Action<string> line)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(line);

        ConfigurationSnapshot snapshot = local.Snapshot;
        PrivacyAnalysis analysis = local.Analysis;

        line(Ui.Text("explain.title"));
        line(string.Empty);

        line(Ui.Format("explain.kind {0}", Ui.Text("narr.intent." + plan.Intent.ToWire())));

        foreach (string limit in plan.Limits) { line("  " + limit); }

        string dataClass = Ui.Text("class." + analysis.Payload.EffectiveClass.ToWire());
        string[] found = [.. analysis.InitialReport.TypeCounts
            .OrderBy(entry => entry.Key.ToWire(), StringComparer.Ordinal)
            .Select(entry => (Ui.Contains("finding." + entry.Key.ToWire()) ? Ui.Text("finding." + entry.Key.ToWire()) : entry.Key.ToWire())
                + (entry.Value > 1 ? " (" + entry.Value.ToString(Formats.Invariant) + ")" : string.Empty))];

        line(found.Length == 0
            ? Ui.Format("explain.data {0}", dataClass)
            : Ui.Format("explain.dataFound {0} {1}", dataClass, string.Join(", ", found)));

        if (analysis.FinalReport.Certainty is not ScanCertainty.Complete)
        {
            line(Ui.Text("explain.scanIncomplete"));
        }

        line(Ui.Format("explain.space {0} {1}",
            local.Profile.EstimatedTokens.ToString(Formats.Invariant),
            local.Band.MinContextTokens.ToString(Formats.Invariant)));

        // Chi risponde e chi lavora per il router: embedding e rilevatori non sono in gara (S116).
        bool Answers(string id) => snapshot.FindService(id) is ServiceDescriptor service && ServiceSetupCatalog.AnswersRequests(service.Capability);
        string Named(string id) => ServiceNames.Display(snapshot, id);

        PolicyDecision[] answering = [.. local.Explain.Decisions.Where(decision => Answers(decision.TargetId))];
        PolicyDecision[] admitted = [.. answering.Where(decision => decision.Allowed).OrderBy(decision => Named(decision.TargetId), StringComparer.CurrentCultureIgnoreCase)];

        line(string.Empty);
        line(admitted.Length == 0 ? Ui.Text("explain.nobody") : Ui.Format("explain.admitted {0}", admitted.Length.ToString(Formats.Invariant)));

        foreach (PolicyDecision decision in admitted)
        {
            ServiceCost? cost = local.Costs.FirstOrDefault(entry => entry.ServiceId == decision.TargetId);

            line("  ● " + Named(decision.TargetId) + " — "
                + OrderText.Describe(local.Order.TryGetValue(decision.TargetId, out OrderPosition place) ? place : null)
                + " · " + (cost?.Ceiling switch
                {
                    null => Ui.Text("list.price.unknown"),
                    0 => Ui.Text("list.price.free"),
                    double estimate => Ui.Format("explain.cost {0} {1}", estimate.ToString("0.######", Formats.Invariant), local.Currency ?? string.Empty),
                }));
        }

        PolicyDecision[] excluded = [.. answering
            .Where(decision => !decision.Allowed && !decision.Reasons.Contains(PolicyReason.TargetDisabled))
            .OrderBy(decision => Named(decision.TargetId), StringComparer.CurrentCultureIgnoreCase)];

        if (excluded.Length > 0)
        {
            line(Ui.Text("explain.excluded"));

            foreach (PolicyDecision decision in excluded)
            {
                line("  ○ " + Named(decision.TargetId) + " — " + string.Join("; ", decision.Reasons.Select(reason =>
                    reason is PolicyReason.CostLimit && local.MaxCostPerRequest is double cap
                        ? CostLimit(local, decision.TargetId, cap)
                        : Ui.Contains("narr.why." + reason.ToWire()) ? Ui.Text("narr.why." + reason.ToWire()) : reason.ToWire())));
            }
        }

        string[] disabled = [.. answering
            .Where(decision => decision.Reasons.Contains(PolicyReason.TargetDisabled))
            .Select(decision => Named(decision.TargetId))
            .Order(StringComparer.CurrentCultureIgnoreCase)];

        if (disabled.Length > 0) { line(Ui.Format("narr.disabled {0}", string.Join(", ", disabled))); }

        string[] helpers = [.. local.Explain.Decisions
            .Where(decision => !Answers(decision.TargetId) && snapshot.FindService(decision.TargetId) is { Enabled: true })
            .Select(decision => Named(decision.TargetId))
            .Order(StringComparer.CurrentCultureIgnoreCase)];

        if (helpers.Length > 0) { line(Ui.Format("narr.auxiliary {0}", string.Join(", ", helpers))); }

        line(string.Empty);
        line(Ui.Text("explain.hint"));
    }

    /// <summary>
    /// Il tetto di spesa con le sue cifre (ADR-032, fase 5): quanto puo' costare al massimo contro il tetto, oppure perche'
    /// un servizio senza prezzo resta fuori. Le stesse frasi del rifiuto di un turno.
    /// </summary>
    private static string CostLimit(LocalAnalysisResult local, string serviceId, double cap)
    {
        string Amount(double value) =>
            value.ToString("0.######", Formats.Invariant) + (local.Currency is string currency ? " " + currency : string.Empty);

        return local.Ceilings.TryGetValue(serviceId, out double? ceiling) && ceiling is double cost
            ? Ui.Format("fact.COST_LIMIT {0} {1}", Amount(cost), Amount(cap))
            : Ui.Format("fact.COST_LIMIT.unknown {0}", Amount(cap));
    }
}
