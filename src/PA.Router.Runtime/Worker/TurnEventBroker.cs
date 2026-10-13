using System.Collections.Concurrent;
using System.Threading.Channels;
using PA.Router.Abstractions.Contracts;

// `Channel` esiste in entrambi i mondi: la coda di System.Threading e il canale di una sessione —
// personale o chatbot — dei contratti del router. Servono tutte e due le direttive, quindi il nome
// va disambiguato invece di rinunciare a una delle due.
using EventQueue = System.Threading.Channels.Channel<PA.Router.Abstractions.Contracts.TurnEvent>;
using Queue = System.Threading.Channels.Channel;

namespace PA.Router.Runtime.Worker;

/// <summary>
/// Distribuisce gli eventi di un turno a chi sta guardando (S65).
///
/// Il worker esegue turni per conto di client che non sono nel suo processo: fino a qui gli eventi
/// — fasi, strumenti, consumo, file toccati, blocchi di risposta — nascevano e morivano dentro il
/// worker, e chi aveva chiesto il turno vedeva soltanto l'esito finale. Era il debito dichiarato in
/// S58 e ripetuto in S63 e S64.
///
/// La sottoscrizione e' **per sessione**, non per esecuzione: il client la sessione la sceglie prima
/// del turno, mentre l'identificatore dell'esecuzione lo assegna il runtime e non lo conosce ancora.
/// Cosi' la stessa operazione serve due cose diverse — seguire il proprio turno e **attaccarsi** da
/// un altro terminale a una conversazione che sta lavorando.
///
/// Gli eventi non vengono conservati: chi arriva dopo vede cio' che accade da li' in avanti. Sono
/// destinati a **mostrare** un turno, e un turno finito non si guarda piu' (03-specs/OBSERVABILITY.md).
/// Chi vuole la storia legge l'audit, che e' l'altro flusso e quello affidabile.
/// </summary>
internal sealed class TurnEventBroker
{
    /// <summary>
    /// Eventi che un osservatore lento puo' accumulare prima che si comincino a scartare.
    ///
    /// Il limite esiste perche' **un guasto della resa non puo' fermare un turno**: se chi guarda
    /// non legge, la coda si riempie e gli eventi nuovi vengono lasciati cadere, non attesi. Un
    /// turno che rallenta perche' un terminale e' bloccato sarebbe il difetto peggiore dei due.
    /// </summary>
    private const int Capacity = 256;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, EventQueue>> sessions =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Osservatore da consegnare al turno di una sessione. Restituisce sempre un osservatore: il
    /// turno non deve sapere se qualcuno sta guardando.
    /// </summary>
    internal ITurnObserver For(string? sessionId) =>
        sessionId is null ? NullTurnObserver.Instance : new SessionObserver(this, sessionId);

    /// <summary>
    /// Si mette in ascolto di una sessione. La sottoscrizione vive finche' il chiamante legge; alla
    /// fine va sempre chiusa, altrimenti la coda resta appesa al broker.
    /// </summary>
    internal Subscription Subscribe(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        EventQueue channel = Queue.CreateBounded<TurnEvent>(new BoundedChannelOptions(Capacity)
        {
            // Chi non legge perde gli eventi vecchi: il turno non aspetta nessuno.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        Guid id = Guid.NewGuid();

        sessions.GetOrAdd(sessionId, _ => new ConcurrentDictionary<Guid, EventQueue>())[id] = channel;

        return new Subscription(this, sessionId, id, channel.Reader);
    }

    /// <summary>Pubblica un evento a chi guarda quella sessione.</summary>
    private void Publish(string sessionId, TurnEvent turnEvent)
    {
        if (!sessions.TryGetValue(sessionId, out ConcurrentDictionary<Guid, EventQueue>? listeners))
        {
            return;
        }

        foreach (EventQueue channel in listeners.Values)
        {
            // Il valore di ritorno si ignora di proposito: una coda piena scarta, non blocca.
            channel.Writer.TryWrite(turnEvent);
        }
    }

    private void Remove(string sessionId, Guid id)
    {
        if (!sessions.TryGetValue(sessionId, out ConcurrentDictionary<Guid, EventQueue>? listeners))
        {
            return;
        }

        listeners.TryRemove(id, out _);

        if (listeners.IsEmpty)
        {
            sessions.TryRemove(sessionId, out _);
        }
    }

    /// <summary>Un ascolto aperto su una sessione.</summary>
    internal sealed class Subscription(TurnEventBroker broker, string sessionId, Guid id, ChannelReader<TurnEvent> reader)
        : IDisposable
    {
        /// <summary>Eventi in arrivo, in ordine di pubblicazione.</summary>
        internal ChannelReader<TurnEvent> Reader => reader;

        /// <inheritdoc />
        public void Dispose() => broker.Remove(sessionId, id);
    }

    /// <summary>
    /// L'osservatore che il turno riceve. Non sa chi stia guardando, e non deve saperlo: e' la
    /// stessa interfaccia che il terminale attua per disegnare (INV-22).
    /// </summary>
    private sealed class SessionObserver(TurnEventBroker broker, string sessionId) : ITurnObserver
    {
        /// <inheritdoc />
        public void Observe(TurnEvent turnEvent)
        {
            ArgumentNullException.ThrowIfNull(turnEvent);

            broker.Publish(sessionId, turnEvent);
        }
    }
}
