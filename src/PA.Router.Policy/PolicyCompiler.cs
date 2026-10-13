using PA.Router.Abstractions.Contracts;

namespace PA.Router.Policy;

/// <summary>
/// Composizione della policy: base compilata nel binario e profilo dell'operatore.
///
/// La base e' un tetto: i limiti valgono per minimo, e nessun profilo supera la classe che la beta
/// consente fuori dalla macchina. Nessuna variabile d'ambiente entra qui (03-specs/POLICY.md).
/// </summary>
public static class PolicyCompiler
{
    /// <summary>
    /// Base sicura compilata nel binario. Non e' un default permissivo: e' il tetto massimo che
    /// nessun profilo puo' superare, allineato ai limiti di 00-baseline.md.
    /// </summary>
    public static class Baseline
    {
        public const int MaxInputBytes = 64 * 1024;

        public const int MaxContextBytes = 512 * 1024;

        public const int MaxOutputBytes = 512 * 1024;

        public const int TimeoutSeconds = 300;

        public const int MaxAttempts = 2;

        public const int MaxMemoryRetentionDays = 3650;

        /// <summary>
        /// I limiti della base come sezione <c>limits</c> di una configurazione: i valori piu' larghi che una configurazione
        /// puo' dichiarare. Serve a chi compila un profilo senza una configurazione caricata, come le prove della policy.
        /// </summary>
        public static LimitsConfig Limits { get; } = new(1, TimeoutSeconds, MaxAttempts)
        {
            MaxInputBytes = Baseline.MaxInputBytes,
            MaxContextBytes = Baseline.MaxContextBytes,
            MaxOutputBytes = Baseline.MaxOutputBytes,
        };
    }

    /// <summary>
    /// Compila un profilo con i limiti della configurazione e il canale del front end che riceve la richiesta.
    ///
    /// Fino al 24 settembre 2026 accettava anche una restrizione di progetto (<c>.pa-router/policy.yaml</c>) che nessun
    /// percorso leggeva mai: il turno compilava sempre senza. ADR-032 (fase 1) l'ha tolta invece di lasciarla come
    /// promessa che nessuno manteneva.
    ///
    /// Dalla fase 2 (stesso giorno) i limiti di esecuzione — tempo massimo, tentativi, byte di input, contesto e uscita,
    /// durata dei ricordi — vengono **solo** dalla configurazione, e il canale dal front end: il profilo non li dichiara
    /// piu'. E' l'unico punto dove i limiti entrano nella policy (INV-22); la base del binario resta un tetto.
    /// </summary>
    /// <param name="profile">Profilo dell'operatore.</param>
    /// <param name="config">Configurazione: <c>limits</c> e <c>memory.retentionDays</c>.</param>
    /// <param name="channel">
    /// Canale del front end: <c>chatbot</c> per l'host API, <c>personal_cli</c> per terminale, REPL, worker e script.
    /// </param>
    public static CompiledPolicy Compile(PolicyDocument profile, ConfigDocument config, Channel channel = Channel.PersonalCli)
    {
        ArgumentNullException.ThrowIfNull(config);

        return Compile(profile, config.Limits, config.Memory.RetentionDays, channel);
    }

    /// <summary>
    /// Compila un profilo con limiti gia' letti. La forma con la configurazione intera e' quella dei front end; questa
    /// serve a chi non ha una configurazione caricata, come le prove della policy.
    /// </summary>
    public static CompiledPolicy Compile(PolicyDocument profile, LimitsConfig limits, int memoryRetentionDays, Channel channel = Channel.PersonalCli)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(limits);

        PolicyConstraints constraints = profile.Constraints;

        // Dal 25 settembre 2026 (ADR-032, fase 4) dove vanno i dati lo dicono solo le zone del profilo. Un elenco dei
        // servizi vuoto vuol dire tutti: nessun profilo nega tutto da solo.
        return new CompiledPolicy(
            ProfileId: profile.ProfileId,
            Channel: channel,
            MinimumDataClass: constraints.MinimumDataClass,
            AllowedServiceIds: constraints.AllowedServiceIds,
            Zones: constraints.Zones,
            AllowedCapabilities: constraints.AllowedCapabilities,
            RedactContacts: constraints.RedactContacts,
            MaxInputBytes: Math.Min(Baseline.MaxInputBytes, limits.MaxInputBytes),
            MaxContextBytes: Math.Min(Baseline.MaxContextBytes, limits.MaxContextBytes),
            MaxOutputBytes: Math.Min(Baseline.MaxOutputBytes, limits.MaxOutputBytes),
            TimeoutSeconds: Math.Min(Baseline.TimeoutSeconds, limits.TimeoutSeconds),
            MaxAttempts: Math.Min(Baseline.MaxAttempts, limits.MaxAttempts),
            AllowLocalMemoryProcessing: constraints.AllowLocalMemoryProcessing,
            MaxMemoryRetentionDays: Math.Min(Baseline.MaxMemoryRetentionDays, memoryRetentionDays),
            DenyAll: false,
            DenyAllReason: null)
        {
            // Il profilo restringe i limiti della configurazione.
            ToolLimits = constraints.ToolLimits,
            PersonalDataGrading = constraints.PersonalDataGrading,
            MaxCostPerRequest = limits.MaxCostPerRequest,
        };
    }
}
