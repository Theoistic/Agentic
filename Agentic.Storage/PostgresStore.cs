using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;

namespace Agentic.Storage;

// ═══════════════════════════════════════════════════════════════════════════
//  PostgresStore
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// PostgreSQL-backed store. Embeddings are stored as <c>BYTEA</c> (raw IEEE 754
/// float arrays — identical wire format to the SQLite BLOB). Similarity search
/// uses SIMD-accelerated cosine in-process. For large datasets swap in pgvector
/// with an HNSW index instead.
/// </summary>
public sealed class PostgresStore : IStore
{
    private readonly string _cs;
    private readonly ConcurrentDictionary<string, object> _cols = new();

    public PostgresStore(string connectionString) => _cs = connectionString;

    public IStoreCollection<T> Collection<T>(string name) where T : class =>
        (IStoreCollection<T>)_cols.GetOrAdd($"{name}:{typeof(T).FullName}", _ => new PostgresCollection<T>(name, this));

    internal NpgsqlConnection Open()
    {
        var conn = new NpgsqlConnection(_cs);
        conn.Open();
        return conn;
    }

    public void Dispose() => _cols.Clear();
}

internal sealed class PostgresCollection<T>(string name, PostgresStore store) : IStoreCollection<T>
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

    private async Task EnsureAsync(NpgsqlConnection conn)
    {
        if (_ready) return;
        using var tbl = conn.CreateCommand();
        tbl.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {_table} (
                id         TEXT PRIMARY KEY,
                data       TEXT NOT NULL,
                embedding  BYTEA,
                created_at BIGINT NOT NULL DEFAULT EXTRACT(EPOCH FROM NOW())::BIGINT
            )
            """;
        await tbl.ExecuteNonQueryAsync();
        using var idx = conn.CreateCommand();
        idx.CommandText = $"""
            CREATE INDEX IF NOT EXISTS ix_{_table}_emb
                ON {_table}(id) WHERE embedding IS NOT NULL
            """;
        await idx.ExecuteNonQueryAsync();
        _ready = true;
    }

    public async Task<string> InsertAsync(T doc, float[]? embedding = null, CancellationToken ct = default)
    {
        using var activity = StorageTelemetry.StartActivity("storage.insert");
        StorageTelemetry.Operations.Add(1, new KeyValuePair<string, object?>("agentic.storage.operation", "insert"));
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("agentic.storage.collection", _table);
        var id = Guid.CreateVersion7().ToString("N");
        await UpsertCoreAsync(id, doc, embedding, ct);
        return id;
    }

    public async Task UpsertAsync(string id, T doc, float[]? embedding = null, CancellationToken ct = default)
    {
        using var activity = StorageTelemetry.StartActivity("storage.upsert");
        StorageTelemetry.Operations.Add(1, new KeyValuePair<string, object?>("agentic.storage.operation", "upsert"));
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("agentic.storage.collection", _table);
        await UpsertCoreAsync(id, doc, embedding, ct);
    }

    private async Task UpsertCoreAsync(string id, T doc, float[]? embedding, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(doc);
        using var conn = store.Open();
        await EnsureAsync(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {_table}(id, data, embedding) VALUES(@id, @data, @emb)
            ON CONFLICT(id) DO UPDATE SET data      = EXCLUDED.data,
                                          embedding = COALESCE(EXCLUDED.embedding, {_table}.embedding);
            """;
        cmd.Parameters.AddWithValue("@id",   id);
        cmd.Parameters.AddWithValue("@data", json);
        cmd.Parameters.AddWithValue("@emb",  embedding is null ? DBNull.Value : (object)VectorMath.Pack(embedding));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        using var activity = StorageTelemetry.StartActivity("storage.delete");
        StorageTelemetry.Operations.Add(1, new KeyValuePair<string, object?>("agentic.storage.operation", "delete"));
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("agentic.storage.collection", _table);
        using var conn = store.Open();
        await EnsureAsync(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_table} WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<T?> GetAsync(string id, CancellationToken ct = default)
    {
        using var activity = StorageTelemetry.StartActivity("storage.get");
        StorageTelemetry.Operations.Add(1, new KeyValuePair<string, object?>("agentic.storage.operation", "get"));
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("agentic.storage.collection", _table);
        using var conn = store.Open();
        await EnsureAsync(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT data FROM {_table} WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        var json = (string?)await cmd.ExecuteScalarAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<T>(json);
    }

    public async IAsyncEnumerable<(string Id, T Document)> ScanAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var conn = store.Open();
        await EnsureAsync(conn);
        using var cmd = conn.CreateCommand();
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
            activity?.SetTag("db.system", "postgresql");
            activity?.SetTag("agentic.storage.collection", _table);
            activity?.SetTag("agentic.storage.top_k", topK);

            var heap = new SortedList<float, SearchResult<T>>(
                Comparer<float>.Create((a, b) => a == b ? 1 : a.CompareTo(b)));

            using var conn = store.Open();
            await EnsureAsync(conn);
            using var cmd = conn.CreateCommand();
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
        catch (Exception ex) { StorageTelemetry.OperationErrors.Add(1); StorageTelemetry.RecordException(activity, ex); throw; }
        finally { StorageTelemetry.OperationDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("agentic.storage.operation", "search")); }
    }
}
