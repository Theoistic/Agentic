using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentic.Cli;

// ── Data types ────────────────────────────────────────────────────────────

public record HsCode(string Chapter, string Position, string SubPosition)
{
    public string Chapter     { get; } = Chapter.Trim().PadLeft(2, '0');
    public string Position    { get; } = Position.Trim().PadLeft(2, '0');
    public string SubPosition { get; } = SubPosition.Trim().PadLeft(4, '0');

    public override string ToString()  => $"{Chapter}{Position}{SubPosition}";
    public string ToFullFormat()       => $"{Chapter}{Position}.{SubPosition[..2]}.{SubPosition[2..]}";
}

public class HSDescription
{
    public HsCode Code        { get; init; } = new("00", "00", "0000");
    public string Description { get; init; } = "";

    public override string ToString()
    {
        var first = Description.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return $"{Code.ToFullFormat()}: {first}";
    }
}

// ── Image tool ────────────────────────────────────────────────────────────

public class ImageTools(LM lm) : IAgentToolSet
{
    private static readonly HttpClient s_http = new();

    [Tool, Description(
        "OCR an invoice image and extract its contents. " +
        "Accepts an HTTP/HTTPS URL or a base64 data URL. " +
        "Returns all text found on the invoice including product descriptions, quantities, and prices.")]
    public async Task<string> ScanInvoice(
        [ToolParam("Image URL or base64 data URL of the invoice")] string imageUrl,
        [ToolParam("What to extract from the invoice")] string prompt = "OCR Invoice")
    {
        try
        {
            var dataUrl = await ToDataUrlAsync(imageUrl);
            var resp    = await lm.RespondAsync([ResponseInput.User(prompt, [dataUrl])], reasoning: ReasoningEffort.None);
            return ExtractText(resp) is { Length: > 0 } t ? t : "(no response)";
        }
        catch (Exception ex) { return $"Failed to analyse image: {ex.Message}"; }
    }

    private static async Task<string> ToDataUrlAsync(string imageUrl)
    {
        if (!imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return imageUrl;

        using var r   = await s_http.GetAsync(imageUrl);
        r.EnsureSuccessStatusCode();
        var mime  = r.Content.Headers.ContentType?.MediaType ?? InferMime(imageUrl);
        var bytes = await r.Content.ReadAsByteArrayAsync();
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string InferMime(string url) =>
        Path.GetExtension(url.Split('?')[0]).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".gif"            => "image/gif",
            ".webp"           => "image/webp",
            _                 => "image/jpeg",
        };

    private static string ExtractText(ResponseResponse resp)
    {
        var sb = new StringBuilder();
        foreach (var item in resp.Output)
            if (item.Type == "message" && item.Content is not null)
                foreach (var part in item.Content)
                    if (part.Type == "output_text" && part.Text is not null)
                        sb.Append(part.Text);
        return sb.ToString();
    }
}

// ── HS code tool ──────────────────────────────────────────────────────────

public class HsCodeTools(LM lm, ICollection<HSDescription> collection) : IDisposableToolSet
{
    private const string DataFile  = "taks_records.json";
    private const int    BatchSize = 50;

    private static readonly JsonSerializerOptions s_jsonOpts = new() { PropertyNameCaseInsensitive = true };

    private CancellationTokenSource? _cts;
    private Task?                    _task;
    private volatile string          _phase      = "idle";
    private string?                  _error;
    private int                      _embedded, _failed, _batchDone, _batchTotal;

    [Tool, Description(
        "Start embedding all HS code descriptions into the vector database as a background task. " +
        "Returns immediately. Already-embedded codes are skipped so it is safely resumable. " +
        "Call GetHsCodeEmbedStatus to monitor progress, then SearchHsCodes once done.")]
    public Task<string> StartHsCodeEmbedding()
    {
        if (_phase is "scanning" or "embedding")
            return Task.FromResult(
                $"Already running [{_phase}]: {_embedded} embedded, {_failed} failed " +
                $"(batch {_batchDone}/{_batchTotal}).");

        _cts?.Cancel(); _cts?.Dispose();
        _cts        = new CancellationTokenSource();
        _phase      = "scanning";
        _error      = null;
        _embedded   = _failed = _batchDone = _batchTotal = 0;
        _task       = Task.Run(() => EmbedAsync(_cts.Token));
        return Task.FromResult("HS code embedding started. Call GetHsCodeEmbedStatus to track progress.");
    }

    [Tool, Description("Get the current status of the background HS code embedding task.")]
    public Task<string> GetHsCodeEmbedStatus() => Task.FromResult(_phase switch
    {
        "idle"      => "No task started. Call StartHsCodeEmbedding to begin.",
        "scanning"  => "Scanning for already-indexed codes…",
        "embedding" => $"Embedding: {_embedded} done, {_failed} failed. Batch {_batchDone}/{_batchTotal}.",
        "done"      => $"Complete. {_embedded} embedded, {_failed} failed across {_batchTotal} batch(es).",
        "cancelled" => $"Cancelled after {_embedded} records.",
        "error"     => $"Failed: {_error}",
        _           => $"Unknown phase: {_phase}",
    });

    [Tool, Description(
        "Search the HS code database by semantic similarity to a product description. " +
        "Requires StartHsCodeEmbedding to have completed first.")]
    public async Task<string> SearchHsCodes(
        [ToolParam("Natural-language description of the goods to classify")] string query,
        [ToolParam("Number of top results to return")] int topK = 5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Query cannot be empty.";

        float[] vec;
        try   { vec = await lm.EmbedAsync(query, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"Failed to embed query: {ex.Message}"; }

        var results = await collection.SearchAsync(vec, topK, ct);
        if (results.Count == 0)
            return "No results. The HS code database may not be embedded yet — call StartHsCodeEmbedding first.";

        var sb = new StringBuilder();
        sb.AppendLine($"Top {results.Count} match(es) for: \"{Truncate(query, 80)}\"");
        sb.AppendLine(new string('─', 60));
        foreach (var r in results)
        {
            sb.AppendLine($"  [{r.Score:F4}]  {r.Document.Code.ToFullFormat()}");
            sb.AppendLine($"            {r.Document.Description}");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task EmbedAsync(CancellationToken ct)
    {
        try
        {
            var dataPath = Path.Combine(AppContext.BaseDirectory, DataFile);
            if (!File.Exists(dataPath)) { _error = $"Data file not found: {dataPath}"; _phase = "error"; return; }

            List<HSDescription> all;
            try
            {
                var json = await File.ReadAllTextAsync(dataPath, ct);
                all = JsonSerializer.Deserialize<List<HSDescription>>(json, s_jsonOpts) ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _error = $"Failed to load data: {ex.Message}"; _phase = "error"; return; }

            var existing = new HashSet<string>();
            await foreach (var (id, _) in collection.ScanAsync(ct))
                existing.Add(id);

            var pending = all.Where(r => !existing.Contains(r.Code.ToString())).ToList();
            if (pending.Count == 0) { _embedded = existing.Count; _phase = "done"; return; }

            _batchTotal = (pending.Count + BatchSize - 1) / BatchSize;
            _phase      = "embedding";

            for (int i = 0; i < pending.Count; i += BatchSize)
            {
                if (ct.IsCancellationRequested) { _phase = "cancelled"; return; }

                var batch = pending.Skip(i).Take(BatchSize).ToList();
                try
                {
                    var vectors = await lm.EmbedBatchAsync(
                        batch.Select(r => $"{r.Code.ToFullFormat()} {r.Description}").ToList(), ct);
                    for (int j = 0; j < batch.Count && j < vectors.Count; j++)
                    {
                        await collection.UpsertAsync(batch[j].Code.ToString(), batch[j], vectors[j], ct);
                        _embedded++;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                { _phase = "cancelled"; return; }
                catch (Exception ex)
                { _failed += batch.Count; Console.Error.WriteLine($"[HsCode] Batch {_batchDone + 1} failed: {ex.Message}"); }

                _batchDone++;
            }
            _phase = "done";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { _phase = "cancelled"; }
        catch (Exception ex) { _error = ex.Message; _phase = "error"; }
    }

    public ValueTask DisposeAsync() { _cts?.Cancel(); _cts?.Dispose(); return ValueTask.CompletedTask; }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}

// ── Scenario ──────────────────────────────────────────────────────────────

public sealed class HsCodeAnalyzerScenario : IScenario
{
    private const string SystemPrompt = """
        You are a trade-classification assistant. Your job is to scan PDF invoices,
        persist every product line to the invoice database, and classify each line with
        the correct HS code.

        Full workflow when a PDF path or URL is provided:
        1. Call GetPdfInfo to learn the page count.
        2. Call ScanPdfPage for every page (0-based index) to extract the raw text.
        3. Call CreateInvoice with the header data found (invoice number, supplier,
           buyer, date, currency, and the pdf path).
        4. For each product line found across all pages, call AddLineItem, including
           the page number it was found on.
        5. If the HS code database is not embedded yet, call StartHsCodeEmbedding and
           poll GetHsCodeEmbedStatus until it reports "done".
        6. For each line item call SearchHsCodes with the product description, then
           call UpdateLineItemHsCode with the best-matching code and description.
        7. Finish with a summary table:
              Line | Product description | Qty | Unit price | HS code | HS description

        For subsequent requests the user may:
        - Add a line item       → AddLineItem
        - Correct a description → UpdateLineItem
        - Re-classify an item   → SearchHsCodes then UpdateLineItemHsCode
        - Review any invoice    → GetInvoice or ListInvoices
        - Delete a bad line     → DeleteLineItem

        Always reflect changes back as a formatted table.
        """;

    public string Name => "HS Code Analyzer";

    public async Task RunAsync(LM lm, IServiceProvider services, string mcpUrl)
    {
        var toolRegistry = services.GetRequiredService<ToolRegistry>();
        var store        = services.GetRequiredService<IStore>();
        await using var db = new TollInvoiceDbContext();

        toolRegistry.Register(new PdfTools(lm));
        toolRegistry.Register(new ImageTools(lm));
        toolRegistry.Register(new HsCodeTools(lm, store.Collection<HSDescription>("hscodes")));
        toolRegistry.Register(new TollInvoiceTools(db));

        ConsoleHelper.WriteDim($"Tools: {string.Join(", ", toolRegistry.GetAllDescriptors().Select(d => d.Name))}");
        Console.WriteLine();

        var agent = new Agent(lm, new AgentOptions
        {
            SystemPrompt = SystemPrompt,
            Compaction   = new CompactionOptions(),
            Reasoning    = ReasoningEffort.None,
        });

        await new AgenticRepl(agent, mcpUrl).RunAsync();
    }
}
