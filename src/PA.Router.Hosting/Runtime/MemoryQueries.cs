using PA.Router.Abstractions.Contracts;
using PA.Router.Policy;

namespace PA.Router.Hosting.Runtime;

/// <summary>Stato della memoria visibile da un ambito, con la coda di indicizzazione.</summary>
/// <param name="Configured">Vero se esiste un servizio di embedding utilizzabile.</param>
/// <param name="ModelFingerprint">Fingerprint del modello in uso, se configurato.</param>
/// <param name="Observations">Ricordi visibili dall'ambito.</param>
/// <param name="JobsPending">Lavori in attesa.</param>
/// <param name="JobsRunning">Lavori in corso.</param>
/// <param name="JobsFailed">Lavori falliti dopo i tentativi ammessi.</param>
/// <param name="Generation">Generazione del progetto, che invalida cio' che e' stato costruito prima.</param>
/// <param name="LastJobError">Ultimo guasto di un lavoro non concluso: codice e momento, se c'e'.</param>
public sealed record MemoryQueueStatus(
    bool Configured,
    string? ModelFingerprint,
    int Observations,
    int JobsPending,
    int JobsRunning,
    int JobsFailed,
    long Generation,
    MemoryIndexFailure? LastJobError = null);

/// <summary>
/// Guasto dell'indicizzazione con il momento in cui e' stato registrato.
///
/// Il codice da solo si legge come «adesso»: un guasto di venti minuti fa, gia' corretto nella configurazione,
/// faceva credere rotto cio' che era stato appena sistemato. La coda riprova al turno successivo, non quando
/// qualcuno guarda.
/// </summary>
/// <param name="Code">Codice stabile dell'errore.</param>
/// <param name="At">Momento del tentativo fallito.</param>
public sealed record MemoryIndexFailure(string Code, DateTimeOffset At);

/// <summary>
/// Letture della memoria per un operatore: ricerca e stato.
///
/// `pa memory search`, `pa memory status` e i comandi <c>/memory</c> e <c>/jobs</c> del REPL
/// passano da qui. L'ambito lo decide il profilo con <see cref="TurnMemory.ScopeFor"/>, mai un
/// parametro del chiamante (INV-22).
/// </summary>
public static class MemoryQueries
{
    /// <summary>
    /// Vero se la configurazione ammette la memoria locale. Con la memoria disattivata nessun front
    /// end la cerca o ne mostra lo stato: la regola sta qui per non esistere in una copia per
    /// ciascuno.
    /// </summary>
    public static bool Available(Registry.Loading.ConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.Config.Memory.Enabled;
    }

    /// <summary>
    /// Vero se la memoria e' attiva ma nessun servizio di embedding e' configurato: la ricerca
    /// risponde solo per parole. Non e' un guasto e non blocca i turni, nemmeno con
    /// <c>memory.required</c>; va detto prima, perche' chi si aspetta una ricerca per significato
    /// deve sapere che non la sta avendo.
    /// </summary>
    public static bool LexicalOnly(Registry.Loading.ConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.Config.Memory.Enabled && snapshot.Config.Memory.IsUnconfigured;
    }

    /// <summary>
    /// Vero se **nessun turno** potra' mai lasciare un ricordo, perche' il profilo fissa la classe minima a
    /// <c>secret</c>: un segreto non entra in memoria in nessuna forma (S10), e il floor si applica prima della
    /// scansione, quindi ogni osservazione nasce segreta e viene scartata.
    ///
    /// Non e' un guasto ed e' una scelta legittima — «tratta tutto come un segreto» — ma senza dirlo la memoria
    /// sembra rotta: l'operatore la accende, configura un servizio di embedding, e la ricerca resta vuota per
    /// sempre senza un solo errore. Il calcolo sta qui perche' lo mostrano `doctor`, il setup e <c>/memory</c>.
    /// </summary>
    public static bool NeverRecords(Registry.Loading.ConfigurationSnapshot snapshot, string? profileId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.Config.Memory.Enabled) { return false; }

        string id = profileId ?? snapshot.Config.DefaultProfileId;

        // `unknown` sta sopra `secret` nell'ordine delle classi: un floor cosi' alto non lascia passare nulla, per la
        // stessa ragione e con lo stesso effetto.
        return snapshot.Profiles.TryGetValue(id, out PolicyDocument? profile)
            && PolicyCompiler.Compile(profile, snapshot.Config).MinimumDataClass is DataClass.Secret or DataClass.Unknown;
    }

    /// <summary>
    /// Vero se ogni ricordo restera' **dentro la conversazione che lo ha prodotto**.
    ///
    /// L'ambito lo decide la classe osservata: `public` e `internal` valgono per il progetto,
    /// `personal` e `sensitive` restano nella sessione (S10). Il floor del profilo si applica prima
    /// della scansione, quindi un floor `personal` rende **ogni** ricordo di sessione: la memoria
    /// funziona, si scrive, si cerca — e sparisce aprendo una conversazione nuova.
    ///
    /// Non e' un difetto ed e' esattamente cio' che quel profilo chiede. Ma va detto: un operatore
    /// che alza il floor per far funzionare la memoria ottiene una memoria che non attraversa le
    /// conversazioni, e senza una riga che lo dica lo scopre cercando un ricordo che c'e' ancora.
    /// </summary>
    public static bool SessionScopedOnly(Registry.Loading.ConfigurationSnapshot snapshot, string? profileId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.Config.Memory.Enabled) { return false; }

        string id = profileId ?? snapshot.Config.DefaultProfileId;

        return snapshot.Profiles.TryGetValue(id, out PolicyDocument? profile)
            && PolicyCompiler.Compile(profile, snapshot.Config).MinimumDataClass is DataClass.Personal or DataClass.Sensitive;
    }

    /// <summary>
    /// Apre la composizione della memoria per una sessione. A differenza di
    /// <see cref="TurnMemory.Open"/> si apre anche senza servizio di embedding: una ricerca senza
    /// modello resta lessicale e lo dichiara, e lo stato deve poter dire che il modello manca.
    /// </summary>
    public static MemoryStack Open(RouterStack stack, PolicyDocument profile, string sessionId, out MemoryScope scope)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(profile);

        CompiledPolicy policy = PolicyCompiler.Compile(profile, stack.Snapshot.Config);

        scope = TurnMemory.ScopeFor(stack.Snapshot, policy, sessionId);

        return MemoryStack.Create(stack, stack.Snapshot, policy, scope);
    }

    /// <summary>Ricerca nell'ambito autorizzato.</summary>
    public static async Task<MemorySearchResult> SearchAsync(
        MemoryStack memory,
        MemoryScope scope,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(scope);

        return await memory.Search.SearchAsync(scope, query, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ultimo guasto dell'indicizzazione non ancora superato, con il suo momento.</summary>
    public static MemoryIndexFailure? Failure(MemoryStack memory)
    {
        ArgumentNullException.ThrowIfNull(memory);

        return memory.Store.LastJobError() is (string code, DateTimeOffset at) ? new MemoryIndexFailure(code, at) : null;
    }

    /// <summary>Stato della memoria visibile e della coda di indicizzazione.</summary>
    public static async Task<MemoryQueueStatus> StatusAsync(
        MemoryStack memory,
        MemoryScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(scope);

        IReadOnlyList<ObservationRecord> visible =
            await memory.Store.ReadAuthorizedAsync(scope, cancellationToken).ConfigureAwait(false);

        return new MemoryQueueStatus(
            memory.Configured,
            memory.Fingerprint,
            visible.Count,
            memory.Store.CountJobs(MemoryJobState.Pending),
            memory.Store.CountJobs(MemoryJobState.Running),
            memory.Store.CountJobs(MemoryJobState.Failed),
            memory.Store.ReadGeneration(scope.ProjectId),
            Failure(memory));
    }
}
