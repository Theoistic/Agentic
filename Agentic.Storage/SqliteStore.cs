using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Agentic.Storage;

// ═══════════════════════════════════════════════════════════════════════════
//  SqliteStore
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// SQLite-backed store. Embeddings are stored as compact BLOBs (raw IEEE 754
/// float arrays — the same wire format used by sqlite-vec and pgvector).
/// Similarity search uses SIMD-accelerated cosine over a partial index that
/// skips rows without embeddings. For datasets beyond ~100 K rows, swap in a
/// dedicated ANN backend (Postgres + pgvector, Qdrant, Pinecone, etc.).
/// </summary>
public sealed class SqliteStore : IStore
{
    private readonly string _cs;
    private readonly SqliteConnection? _pinned;

    public SqliteStore(string connectionString)
    {
        _cs = connectionString;
        if (connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            _pinned = new SqliteConnection(connectionString);
            _pinned.Open();
            Pragma(_pinned);
        }
    }

    public IStoreCollection<T> Collection<T>(string name) where T : class =>
        new SqliteCollection<T>(name, this);

    internal ScopedConnection Open()
    {
        if (_pinned is not null) return new(_pinned, owned: false);
        var c = new SqliteConnection(_cs);
        c.Open();
        Pragma(c);
        return new(c, owned: true);
    }

    private static void Pragma(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous  = NORMAL;
            PRAGMA cache_size   = -64000;
            PRAGMA mmap_size    = 268435456;
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _pinned?.Dispose();
}

/// <summary>Disposes owned connections; leaves borrowed (pinned) ones open.</summary>
internal readonly struct ScopedConnection(SqliteConnection conn, bool owned) : IDisposable
{
    public SqliteConnection Conn => conn;
    public void Dispose() { if (owned) conn.Dispose(); }
}

internal sealed class SqliteCollection<T>(string name, SqliteStore store) : IStoreCollection<T>
    where T : class
{
    private readonly string _table = SanitizeName(name);
    private volatile bool _ready;

    private static string SanitizeName(string n)
    {
        foreach (var ch in n)
            if (!char.IsLetterOrDigit(ch) && ch != '_')
                throw new ArgumentException($"Invalid collection name: {n}");
        return $"col_{n}";
    }

    private async Task EnsureAsync(SqliteConnection c)
    {
        if (_ready) return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {_table} (
                id         TEXT PRIMARY KEY,
                data       TEXT NOT NULL,
                embedding  BLOB,
                created_at INTEGER NOT NULL DEFAULT (unixepoch())
            );
            CREATE INDEX IF NOT EXISTS ix_{_table}_emb
                ON {_table}(id) WHERE embedding IS NOT NULL;
            """;
        await cmd.ExecuteNonQueryAsync();
        _ready = true;
    }

    public async Task<string> InsertAsync(T doc, float[]? embedding = null, CancellationToken ct = default)
    {
        using var activity = StorageTelemetry.StartActivity("storage.insert");
        StorageTelemetry.Operations.Add(1, new KeyValuePair<string, object?>("agentic.storage.operation", "insert"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            activity?.SetTag("db.system", "sqlite");
            activity?.SetTag("agentic.storage.collection", _table);
            var id = Guid.CreateVersion7().ToString("N");
            await UpsertCoreAsync(id, doc, embedding, ct);
            return id;
        }
        catch (Exception ex)
        {
            StorageTelemetry.OperationErrors.Add(1);
            StorageTelemetry.RecordException(activity, ex);
            throw;
        }
        finally
        {
            StorageTelemetry.OperationDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("agentic.storage.operation", "insert"));
        }
    }

    public async Task UpsertAsync(string id, T doc, float[]? embedding = null, CancellationToken ct = default)
    {
        using var activity = StorageTelemetry.StartActivity("storage.upsert");
        StorageTelemetry.Operations.Add(1, new KeyValuePair<string, object?>("agentic.storage.operation", "upsert"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            activity?.SetTag("db.system", "sqlite");
            activity?.SetTag("agentic.storage.collection", _table);
            await UpsertCoreAsync(id, doc, embedding, ct);
        }
        catch (Exception ex)
        {
            StorageTelemetry.OperationErrors.Add(1);
            StorageTelemetry.RecordException(activity, ex);
            throw;
        }
        finally
        {
            StorageTelemetry.OperationDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("agentic.storage.operation", "upsert"));
        }
    }

    private async Task UpsertCoreAsync(string id, T doc, float[]? embedding, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(doc);
        using var scope = store.Open();
        await EnsureAsync(scope.Conn);
        using var cmd = scope.Conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {_table}(id, data, embedding) VALUES($id, $data, $emb)
            ON CONFLICT(id) DO UPDATE SET data      = excluded.data,
                                          embedding = COALESCE(excluded.embedding, {_table}.embedding);
            """;
        cmd.Parameters.AddWithValue("$id",   id);
        cmd.Parameters.AddWithValue("$data", json);
        cmd.Parameters.AddWithValue("$emb",  embedding is null ? DBNull.Value : (object)VectorMath.Pack(embedding));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        using var activity = StorageTelemetry.StartActivity("storage.delete");
        StorageTelemetry.Operations.Add(1, new KeyValuePair<string, object?>("agentic.storage.operation", "delete"));
        activity?.SetTag("db.system", "sqlite");
        activity?.SetTag("agentic.storage.collection", _table);
        using var scope = store.Open();
        await EnsureAsync(scope.Conn);
        using var cmd = scope.Conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<T?> GetAsync(string id, CancellationToken ct = default)
    {
        using var activity = StorageTelemetry.StartActivity("storage.get");
        StorageTelemetry.Operations.Add(1, new KeyValuePair<string, object?>("agentic.storage.operation", "get"));
        activity?.SetTag("db.system", "sqlite");
        activity?.SetTag("agentic.storage.collection", _table);
        using var scope = store.Open();
        await EnsureAsync(scope.Conn);
        using var cmd = scope.Conn.CreateCommand();
        cmd.CommandText = $"SELECT data FROM {_table} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var json = (string?)await cmd.ExecuteScalarAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<T>(json);
    }

    public async IAsyncEnumerable<(string Id, T Document)> ScanAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var scope = store.Open();
        await EnsureAsync(scope.Conn);
        using var cmd = scope.Conn.CreateCommand();
        cmd.CommandText = $"SELECT id, data FROM {_table}";
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var doc = JsonSerializer.Deserialize<T>(r.GetString(1));
            if (doc is not null) yield return (r.GetString(0), doc);
        }
    }

    public async Task<List<SearchResult<T>>> SearchAsync(
        float[] query, int topK = 5, CancellationToken ct = default)
    {
        using var activity = StorageTelemetry.StartActivity("storage.search");
        StorageTelemetry.Operations.Add(1, new KeyValuePair<string, object?>("agentic.storage.operation", "search"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            activity?.SetTag("db.system", "sqlite");
            activity?.SetTag("agentic.storage.collection", _table);
            activity?.SetTag("agentic.storage.top_k", topK);

            // Stream rows with embeddings and keep only top-K via a min-heap — O(topK) memory.
            var heap = new SortedList<float, SearchResult<T>>(
                Comparer<float>.Create((a, b) => a == b ? 1 : a.CompareTo(b)));

            using var scope = store.Open();
            await EnsureAsync(scope.Conn);
            using var cmd = scope.Conn.CreateCommand();
            cmd.CommandText = $"SELECT id, data, embedding FROM {_table} WHERE embedding IS NOT NULL";
            using var r = await cmd.ExecuteReaderAsync(ct);

            while (await r.ReadAsync(ct))
            {
                var emb   = VectorMath.Unpack((byte[])r.GetValue(2));
                var score = VectorMath.Cosine(emb, query);

                if (heap.Count < topK || score > heap.Keys[0])
                {
                    var doc = JsonSerializer.Deserialize<T>(r.GetString(1))!;
                    heap.Add(score, new(r.GetString(0), doc, score));
                    if (heap.Count > topK) heap.RemoveAt(0);
                }
            }

            var results = heap.Values.Reverse().ToList();
            activity?.SetTag("agentic.storage.results_count", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            StorageTelemetry.OperationErrors.Add(1);
            StorageTelemetry.RecordException(activity, ex);
            throw;
        }
        finally
        {
            StorageTelemetry.OperationDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("agentic.storage.operation", "search"));
        }
    }
}
