using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  Storage abstractions
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>A ranked result from a vector similarity search.</summary>
/// <typeparam name="T">The document type stored in the collection.</typeparam>
/// <param name="Id">The document's unique identifier.</param>
/// <param name="Document">The retrieved document.</param>
/// <param name="Score">Cosine similarity score in the range [0, 1].</param>
public sealed record SearchResult<T>(string Id, T Document, float Score) where T : class;

/// <summary>
/// Unified collection: document CRUD + optional vector similarity search.
/// Pass embeddings alongside documents to enable <see cref="SearchAsync"/>.
/// When no explicit embedding is supplied, an upsert preserves any previously
/// stored embedding (useful for updating document fields without re-embedding).
/// </summary>
public interface ICollection<T> where T : class
{
    /// <summary>Insert with an auto-generated ID. Returns the new ID.</summary>
    Task<string> InsertAsync(T doc, float[]? embedding = null, CancellationToken ct = default);
    /// <summary>Insert or replace the document with the given <paramref name="id"/>. Preserves any existing embedding when none is supplied.</summary>
    Task UpsertAsync(string id, T doc, float[]? embedding = null, CancellationToken ct = default);
    /// <summary>Removes the document with the given <paramref name="id"/>.</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);
    /// <summary>Returns the document with the given <paramref name="id"/>, or <c>null</c> if not found.</summary>
    Task<T?> GetAsync(string id, CancellationToken ct = default);
    /// <summary>Streams all documents in the collection as <c>(id, document)</c> pairs.</summary>
    IAsyncEnumerable<(string Id, T Document)> ScanAsync(CancellationToken ct = default);
    /// <summary>Returns the top <paramref name="topK"/> documents ranked by cosine similarity to <paramref name="query"/>.</summary>
    Task<List<SearchResult<T>>> SearchAsync(float[] query, int topK = 5, CancellationToken ct = default);
}

/// <summary>
/// Storage backend factory. Ships with <see cref="InMemoryStore"/> and
/// <see cref="SqliteStore"/>; implement for Postgres/pgvector, Redis, etc.
/// </summary>
public interface IStore : IDisposable
{
    /// <summary>Gets or creates a named collection for documents of type <typeparamref name="T"/>.</summary>
    ICollection<T> Collection<T>(string name) where T : class;
}

// ═══════════════════════════════════════════════════════════════════════════
//  Shared SIMD vector math
// ═══════════════════════════════════════════════════════════════════════════

public static class VectorMath
{
    /// <summary>Cosine similarity using hardware SIMD (AVX2 / SSE4 / NEON).</summary>
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int len  = Math.Min(a.Length, b.Length);
        int vLen = Vector<float>.Count;
        int i    = 0;

        var vDot = Vector<float>.Zero;
        var vA   = Vector<float>.Zero;
        var vB   = Vector<float>.Zero;

        for (; i <= len - vLen; i += vLen)
        {
            var va = new Vector<float>(a.Slice(i, vLen));
            var vb = new Vector<float>(b.Slice(i, vLen));
            vDot += va * vb;
            vA   += va * va;
            vB   += vb * vb;
        }

        float dot   = Vector.Dot(vDot, Vector<float>.One);
        float normA = Vector.Dot(vA,   Vector<float>.One);
        float normB = Vector.Dot(vB,   Vector<float>.One);

        for (; i < len; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0f ? 0f : dot / denom;
    }

    /// <summary>L2-normalize in place. Returns the same array for chaining.</summary>
    public static float[] Normalize(float[] v)
    {
        float norm = 0f;
        foreach (var x in v) norm += x * x;
        norm = MathF.Sqrt(norm);
        if (norm > 0f) for (int i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }

    internal static byte[]  Pack(float[] v)  => MemoryMarshal.AsBytes(v.AsSpan()).ToArray();
    internal static float[] Unpack(byte[] b) => MemoryMarshal.Cast<byte, float>(b).ToArray();
}

// ═══════════════════════════════════════════════════════════════════════════
//  InMemoryStore
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Volatile in-memory <see cref="IStore"/> implementation backed by concurrent dictionaries. Intended for testing and short-lived scenarios.</summary>
public sealed class InMemoryStore : IStore
{
    private readonly ConcurrentDictionary<string, object> _cols = new();

    public ICollection<T> Collection<T>(string name) where T : class =>
        (ICollection<T>)_cols.GetOrAdd($"{name}:{typeof(T).FullName}", _ => new InMemoryCollection<T>());

    public void Dispose() => _cols.Clear();
}

internal sealed class InMemoryCollection<T> : ICollection<T> where T : class
{
    private readonly ConcurrentDictionary<string, (T Doc, float[]? Emb)> _data = new();

    public Task<string> InsertAsync(T doc, float[]? embedding = null, CancellationToken ct = default)
    {
        var id = Guid.CreateVersion7().ToString("N");
        _data[id] = (doc, embedding);
        return Task.FromResult(id);
    }

    public Task UpsertAsync(string id, T doc, float[]? embedding = null, CancellationToken ct = default)
    {
        _data.AddOrUpdate(id, (doc, embedding),
            (_, old) => (doc, embedding ?? old.Emb));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        _data.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task<T?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_data.TryGetValue(id, out var e) ? e.Doc : null);

    public async IAsyncEnumerable<(string Id, T Document)> ScanAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        foreach (var kv in _data)
        {
            ct.ThrowIfCancellationRequested();
            yield return (kv.Key, kv.Value.Doc);
        }
    }

    public Task<List<SearchResult<T>>> SearchAsync(float[] query, int topK = 5, CancellationToken ct = default)
    {
        var results = _data
            .Where(kv => kv.Value.Emb is not null)
            .Select(kv => new SearchResult<T>(kv.Key, kv.Value.Doc, VectorMath.Cosine(kv.Value.Emb!, query)))
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
        return Task.FromResult(results);
    }
}
