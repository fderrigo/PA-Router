using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;
using PA.Router.Hosting.Runtime;
using PA.Router.Memory;

namespace PA.Router.Runtime.Worker;

/// <summary>
/// Svuotamento in sottofondo della coda di indicizzazione.
///
/// La coda esisteva gia' e nessuno la svuotava: un turno lasciava il proprio lavoro in attesa e il
/// turno successivo non trovava la memoria finche' l'operatore non eseguiva <c>pa memory reindex</c>
/// a mano (difetto D1). Il posto dove svuotarla e' il worker, che possiede lo store e vive fra un
/// turno e l'altro.
///
/// Il drain non tocca il percorso del turno: se l'indicizzazione fallisce, il turno resta
/// completato e il lavoro resta visibile come pending o failed, con i suoi tentativi.
/// </summary>
internal sealed class MemoryDrain : IAsyncDisposable
{
    private readonly RouterStack stack;
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Task loop;

    private MemoryStack? memory;
    private MemoryIndexer? indexer;
    private long generation = -1;
    private long drained;

    internal MemoryDrain(RouterStack stack, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(stack);

        this.stack = stack;

        loop = Task.Run(() => RunAsync(interval));
    }

    /// <summary>Sessione sintetica del drain: non appartiene a nessun turno dell'operatore.</summary>
    private const string WorkerSession = "worker-drain";

    /// <summary>Lavori completati da quando il worker e' vivo.</summary>
    internal long Drained => Interlocked.Read(ref drained);

    /// <summary>
    /// Svuota adesso quanto e' in coda, fino al tetto indicato. Serve al percorso di un turno, che
    /// vuole lasciare la memoria consultabile al turno successivo senza attendere il giro
    /// successivo del ciclo.
    /// </summary>
    internal async Task<int> DrainNowAsync(int maxJobs, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // L'indicizzazione embedda testo con la policy del profilo predefinito: se l'operatore la
            // cambia — per esempio vieta l'elaborazione locale della memoria — il drain deve
            // applicare quella nuova dal giro successivo, non quella dell'avvio. Con i file non
            // validi non si indicizza nulla: il lavoro resta in coda.
            StackView view;

            try
            {
                view = stack.EnsureCurrent();
            }
            catch (PaRouterException)
            {
                return 0;
            }

            if (view.Generation != generation)
            {
                memory?.Dispose();

                // L'apertura e l'indicizzatore sono gli stessi di `pa run` e del REPL: qui cambia il
                // ciclo di vita, non che cosa succede. Senza servizio di embedding non si apre nulla.
                memory = TurnMemory.Open(stack, view.Snapshot.DefaultProfile, WorkerSession);
                indexer = memory is null ? null : TurnMemory.IndexerFor(memory);
                generation = view.Generation;
            }

            if (indexer is null)
            {
                return 0;
            }

            int executed = await indexer.DrainAsync(maxJobs, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref drained, executed);

            return executed;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Arresto richiesto.
        }

        memory?.Dispose();
        gate.Dispose();
        shutdown.Dispose();
    }

    private async Task RunAsync(TimeSpan interval)
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                await Task.Delay(interval, shutdown.Token).ConfigureAwait(false);

                try
                {
                    await DrainNowAsync(64, shutdown.Token).ConfigureAwait(false);
                }
                catch (PaRouterException ex)
                {
                    // Un errore di indicizzazione non ferma il worker: il lavoro resta in coda con
                    // i suoi tentativi ed e' visibile a `pa memory status`.
                    stack.Log.Warning("memory_drain_failed", ("reason", ex.Code.ToWire()));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Arresto richiesto.
        }
    }
}
