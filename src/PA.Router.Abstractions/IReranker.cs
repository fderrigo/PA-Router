namespace PA.Router.Abstractions;

/// <summary>
/// Riordino delle candidate di una ricerca con un modello locale.
///
/// Esiste per una ragione misurata, non per completezza: sul corpus versionato il recupero trova la
/// nota pertinente fra le cinquanta candidate in **tutti** i casi, e la mette fuori dalle prime
/// cinque in sette casi su trenta. Il tetto non e' il modello di embedding, e' l'ordinamento
/// (evidence/S32/AC17-DIAGNOSI.md).
///
/// Un riordinatore **non recupera**: riceve le candidate che il recupero gli passa e cambia solo il
/// loro ordine. Non puo' aggiungere un documento che la ricerca non ha prodotto, e quindi non puo'
/// far comparire un ricordo che l'ambito aveva escluso — il filtro di ambito e' avvenuto prima, e
/// resta l'unica cosa che decide che cosa e' visibile.
///
/// Gira in processo come il rilevatore PII: nessuna porta, nessun programma da avviare, nessun
/// egress. La domanda e il testo dei ricordi non lasciano la macchina.
/// </summary>
public interface IReranker : IDisposable
{
    /// <summary>Identificatore del modello, per fingerprint e diagnosi.</summary>
    string Name { get; }

    /// <summary>Versione del riordinatore, registrata dove serve spiegare un ordine.</summary>
    string Version { get; }

    /// <summary>
    /// Punteggio di pertinenza di ogni documento rispetto alla domanda, nello stesso ordine dei
    /// documenti ricevuti. Piu' alto e' piu' pertinente; la scala non ha significato assoluto e
    /// serve solo a ordinare.
    ///
    /// Sincrono perche' l'inferenza lo e': gira in processo su tensori gia' in memoria, e
    /// avvolgerla in un <c>Task</c> fingerebbe un'asincronia che non esiste.
    /// </summary>
    /// <param name="query">Testo della domanda.</param>
    /// <param name="documents">Documenti candidati, gia' filtrati per ambito.</param>
    /// <param name="budget">
    /// Tempo massimo per il lavoro **aggiuntivo**. La prima coppia si valuta sempre: un budget
    /// cosi' stretto da non valutarne nessuna renderebbe il riordino un interruttore nascosto.
    /// Oltre il budget le coppie restanti conservano l'ordine che avevano.
    /// </param>
    IReadOnlyList<double> Score(string query, IReadOnlyList<string> documents, TimeSpan budget);
}
