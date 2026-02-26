using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace Agentic.Cli;

public class HsCodeTools(LM lm, ICollection<HSDescription> collection) : IDisposableToolSet
{
    private const string DataFile  = "taks_records.json";
    private const int    BatchSize = 50;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Background embedding state ────────────────────────────────────────

    private CancellationTokenSource? _cts;
    private Task?                    _task;
    private volatile string          _phase    = "idle";   // idle | scanning | embedding | done | cancelled | error
    private string?                  _error;
    private int                      _embedded;
    private int                      _failed;
    private int                      _batchDone;
    private int                      _batchTotal;

    // ── Tools ─────────────────────────────────────────────────────────────

    [Tool, Description(
        "Start embedding all HS code descriptions into the vector database as a background task. " +
        "Returns immediately — the work continues independently of this call. " +
        "Already-embedded codes are skipped so it is safely resumable. " +
        "Call GetHsCodeEmbedStatus to monitor progress and SearchHsCodes once done.")]
    public Task<string> StartHsCodeEmbedding()
    {
        if (_phase is "scanning" or "embedding")
            return Task.FromResult(
                $"Already running [{_phase}]: {_embedded} records embedded, {_failed} failed " +
                $"(batch {_batchDone}/{_batchTotal}). Call GetHsCodeEmbedStatus for live progress.");

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        _phase     = "scanning";
        _error     = null;
        _embedded  = 0;
        _failed    = 0;
        _batchDone = 0;
        _batchTotal = 0;

        _task = Task.Run(() => RunAsync(_cts.Token));
        return Task.FromResult(
            "HS code embedding started in the background. " +
            "Call GetHsCodeEmbedStatus to track progress.");
    }

    [Tool, Description(
        "Get the current status of the background HS code embedding task. " +
        "Reports phase (scanning / embedding / done / cancelled / error), " +
        "records embedded so far, failures, and batch progress.")]
    public Task<string> GetHsCodeEmbedStatus()
    {
        return Task.FromResult(_phase switch
        {
            "idle"      => "No embedding task has been started. Call StartHsCodeEmbedding to begin.",
            "scanning"  => "Scanning the database for already-indexed codes…",
            "embedding" => $"Embedding in progress: {_embedded} records done, {_failed} failed. Batch {_batchDone} / {_batchTotal}.",
            "done"      => $"Embedding complete. {_embedded} records embedded, {_failed} failed across {_batchTotal} batch(es).",
            "cancelled" => $"Embedding was cancelled after {_embedded} records.",
            "error"     => $"Embedding failed: {_error}",
            _           => $"Unknown phase: {_phase}",
        });
    }

    // ── Search ────────────────────────────────────────────────────────────

    [Tool, Description(
        "Search the HS code database by semantic similarity to a natural-language description of goods. " +
        "The query is embedded and compared against pre-indexed HS code vectors. " +
        "Returns the top matching codes with their descriptions and similarity scores. " +
        "Requires StartHsCodeEmbedding to have completed first.")]
    public async Task<string> SearchHsCodes(
        [ToolParam("Natural-language description of the goods to classify")] string query,
        [ToolParam("Number of top results to return")] int topK = 5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Query cannot be empty.";

        float[] queryVector;
        try
        {
            queryVector = await lm.EmbedAsync(query, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"Failed to embed query: {ex.Message}";
        }

        var results = await collection.SearchAsync(queryVector, topK, ct);
        if (results.Count == 0)
            return "No results found. The HS code database may not be embedded yet — call StartHsCodeEmbedding first.";

        var sb = new StringBuilder();
        sb.AppendLine($"Top {results.Count} HS code match(es) for: \"{Truncate(query, 80)}\"");
        sb.AppendLine(new string('─', 60));
        foreach (var r in results)
        {
            sb.AppendLine($"  [{r.Score:F4}]  {r.Document.Code.ToFullFormat()}");
            sb.AppendLine($"            {r.Document.Description}");
        }
        return sb.ToString().TrimEnd();
    }

    // ── Background worker ─────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var dataPath = Path.Combine(AppContext.BaseDirectory, DataFile);
            if (!File.Exists(dataPath))
            {
                _error = $"Data file not found: {dataPath}";
                _phase = "error";
                return;
            }

            List<HSDescription> all;
            try
            {
                var json = await File.ReadAllTextAsync(dataPath, ct);
                all = JsonSerializer.Deserialize<List<HSDescription>>(json, s_jsonOpts) ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _error = $"Failed to load HS data: {ex.Message}";
                _phase = "error";
                return;
            }

            var existing = new HashSet<string>();
            await foreach (var (id, _) in collection.ScanAsync(ct))
                existing.Add(id);

            var pending = all
                .Where(r => !existing.Contains(r.Code.ToString()))
                .ToList();

            if (pending.Count == 0)
            {
                _embedded   = existing.Count;
                _batchTotal = 0;
                _phase      = "done";
                return;
            }

            _batchTotal = (pending.Count + BatchSize - 1) / BatchSize;
            _phase      = "embedding";

            for (int i = 0; i < pending.Count; i += BatchSize)
            {
                if (ct.IsCancellationRequested) { _phase = "cancelled"; return; }

                var batch = pending.Skip(i).Take(BatchSize).ToList();
                var texts = batch.Select(r => $"{r.Code.ToFullFormat()} {r.Description}").ToList();

                try
                {
                    var vectors = await lm.EmbedBatchAsync(texts, ct);
                    for (int j = 0; j < batch.Count && j < vectors.Count; j++)
                    {
                        await collection.UpsertAsync(batch[j].Code.ToString(), batch[j], vectors[j], ct);
                        _embedded++;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _phase = "cancelled";
                    return;
                }
                catch (Exception ex)
                {
                    _failed += batch.Count;
                    Console.Error.WriteLine($"[HsCode] Batch {_batchDone + 1} failed: {ex.Message}");
                }

                _batchDone++;
            }

            _phase = "done";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _phase = "cancelled";
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _phase = "error";
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 3)] + "...";
}
