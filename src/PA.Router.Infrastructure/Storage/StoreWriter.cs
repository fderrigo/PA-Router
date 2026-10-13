using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Observability;

namespace PA.Router.Infrastructure.Storage;

/// <summary>
/// Writer seriale dello store. Un solo consumatore possiede la connessione di scrittura e
/// applica una transazione breve per volta; i produttori concorrenti attendono in una coda
/// limitata a 128 operazioni (03-specs/LOCAL-RUNTIME.md).
///
/// La coda satura applica backpressure fino alla deadline e poi rifiuta con STORE_UNAVAILABLE:
/// una nuova accettazione viene negata prima di perdere un evento di audit. Non esiste un drop.
///
/// Il lavoro e' sincrono di proposito: l'ADO.NET di SQLite esegue comunque in modo sincrono e
/// fingere I/O asincrono sposterebbe soltanto il blocco su un thread del pool.
/// </summary>
public sealed class StoreWriter : IAsyncDisposable
{
    /// <summary>Capacita' della coda, fissata dalla specifica.</summary>
    public const int ChannelCapacity = 128;

    private readonly Channel<WorkItem> channel;
    private readonly Func<SqliteConnection> openConnection;
    private readonly TimeSpan enqueueTimeout;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task loop;
    private readonly object queueGate = new();
    private int pending;
    private volatile bool closed;
    private Task? disposeTask;

    public StoreWriter(SqliteConnectionFactory factory, TimeSpan enqueueTimeout)
        : this((factory ?? throw new ArgumentNullException(nameof(factory))).Open, enqueueTimeout)
    {
    }

    internal StoreWriter(Func<SqliteConnection> openConnection, TimeSpan enqueueTimeout)
    {
        ArgumentNullException.ThrowIfNull(openConnection);

        if (enqueueTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(enqueueTimeout));
        }

        this.openConnection = openConnection;
        this.enqueueTimeout = enqueueTimeout;

        channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        loop = Task.Run(RunAsync);

        // Profondita' della coda: un numero che sale e non scende e' il primo segno che il writer
        // non sta piu' committando.
        RouterTelemetry.BindGauge("storage_queue_depth", () => PendingCount);
    }

    /// <summary>Operazioni accodate e non ancora committate.</summary>
    public int PendingCount => Volatile.Read(ref pending);

    /// <summary>
    /// Accoda una transazione e attende il commit. Il delegato riceve la connessione del writer
    /// e la transazione gia' aperta con BEGIN IMMEDIATE; deve contenere solo operazioni storage.
    /// </summary>
    /// <param name="transactionName">Nome della transazione, usato nelle diagnosi (T1, T2, T3).</param>
    public async Task<T> ExecuteAsync<T>(
        string transactionName,
        Func<SqliteConnection, SqliteTransaction, T> work,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionName);
        ArgumentNullException.ThrowIfNull(work);

        WorkItem item = new(
            transactionName,
            (connection, transaction) => work(connection, transaction),
            new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously));

        using CancellationTokenSource linked = EnqueueCancellation(transactionName, cancellationToken);
        linked.CancelAfter(enqueueTimeout);

        try
        {
            bool accepted = false;
            while (!accepted && await channel.Writer.WaitToWriteAsync(linked.Token).ConfigureAwait(false))
            {
                lock (queueGate)
                {
                    accepted = channel.Writer.TryWrite(item);
                    if (accepted) { Interlocked.Increment(ref pending); }
                }
            }

            if (!accepted) { throw new ChannelClosedException(); }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PaRouterException(
                ErrorCode.StoreUnavailable,
                "The storage writer queue stayed saturated past the deadline.",
                ex)
                .WithMetadata("transaction", transactionName);
        }
        catch (ChannelClosedException ex)
        {
            throw new PaRouterException(
                ErrorCode.StoreUnavailable,
                "The storage writer is shutting down.",
                ex)
                .WithMetadata("transaction", transactionName);
        }

        object? result = await item.Completion.Task.ConfigureAwait(false);
        return (T)result!;
    }

    private CancellationTokenSource EnqueueCancellation(string name, CancellationToken cancellationToken)
    {
        lock (queueGate)
        {
            // Il controllo e la lettura del token sono atomici rispetto a DisposeAsync.
            if (closed) { throw Unavailable(name); }
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            using SqliteConnection connection = openConnection();
            await foreach (WorkItem item in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                object? result = null;
                Exception? failure = null;
                try { result = Execute(connection, item); }
                catch (Exception ex) { failure = ex is PaRouterException ? ex : Unavailable(item.Name); }
                finally { lock (queueGate) { Interlocked.Decrement(ref pending); } }
                if (failure is null) { item.Completion.TrySetResult(result); }
                else { item.Completion.TrySetException(failure); }
            }
        }
        catch (Exception)
        {
            // Un guasto del consumatore deve chiudere anche tutte le attese dei produttori.
            // Il messaggio pubblico non contiene dettagli del database o percorsi.
        }
        finally
        {
            closed = true;
            channel.Writer.TryComplete();
            while (channel.Reader.TryRead(out WorkItem? item))
            {
                lock (queueGate) { Interlocked.Decrement(ref pending); }
                item.Completion.TrySetException(Unavailable(item.Name));
            }
        }
    }

    private static object? Execute(SqliteConnection connection, WorkItem item)
    {
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        try
        {
            object? result = item.Work(connection, transaction);
            transaction.Commit();
            return result;
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    private static PaRouterException Unavailable(string name) =>
        new PaRouterException(ErrorCode.StoreUnavailable, "The storage operation could not be completed.")
            .WithMetadata("transaction", name);

    private static void Rollback(SqliteTransaction transaction)
    {
        try
        {
            transaction.Rollback();
        }
        catch (InvalidOperationException)
        {
            // La transazione era gia' conclusa: non c'e' nulla da annullare.
        }
        catch (SqliteException)
        {
            // Il rollback fallito non deve nascondere l'errore originale del delegato.
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (queueGate) { return new ValueTask(disposeTask ??= DisposeCoreAsync()); }
    }

    private async Task DisposeCoreAsync()
    {
        closed = true;
        channel.Writer.TryComplete();

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Arresto richiesto: il loop termina senza errore da propagare.
        }
        finally
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
            shutdown.Dispose();
        }
    }

    private sealed record WorkItem(
        string Name,
        Func<SqliteConnection, SqliteTransaction, object?> Work,
        TaskCompletionSource<object?> Completion);
}
