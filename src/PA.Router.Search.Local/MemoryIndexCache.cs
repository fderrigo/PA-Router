using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Search.Local;

/// <summary>
/// Cache degli indici caricati, con budget e sfratto LRU.
///
/// La chiave contiene progetto, sessione, forma dell'ambito, fingerprint e generazione. La
/// generazione e' la parte che conta: una transazione che modifica o revoca un ricordo la
/// incrementa, quindi lo snapshot precedente non puo' piu' essere usato per rispondere. Una cache
/// che sopravvivesse a una revoca restituirebbe cio' che e' appena stato tolto
/// (03-specs/SEMANTIC-SEARCH.md).
///
/// Vive solo in RAM: non esiste scrittura di cache in chiaro su disco.
/// </summary>
public sealed class MemoryIndexCache : IDisposable
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> order = [];
    private readonly Lock gate = new();
    private readonly IClock? clock;
    private readonly bool retainWhenIdle;
    private long budgetBytes;
    private long usedBytes;
    private long epoch;
    private int leases;
    private bool disposeRequested;
    private bool disposed;

    public MemoryIndexCache(long budgetBytes = MemoryLimits.CacheBudgetBytes, IClock? clock = null, bool retainWhenIdle = true)
    {
        if (budgetBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        }

        this.budgetBytes = budgetBytes;
        this.clock = clock;
        this.retainWhenIdle = retainWhenIdle;
    }

    /// <summary>
    /// Keeps a runtime-owned cache alive until this caller finishes. Owner disposal refuses new
    /// leases immediately, but existing readers retain their indices until the last lease ends.
    /// </summary>
    public IDisposable AcquireLease()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposeRequested, this);
            leases = checked(leases + 1);
            return new CacheLease(this);
        }
    }

    /// <summary>Changes the shared retained budget and evicts the oldest entries immediately.</summary>
    public void Resize(long bytes)
    {
        if (bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposeRequested, this);
            budgetBytes = bytes;
            EvictUnderLock(0);
        }
    }

    /// <summary>Current budget for all retained entries, shared by every lease.</summary>
    public long BudgetBytes
    {
        get
        {
            lock (gate)
            {
                return budgetBytes;
            }
        }
    }

    /// <summary>Capture before loading: an invalidation prevents that load from repopulating the cache.</summary>
    public long Epoch
    {
        get
        {
            lock (gate)
            {
                return epoch;
            }
        }
    }

    /// <summary>Byte occupati dagli snapshot in cache.</summary>
    public long UsedBytes
    {
        get
        {
            lock (gate)
            {
                return usedBytes;
            }
        }
    }

    /// <summary>Snapshot presenti.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    /// <summary>Chiave di cache di un ambito a una certa generazione.</summary>
    public static string Key(MemoryScope scope, long generation, string? fingerprint)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return string.Join(
            '|',
            scope.ProjectId.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
            scope.SessionId,
            scope.Channel.ToWire(),
            scope.IncludeProjectScope ? "project" : "session",
            scope.MaxClassification.ToWire(),
            scope.RequirePublishedForChatbot ? "published" : "any",
            fingerprint ?? "none",
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Snapshot in cache per quella chiave, se esiste.</summary>
    public MemoryIndexSnapshot? TryGet(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(key, out Entry entry))
            {
                return null;
            }

            // TTL can elapse without a write, so the generation alone is not sufficient.
            if (entry.ExpiresAt <= (clock?.UtcNow ?? DateTimeOffset.UtcNow))
            {
                entries.Remove(key);
                order.Remove(entry.Node);
                usedBytes -= entry.Size;
                entry.Lexical?.Dispose();
                return null;
            }

            order.Remove(entry.Node);
            order.AddFirst(entry.Node);

            return entry.Snapshot;
        }
    }

    /// <summary>
    /// Rechecks TTL after asynchronous loading or embedding. Fresh snapshots keep their identity
    /// and retained lexical index; only an expired snapshot needs a filtered, temporary index.
    /// </summary>
    internal MemoryIndexSnapshot FilterExpired(MemoryIndexSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DateTimeOffset now = clock?.UtcNow ?? DateTimeOffset.UtcNow;
        if (!snapshot.Observations.Any(observation => observation.ExpiresAt <= now))
        {
            return snapshot;
        }

        IReadOnlyList<ObservationRecord> current = [.. snapshot.Observations.Where(observation => observation.ExpiresAt > now)];
        HashSet<string> ids = current.Select(observation => observation.Id).ToHashSet(StringComparer.Ordinal);
        return snapshot with
        {
            Observations = current,
            Entries = [.. snapshot.Entries.Where(entry => ids.Contains(entry.ObservationId))],
        };
    }

    /// <summary>
    /// Inserisce uno snapshot e il suo indice. Il budget limita la stima della cache trattenuta,
    /// non i picchi di costruzione o gli snapshot ancora usati da ricerche in volo.
    /// </summary>
    public void Set(string key, MemoryIndexSnapshot snapshot, long? expectedEpoch = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (expectedEpoch is long expected && expected != epoch)
            {
                return;
            }

            if (entries.TryGetValue(key, out Entry same) && ReferenceEquals(same.Snapshot, snapshot))
            {
                return;
            }

            long size = Size(key, snapshot);
            CheckBudget(size);
            LexicalIndex? lexical = snapshot.Entries.Count == 0 ? null : LexicalIndex.Build(snapshot.Entries);
            try
            {
                // Database pages plus a second copy for SQLite's page cache, and fixed native
                // bookkeeping. This is conservative accounting, not a process-RSS guarantee.
                size = checked(size + (lexical is null ? 0 : lexical.AllocatedPageBytes * 2 + 64 * 1024));
                CheckBudget(size);
            }
            catch
            {
                lexical?.Dispose();
                throw;
            }

            if (entries.TryGetValue(key, out Entry existing))
            {
                order.Remove(existing.Node);
                entries.Remove(key);
                usedBytes -= existing.Size;
                existing.Lexical?.Dispose();
            }

            EvictUnderLock(size);

            LinkedListNode<string> node = order.AddFirst(key);
            DateTimeOffset expiresAt = snapshot.Observations.Count == 0
                ? DateTimeOffset.MaxValue
                : snapshot.Observations.Min(observation => observation.ExpiresAt);
            entries[key] = new Entry(snapshot, lexical, size, node, expiresAt);
            usedBytes += size;
        }
    }

    /// <summary>Releases retained indices and rejects loads begun before this invalidation.</summary>
    public void Clear()
    {
        lock (gate)
        {
            ClearUnderLock();
        }
    }

    /// <summary>
    /// Cerca nell'indice dello snapshot esatto. Il lock copre solo SQLite, mai embedding o I/O
    /// dello store. Eviction e dispose non possono chiudere una connessione in uso. Se lo snapshot
    /// e' stato sfrattato durante l'embedding, l'indice temporaneo non ripopola la cache.
    /// </summary>
    public IReadOnlyList<string> SearchLexical(string key, MemoryIndexSnapshot snapshot, string query, int take)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (snapshot.Entries.Count == 0)
            {
                return [];
            }

            if (entries.TryGetValue(key, out Entry entry) && ReferenceEquals(entry.Snapshot, snapshot))
            {
                return entry.Lexical?.Search(query, take) ?? [];
            }

            CheckBudget(Size(key, snapshot));
            using LexicalIndex temporary = LexicalIndex.Build(snapshot.Entries);
            CheckBudget(checked(Size(key, snapshot) + temporary.AllocatedPageBytes * 2 + 64 * 1024));
            return temporary.Search(query, take);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
        {
            disposeRequested = true;
            if (leases == 0)
            {
                disposed = true;
                ClearUnderLock();
            }
        }
    }

    private void ReleaseLease()
    {
        lock (gate)
        {
            leases--;
            if (disposeRequested && leases == 0)
            {
                disposed = true;
                ClearUnderLock();
            }
            else if (!retainWhenIdle && leases == 0)
            {
                // A read-only runtime cannot receive another process's write notifications.
                ClearUnderLock();
            }
        }
    }

    private void EvictUnderLock(long incomingBytes)
    {
        while (usedBytes + incomingBytes > budgetBytes && order.Last is LinkedListNode<string> last)
        {
            Entry evicted = entries[last.Value];
            entries.Remove(last.Value);
            order.RemoveLast();
            usedBytes -= evicted.Size;
            evicted.Lexical?.Dispose();
        }
    }

    private void ClearUnderLock()
    {
        epoch++;
        foreach (Entry entry in entries.Values)
        {
            entry.Lexical?.Dispose();
        }

        entries.Clear();
        order.Clear();
        usedBytes = 0;
    }

    private void CheckBudget(long size)
    {
        if (size > budgetBytes)
        {
            throw new PaRouterException(ErrorCode.MemoryLimit, "This memory index does not fit the cache budget.")
                .WithMetadata("bytes", size.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithMetadata("budgetBytes", budgetBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// UTF-16, array, riferimenti e oggetti, anche quando stringhe sono condivise. La sovrastima
    /// intenzionale evita di contare solo il payload UTF-8 ignorando il costo managed.
    /// </summary>
    private static long Size(string key, MemoryIndexSnapshot snapshot)
    {
        long size = 512 + StringSize(key) + StringSize(snapshot.Fingerprint);

        foreach (MemoryIndexEntry entry in snapshot.Entries)
        {
            size += 128 + StringSize(entry.ChunkId) + StringSize(entry.ObservationId) + StringSize(entry.Text);
            size += 32 + (entry.Vector?.LongLength ?? 0) * sizeof(float);
        }

        foreach (ObservationRecord observation in snapshot.Observations)
        {
            size += 512 + StringSize(observation.Content) + StringSize(observation.Id)
                + StringSize(observation.SessionId) + StringSize(observation.ExecutionId)
                + StringSize(observation.EventKey) + StringSize(observation.Supersedes);
            foreach (string evidence in observation.EvidenceRefs)
            {
                size += 16 + StringSize(evidence);
            }
        }

        return size;
    }

    private static long StringSize(string? value) => value is null ? 0 : 32 + value.Length * 2L;

    private sealed class CacheLease(MemoryIndexCache cache) : IDisposable
    {
        private MemoryIndexCache? owner = cache;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseLease();
    }

    private readonly record struct Entry(MemoryIndexSnapshot Snapshot, LexicalIndex? Lexical, long Size, LinkedListNode<string> Node, DateTimeOffset ExpiresAt);
}
