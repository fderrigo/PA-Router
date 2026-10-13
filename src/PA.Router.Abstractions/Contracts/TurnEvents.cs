namespace PA.Router.Abstractions.Contracts;

/// <summary>
/// Evento di avanzamento di un turno, destinato a **mostrarlo** a chi lo ha chiesto.
///
/// E' una cosa diversa dalla telemetria e dall'audit, e non va confusa: la telemetria e'
/// best-effort e finisce in metriche e span, l'audit e' affidabile e finisce nello store, questi
/// eventi servono al front end e muoiono con il turno. Non vengono campionati, non vengono
/// esportati, non vengono conservati (03-specs/OBSERVABILITY.md).
///
/// L'insieme e' chiuso e nessun evento porta argomenti di strumenti, risultati, credenziali o testo
/// della domanda. L'unico contenuto che attraversa questo flusso e' un blocco di risposta **gia'
/// passato dallo screening**, con la regola di rilascio di 03-specs/PRIVACY.md.
/// </summary>
public abstract record TurnEvent;

/// <summary>Una fase del turno e' cominciata.</summary>
/// <param name="Phase">Nome della fase, da un insieme noto al router.</param>
public sealed record TurnPhaseStarted(string Phase) : TurnEvent;

/// <summary>
/// Una fase si e' conclusa, con il suo esito e quanto e' durata. La durata serve a capire dove se
/// ne va il tempo di un turno: prima non era visibile da nessuna parte.
/// </summary>
/// <param name="Phase">Nome della fase.</param>
/// <param name="Outcome">Esito sintetico, senza contenuti.</param>
/// <param name="Elapsed">Durata osservata.</param>
public sealed record TurnPhaseEnded(string Phase, string Outcome, TimeSpan Elapsed) : TurnEvent;

/// <summary>
/// Uno strumento e' stato eseguito. Nome, sorgente e giro, mai argomenti o risultati: e' la stessa
/// regola dell'audit (ADR-020).
/// </summary>
/// <param name="Tool">Nome dello strumento.</param>
/// <param name="Source">Sorgente che lo offre.</param>
/// <param name="Round">Giro del dialogo modello-strumento.</param>
public sealed record TurnToolCalled(string Tool, string Source, int Round) : TurnEvent;

/// <summary>Consumo dichiarato dal provider, quando lo dichiara.</summary>
/// <param name="Usage">Token e costo osservati.</param>
public sealed record TurnUsageObserved(UsageReport Usage) : TurnEvent;

/// <summary>
/// Un file che il programma **dichiara** di aver toccato.
///
/// E' un effetto affermato dall'adapter, non osservato dal router: chi lo mostra deve dirlo. Fino
/// alla S60 il percorso arrivava dagli adapter e il coordinatore lo schiacciava in un booleano,
/// quindi chi guardava un turno non poteva sapere **quali** file fossero cambiati.
///
/// Porta il percorso e nient'altro: nessun contenuto, nessun frammento di diff.
/// </summary>
/// <param name="RelativePath">Percorso dichiarato dal programma.</param>
public sealed record TurnArtifactChanged(string RelativePath) : TurnEvent;

/// <summary>
/// Un blocco di risposta e' stato rilasciato: **e' gia' passato dallo screening**, altrimenti non
/// sarebbe qui. E' l'unico evento che porta testo.
/// </summary>
/// <param name="Text">Testo ammesso, gia' esaminato e, se richiesto, redatto.</param>
public sealed record TurnBlockReleased(string Text) : TurnEvent;

/// <summary>
/// Chi guarda un turno mentre accade.
///
/// Il front end lo attua per disegnare; il motore lo chiama e non sa che cosa ne venga fatto. E'
/// INV-22 applicato a cio' che si mostra: l'evento e' l'operazione, scritta una volta; il disegno
/// e' di ciascun front end.
/// </summary>
public interface ITurnObserver
{
    /// <summary>
    /// Riceve un evento. Un'attuazione non deve lanciare: un guasto della resa non puo' fermare un
    /// turno che sta gia' lavorando.
    /// </summary>
    /// <param name="turnEvent">Evento appena accaduto.</param>
    void Observe(TurnEvent turnEvent);
}

/// <summary>Osservatore che non fa nulla: il caso normale di chi non guarda.</summary>
public sealed class NullTurnObserver : ITurnObserver
{
    /// <summary>Istanza condivisa.</summary>
    public static NullTurnObserver Instance { get; } = new();

    private NullTurnObserver()
    {
    }

    /// <inheritdoc />
    public void Observe(TurnEvent turnEvent)
    {
    }
}
