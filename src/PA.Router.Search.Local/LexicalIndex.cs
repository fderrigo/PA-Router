using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PA.Router.Abstractions;
using PA.Router.Abstractions.Contracts;

namespace PA.Router.Search.Local;

/// <summary>
/// Indice lessicale FTS5 in un database <c>:memory:</c> del processo.
///
/// In memoria e non su file: un indice FTS su disco conterrebbe il testo in chiaro, e la memoria
/// e' cifrata a riposo proprio per evitarlo. La cache conserva l'indice per lo snapshot
/// autorizzato e ne serializza accesso e rilascio (03-specs/SEMANTIC-SEARCH.md).
///
/// La query dell'utente non diventa mai sintassi: i token vengono estratti, messi fra virgolette
/// e uniti con OR. Un testo che contiene <c>NEAR</c>, <c>*</c> o un apice resta testo.
/// </summary>
public sealed class LexicalIndex : IDisposable
{
    private readonly SqliteConnection connection;

    private LexicalIndex(SqliteConnection connection) => this.connection = connection;

    /// <summary>Costruisce l'indice sui soli chunk autorizzati.</summary>
    public static LexicalIndex Build(IReadOnlyList<MemoryIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        SqliteConnection connection = new("Data Source=:memory:;Pooling=False");

        try
        {
            connection.Open();
            using (SqliteCommand create = connection.CreateCommand())
            {
                create.CommandText =
                    "PRAGMA temp_store=MEMORY; CREATE VIRTUAL TABLE chunks USING fts5(chunk_id UNINDEXED, body, tokenize='unicode61');"
                    + "CREATE VIRTUAL TABLE query_terms USING fts5(body, tokenize='unicode61');"
                    + "CREATE VIRTUAL TABLE query_vocabulary USING fts5vocab(query_terms, instance);";
                create.ExecuteNonQuery();
            }

            using SqliteTransaction transaction = connection.BeginTransaction();

            foreach (MemoryIndexEntry entry in entries)
            {
                using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO chunks(chunk_id, body) VALUES ($id, $body);";
                insert.Parameters.AddWithValue("$id", entry.ChunkId);
                insert.Parameters.AddWithValue("$body", entry.Text);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();

            return new LexicalIndex(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Chunk piu' rilevanti secondo BM25, dal migliore. In SQLite <c>bm25()</c> restituisce valori
    /// negativi e il piu' piccolo e' il migliore, quindi l'ordine crescente e' quello giusto.
    /// </summary>
    public IReadOnlyList<string> Search(string query, int take)
    {
        // Abstain, never truncate a query and silently weaken its coverage requirement.
        ArgumentNullException.ThrowIfNull(query);
        if (Encoding.UTF8.GetByteCount(query) > MaximumQueryBytes)
        {
            return [];
        }

        IReadOnlyList<QueryTerm> raw = QueryTerms(query);
        if (raw.Count > MaximumQueryTerms) { return []; }

        IReadOnlyList<string> terms = CanonicalTerms(raw);
        string match = Join(terms);

        if (match.Length == 0)
        {
            return [];
        }

        using SqliteCommand command = connection.CreateCommand();
        List<string> selections = [];
        for (int index = 0; index < terms.Count; index++)
        {
            string parameter = "$term" + index.ToString(CultureInfo.InvariantCulture);
            selections.Add("SELECT rowid FROM chunks WHERE chunks MATCH " + parameter);
            command.Parameters.AddWithValue(parameter, Join([terms[index]]));
        }

        // Count indexed matches, not substrings, and qualify before ordering/top-k. Every query
        // term counts in the denominator, including terms absent from this snapshot.
        // Materialize scalar scores once. Pushing rowid IN into the FTS cursor restarts MATCH
        // and BM25 for every eligible row, making common-term queries effectively quadratic.
        command.CommandText = "WITH matches AS (" + string.Join(" UNION ALL ", selections)
            + "), eligible AS (SELECT rowid FROM matches GROUP BY rowid HAVING count(*) >= $minimum), "
            + "ranked AS MATERIALIZED (SELECT rowid, chunk_id, bm25(chunks) AS score FROM chunks WHERE chunks MATCH $query) "
            + "SELECT chunk_id FROM ranked WHERE rowid IN (SELECT rowid FROM eligible) "
            + "ORDER BY score, chunk_id LIMIT $take;";

        command.Parameters.AddWithValue("$query", match);
        command.Parameters.AddWithValue("$take", take);
        command.Parameters.AddWithValue("$minimum", terms.Count == 1 ? 1 : Math.Max(2, (terms.Count + 1) / 2));

        List<string> results = [];

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    /// <summary>
    /// Espressione MATCH costruita dai soli token alfanumerici della query, ciascuno fra
    /// virgolette e uniti con OR. Gli apici interni vengono raddoppiati: e' l'escape di FTS5.
    /// </summary>
    public static string ToMatchExpression(string query) => Join(Tokens(query));

    /// <summary>Pagine allocate dal database, non una misura dell'intero heap nativo.</summary>
    public long AllocatedPageBytes => checked(Count("PRAGMA page_count;", null) * Count("PRAGMA page_size;", null));

    private static string Join(IReadOnlyList<string> tokens) =>
        string.Join(" OR ", tokens.Select(value => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));

    // A lexical heuristic, not a language/semantic classifier. Short explicit single terms,
    // acronyms and numeric identifiers remain searchable; short prose is not promoted by
    // rarity in a small corpus. Long grammatical terms may still pass this heuristic.
    private const int SelectiveLength = 4;
    private const int MaximumQueryTerms = 64;
    private const int MaximumQueryBytes = 16 * 1024;

    private sealed record QueryTerm(string Text, bool Informative);

    private IReadOnlyList<string> CanonicalTerms(IReadOnlyList<QueryTerm> raw)
    {
        if (raw.Count == 0) { return []; }

        // The exact same tokenizer supplies canonical terms for matching and deduplication.
        // Rollback leaves no logical scratch rows even on failure; it is not memory zeroization.
        using SqliteTransaction transaction = connection.BeginTransaction();
        for (int index = 0; index < raw.Count; index++)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO query_terms(rowid, body) VALUES ($id, $body);";
            insert.Parameters.AddWithValue("$id", index + 1);
            insert.Parameters.AddWithValue("$body", raw[index].Text);
            insert.ExecuteNonQuery();
        }

        Dictionary<int, List<string>> canonical = [];
        using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT doc, term FROM query_vocabulary ORDER BY doc, offset;";
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                int index = reader.GetInt32(0) - 1;
                if (!canonical.TryGetValue(index, out List<string>? words))
                {
                    words = [];
                    canonical.Add(index, words);
                }

                words.Add(reader.GetString(1));
            }
        }

        transaction.Rollback();
        return [.. canonical.Where(pair => raw.Count == 1 || raw[pair.Key].Informative)
            .Select(pair => string.Join(' ', pair.Value)).Distinct(StringComparer.Ordinal)];
    }

    private static IReadOnlyList<QueryTerm> QueryTerms(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        for (int index = 0; index < query.Length; index++)
        {
            if (!char.IsSurrogate(query[index])) { continue; }
            if (!char.IsHighSurrogate(query[index]) || index + 1 >= query.Length || !char.IsLowSurrogate(query[index + 1]))
            {
                throw new PaRouterException(ErrorCode.InputInvalid, "A memory query must contain valid Unicode.");
            }

            index++;
        }

        List<QueryTerm> terms = [];
        StringBuilder unit = new();
        foreach (Rune character in query.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(character) || character.Value is '-' or '_'
                || Rune.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or UnicodeCategory.PrivateUse)
            {
                unit.Append(character.ToString());
            }
            else
            {
                FlushUnit(terms, unit);
                if (terms.Count > MaximumQueryTerms) { return terms; }
            }
        }

        FlushUnit(terms, unit);
        return terms;
    }

    private static void FlushUnit(List<QueryTerm> terms, StringBuilder unit)
    {
        if (unit.Length == 0) { return; }
        string[] pieces = unit.ToString().Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        unit.Clear();
        bool identifier = pieces.Length > 1 && pieces.Any(piece => piece.EnumerateRunes().Any(Rune.IsDigit));
        foreach (string text in identifier ? [string.Join(' ', pieces)] : pieces)
        {
            int length = text.EnumerateRunes().Count(character => Rune.GetUnicodeCategory(character)
                is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark));
            bool informative = identifier || length >= SelectiveLength || text.EnumerateRunes().Any(Rune.IsDigit)
                || (length >= 2 && text.EnumerateRunes().All(Rune.IsUpper));
            if (!terms.Any(term => string.Equals(term.Text, text, StringComparison.Ordinal)))
            {
                terms.Add(new QueryTerm(text, informative));
            }
        }
    }

    private long Count(string sql, string? match)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        if (match is not null) { command.Parameters.AddWithValue("$query", match); }
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> Tokens(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<string> tokens = [];
        StringBuilder token = new();

        foreach (char character in query)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(character);
                continue;
            }

            Flush(tokens, token);
        }

        Flush(tokens, token);

        return tokens;
    }

    private static void Flush(List<string> tokens, StringBuilder token)
    {
        if (token.Length == 0)
        {
            return;
        }

        string value = token.ToString().ToLower(CultureInfo.InvariantCulture);
        token.Clear();

        // Un token di una lettera sola non seleziona nulla di utile e allarga solo la OR.
        if (value.Length > 1 && !tokens.Contains(value, StringComparer.Ordinal))
        {
            tokens.Add(value);
        }
    }

    /// <inheritdoc />
    public void Dispose() => connection.Dispose();
}
