using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Agentic.Cli;

// ── Data ──────────────────────────────────────────────────────────────────

public record HsCode(string Chapter, string Position, string SubPosition)
{
    public string Chapter     { get; } = Chapter.Trim().PadLeft(2, '0');
    public string Position    { get; } = Position.Trim().PadLeft(2, '0');
    public string SubPosition { get; } = SubPosition.Trim().PadLeft(4, '0');
    public override string ToString() => $"{Chapter}{Position}{SubPosition}";
    public string ToFullFormat()      => $"{Chapter}{Position}.{SubPosition[..2]}.{SubPosition[2..]}";
}

public class HSDescription
{
    public HsCode Code        { get; init; } = new("00", "00", "0000");
    public string Description { get; init; } = "";
    public override string ToString() =>
        $"{Code.ToFullFormat()}: {Description.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ""}";
}

public class TollInvoice
{
    public int      Id            { get; set; }
    public string   InvoiceNumber { get; set; } = "";
    public string   Supplier      { get; set; } = "";
    public string   Buyer         { get; set; } = "";
    public string   InvoiceDate   { get; set; } = "";
    public string   Currency      { get; set; } = "USD";
    public string?  PdfPath       { get; set; }
    public DateTime ScannedAt     { get; set; } = DateTime.UtcNow;
    public List<TollLineItem> LineItems { get; set; } = [];
}

public class TollLineItem
{
    public int      Id                 { get; set; }
    public int      TollInvoiceId      { get; set; }
    public TollInvoice Invoice         { get; set; } = null!;
    public int      LineNumber         { get; set; }
    public string   ProductDescription { get; set; } = "";
    public decimal  Quantity           { get; set; }
    public string   Unit               { get; set; } = "";
    public decimal  UnitPrice          { get; set; }
    public decimal  TotalPrice         { get; set; }
    public string?  HsCode             { get; set; }
    public string?  HsDescription      { get; set; }
    public string?  CountryOfOrigin    { get; set; }
    public int?     PageNumber         { get; set; }
}

// ── Database ──────────────────────────────────────────────────────────────

public class TollInvoiceDbContext : DbContext
{
    public DbSet<TollInvoice>  Invoices  => Set<TollInvoice>();
    public DbSet<TollLineItem> LineItems => Set<TollLineItem>();
    protected override void OnConfiguring(DbContextOptionsBuilder opt) =>
        opt.UseInMemoryDatabase("toll_invoices");
    protected override void OnModelCreating(ModelBuilder mb) =>
        mb.Entity<TollInvoice>()
          .HasMany(i => i.LineItems).WithOne(l => l.Invoice).HasForeignKey(l => l.TollInvoiceId);
}

// ── Tools ─────────────────────────────────────────────────────────────────

public class HsCodeAnalyzerTools(LM lm, ICollection<HSDescription> hs, TollInvoiceDbContext db) : IDisposableToolSet
{
    private const string DataFile  = "taks_records.json";
    private const int    BatchSize = 50;

    private static readonly string     s_renderDir = Path.Combine(AppContext.BaseDirectory, "pdf-renders");
    private static readonly HttpClient s_http      = new();
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

    private CancellationTokenSource? _cts;
    private Task?                    _task;
    private volatile string          _phase    = "idle";
    private string?                  _error;
    private int                      _embedded, _failed, _batchDone, _batchTotal;

    // ── PDF ───────────────────────────────────────────────────────────────

    [Tool, Description("Get the page count of a PDF. Accepts a local path or HTTP/HTTPS URL.")]
    public async Task<string> GetPdfInfo([ToolParam("Local path or HTTP/HTTPS URL")] string pdfPath)
    {
        try
        {
            var local = await EnsureLocalAsync(pdfPath);
            using var reader = DocLib.Instance.GetDocReader(local, new PageDimensions(1.0));
            return $"PDF has {reader.GetPageCount()} page(s).";
        }
        catch (Exception ex) { return $"Failed to read PDF: {ex.Message}"; }
    }

    [Tool, Description(
        "Render one page of a PDF and OCR it with vision AI. Returns all extracted text. " +
        "Call GetPdfInfo first. Page index is 0-based.")]
    public async Task<string> ScanPdfPage(
        [ToolParam("Local path or HTTP/HTTPS URL")] string pdfPath,
        [ToolParam("Zero-based page index")] int pageIndex = 0,
        [ToolParam("Extraction prompt")] string prompt =
            "Extract all invoice content: supplier, buyer, invoice number, date, currency, " +
            "and every line item with product description, quantity, unit, unit price, total price, and country of origin.")
    {
        try
        {
            var local = await EnsureLocalAsync(pdfPath);
            var (dataUrl, savePath) = RenderPage(local, pageIndex);
            var resp  = await lm.RespondAsync([ResponseInput.User(prompt, [dataUrl])],
                            thinking: new ThinkingConfig { Enabled = false });
            var text  = ExtractText(resp);
            return text.Length > 0
                ? $"[Page {pageIndex} · {savePath}]\n{text}"
                : $"[Page {pageIndex} · {savePath}] (no content extracted)";
        }
        catch (Exception ex) { return $"Failed to scan page {pageIndex}: {ex.Message}"; }
    }

    // ── Image ─────────────────────────────────────────────────────────────

    [Tool, Description("OCR an invoice image (URL or base64 data URL). Returns all text on the invoice.")]
    public async Task<string> ScanInvoice(
        [ToolParam("Image URL or base64 data URL")] string imageUrl,
        [ToolParam("Extraction prompt")] string prompt = "OCR Invoice")
    {
        try
        {
            var dataUrl = imageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? await FetchDataUrlAsync(imageUrl) : imageUrl;
            var resp = await lm.RespondAsync([ResponseInput.User(prompt, [dataUrl])],
                           thinking: new ThinkingConfig { Enabled = false });
            return ExtractText(resp) is { Length: > 0 } t ? t : "(no response)";
        }
        catch (Exception ex) { return $"Failed to analyse image: {ex.Message}"; }
    }

    // ── HS codes ──────────────────────────────────────────────────────────

    [Tool, Description(
        "Start embedding all HS code descriptions into the vector database as a background task. " +
        "Returns immediately. Call GetHsCodeEmbedStatus to monitor, then SearchHsCodes when done.")]
    public Task<string> StartHsCodeEmbedding()
    {
        if (_phase is "scanning" or "embedding")
            return Task.FromResult($"Already running [{_phase}]: {_embedded} embedded, {_failed} failed.");
        _cts?.Cancel(); _cts?.Dispose();
        _cts = new CancellationTokenSource(); _phase = "scanning"; _error = null;
        _embedded = _failed = _batchDone = _batchTotal = 0;
        _task = Task.Run(() => EmbedAsync(_cts.Token));
        return Task.FromResult("Embedding started. Call GetHsCodeEmbedStatus to track progress.");
    }

    [Tool, Description("Get the current status of the HS code embedding task.")]
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

    [Tool, Description("Search HS codes by semantic similarity to a product description.")]
    public async Task<string> SearchHsCodes(
        [ToolParam("Natural-language product description")] string query,
        [ToolParam("Number of top results")] int topK = 5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Query cannot be empty.";
        float[] vec;
        try   { vec = await lm.EmbedAsync(query, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"Failed to embed query: {ex.Message}"; }

        var results = await hs.SearchAsync(vec, topK, ct);
        if (results.Count == 0) return "No results — call StartHsCodeEmbedding first.";

        var sb = new StringBuilder();
        sb.AppendLine($"Top {results.Count} for: \"{Truncate(query, 80)}\"");
        sb.AppendLine(new string('─', 60));
        foreach (var r in results)
        {
            sb.AppendLine($"  [{r.Score:F4}]  {r.Document.Code.ToFullFormat()}");
            sb.AppendLine($"            {r.Document.Description}");
        }
        return sb.ToString().TrimEnd();
    }

    // ── Invoices ──────────────────────────────────────────────────────────

    [Tool, Description("Create a new toll invoice. Returns the new invoice ID.")]
    public async Task<string> CreateInvoice(
        [ToolParam("Invoice number")]   string  invoiceNumber,
        [ToolParam("Supplier name")]    string  supplier,
        [ToolParam("Buyer name")]       string  buyer,
        [ToolParam("Invoice date")]     string  invoiceDate,
        [ToolParam("Currency code")]    string  currency = "USD",
        [ToolParam("Source PDF path")] string? pdfPath  = null)
    {
        var inv = new TollInvoice { InvoiceNumber = invoiceNumber, Supplier = supplier, Buyer = buyer,
                                    InvoiceDate = invoiceDate, Currency = currency, PdfPath = pdfPath };
        db.Invoices.Add(inv);
        await db.SaveChangesAsync();
        return $"Invoice created with ID {inv.Id}.";
    }

    [Tool, Description("List all invoices.")]
    public async Task<string> ListInvoices()
    {
        var list = await db.Invoices.OrderByDescending(i => i.ScannedAt).ToListAsync();
        return list.Count == 0 ? "No invoices found."
            : string.Join("\n", list.Select(i =>
                $"  [{i.Id}] {i.InvoiceNumber}  {i.Supplier} → {i.Buyer}  {i.InvoiceDate}  {i.Currency}"));
    }

    [Tool, Description("Get full details of one invoice including all line items.")]
    public async Task<string> GetInvoice([ToolParam("Invoice ID")] int invoiceId)
    {
        var inv = await db.Invoices.Include(i => i.LineItems).FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (inv is null) return $"Invoice {invoiceId} not found.";
        var sb = new StringBuilder();
        sb.AppendLine($"Invoice #{inv.InvoiceNumber} (ID {inv.Id})  {inv.Supplier} → {inv.Buyer}  {inv.InvoiceDate}  {inv.Currency}");
        foreach (var item in inv.LineItems.OrderBy(l => l.LineNumber))
        {
            sb.AppendLine($"  [{item.Id}] L{item.LineNumber}  {item.ProductDescription}  {item.Quantity} {item.Unit}  @ {item.UnitPrice} = {item.TotalPrice}");
            sb.AppendLine($"        HS {item.HsCode ?? "(unclassified)"}  {item.HsDescription ?? ""}  {(item.CountryOfOrigin is not null ? $"COO:{item.CountryOfOrigin}" : "")}");
        }
        return sb.ToString().TrimEnd();
    }

    [Tool, Description("Add a product line item to an invoice. Returns the new line item ID.")]
    public async Task<string> AddLineItem(
        [ToolParam("Invoice ID")]            int      invoiceId,
        [ToolParam("Line number")]           int      lineNumber,
        [ToolParam("Product description")]   string   productDescription,
        [ToolParam("Quantity")]              decimal  quantity,
        [ToolParam("Unit, e.g. PCS, KG")]   string   unit,
        [ToolParam("Unit price")]            decimal  unitPrice,
        [ToolParam("Total line price")]      decimal  totalPrice,
        [ToolParam("Country of origin")]     string?  countryOfOrigin = null,
        [ToolParam("PDF page number")]       int?     pageNumber      = null)
    {
        if (await db.Invoices.FindAsync(invoiceId) is null) return $"Invoice {invoiceId} not found.";
        var item = new TollLineItem { TollInvoiceId = invoiceId, LineNumber = lineNumber,
            ProductDescription = productDescription, Quantity = quantity, Unit = unit,
            UnitPrice = unitPrice, TotalPrice = totalPrice,
            CountryOfOrigin = countryOfOrigin, PageNumber = pageNumber };
        db.LineItems.Add(item);
        await db.SaveChangesAsync();
        return $"Line item {item.Id} added.";
    }

    [Tool, Description("Set the HS code and description for a line item.")]
    public async Task<string> UpdateLineItemHsCode(
        [ToolParam("Line item ID")]    int    lineItemId,
        [ToolParam("HS code")]         string hsCode,
        [ToolParam("HS description")]  string hsDescription)
    {
        var item = await db.LineItems.FindAsync(lineItemId);
        if (item is null) return $"Line item {lineItemId} not found.";
        item.HsCode = hsCode; item.HsDescription = hsDescription;
        await db.SaveChangesAsync();
        return $"Line item {lineItemId}: {hsCode} — {hsDescription}";
    }

    [Tool, Description("Update fields on a line item. Pass null to keep the current value.")]
    public async Task<string> UpdateLineItem(
        [ToolParam("Line item ID")]          int      lineItemId,
        [ToolParam("Product description")]   string?  productDescription = null,
        [ToolParam("Quantity")]              decimal? quantity            = null,
        [ToolParam("Unit")]                  string?  unit                = null,
        [ToolParam("Unit price")]            decimal? unitPrice           = null,
        [ToolParam("Total price")]           decimal? totalPrice          = null,
        [ToolParam("Country of origin")]     string?  countryOfOrigin     = null)
    {
        var item = await db.LineItems.FindAsync(lineItemId);
        if (item is null) return $"Line item {lineItemId} not found.";
        if (productDescription is not null) item.ProductDescription = productDescription;
        if (quantity           is not null) item.Quantity           = quantity.Value;
        if (unit               is not null) item.Unit               = unit;
        if (unitPrice          is not null) item.UnitPrice          = unitPrice.Value;
        if (totalPrice         is not null) item.TotalPrice         = totalPrice.Value;
        if (countryOfOrigin    is not null) item.CountryOfOrigin    = countryOfOrigin;
        await db.SaveChangesAsync();
        return $"Line item {lineItemId} updated.";
    }

    [Tool, Description("Delete a line item from an invoice.")]
    public async Task<string> DeleteLineItem([ToolParam("Line item ID")] int lineItemId)
    {
        var item = await db.LineItems.FindAsync(lineItemId);
        if (item is null) return $"Line item {lineItemId} not found.";
        db.LineItems.Remove(item); await db.SaveChangesAsync();
        return $"Line item {lineItemId} deleted.";
    }

    // ── Internals ─────────────────────────────────────────────────────────

    private static (string DataUrl, string SavePath) RenderPage(string localPath, int pageIndex)
    {
        using var reader = DocLib.Instance.GetDocReader(localPath, new PageDimensions(2.0));
        var count = reader.GetPageCount();
        if (pageIndex < 0 || pageIndex >= count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), $"Page {pageIndex} out of range (0–{count - 1}).");
        using var page = reader.GetPageReader(pageIndex);
        using var img  = Image.LoadPixelData<Bgra32>(page.GetImage(), page.GetPageWidth(), page.GetPageHeight());
        img.Mutate(x => x.BackgroundColor(Color.White));
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms, new JpegEncoder { Quality = 80 });
        var jpeg = ms.ToArray();
        Directory.CreateDirectory(s_renderDir);
        var path = Path.Combine(s_renderDir, $"{Path.GetFileNameWithoutExtension(localPath)}-page{pageIndex}.jpg");
        File.WriteAllBytes(path, jpeg);
        return ($"data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}", path);
    }

    private static async Task<string> EnsureLocalAsync(string path)
    {
        if (!path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return path;
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pdf");
        using var r = await s_http.GetAsync(path);
        r.EnsureSuccessStatusCode();
        await File.WriteAllBytesAsync(tmp, await r.Content.ReadAsByteArrayAsync());
        return tmp;
    }

    private static async Task<string> FetchDataUrlAsync(string url)
    {
        using var r   = await s_http.GetAsync(url);
        r.EnsureSuccessStatusCode();
        var mime  = r.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        var bytes = await r.Content.ReadAsByteArrayAsync();
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

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

    private async Task EmbedAsync(CancellationToken ct)
    {
        try
        {
            var dataPath = Path.Combine(AppContext.BaseDirectory, DataFile);
            if (!File.Exists(dataPath)) { _error = $"Data file not found: {dataPath}"; _phase = "error"; return; }
            List<HSDescription> all;
            try
            {
                all = JsonSerializer.Deserialize<List<HSDescription>>(
                    await File.ReadAllTextAsync(dataPath, ct), s_json) ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _error = $"Failed to load data: {ex.Message}"; _phase = "error"; return; }

            var existing = new HashSet<string>();
            await foreach (var (id, _) in hs.ScanAsync(ct)) existing.Add(id);

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
                    { await hs.UpsertAsync(batch[j].Code.ToString(), batch[j], vectors[j], ct); _embedded++; }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { _phase = "cancelled"; return; }
                catch (Exception ex) { _failed += batch.Count; Console.Error.WriteLine($"[HsCode] Batch {_batchDone + 1} failed: {ex.Message}"); }
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
        3. Call CreateInvoice with the header data (invoice number, supplier, buyer,
           date, currency, pdf path).
        4. For each product line, call AddLineItem with the page number it came from.
        5. If the HS database is not embedded, call StartHsCodeEmbedding and poll
           GetHsCodeEmbedStatus until "done".
        6. For each line item, call SearchHsCodes then UpdateLineItemHsCode.
        7. Finish with a summary table:
              Line | Product description | Qty | Unit price | HS code | HS description

        Subsequent requests:
        - Add a line         → AddLineItem
        - Correct a field    → UpdateLineItem
        - Re-classify        → SearchHsCodes → UpdateLineItemHsCode
        - Review             → GetInvoice / ListInvoices
        - Remove a line      → DeleteLineItem
        Always reflect changes back as a formatted table.
        """;

    public string Name => "HS Code Analyzer";

    public async Task RunAsync(LM lm, IServiceProvider services, string mcpUrl)
    {
        var toolRegistry = services.GetRequiredService<ToolRegistry>();
        var store        = services.GetRequiredService<IStore>();
        await using var db = new TollInvoiceDbContext();

        toolRegistry.Register(new HsCodeAnalyzerTools(lm, store.Collection<HSDescription>("hscodes"), db));

        ConsoleHelper.WriteDim($"Tools: {string.Join(", ", toolRegistry.GetAllDescriptors().Select(d => d.Name))}");
        Console.WriteLine();

        var agent = new Agent(lm, new AgentOptions
        {
            SystemPrompt = SystemPrompt,
            Compaction   = new CompactionOptions(),
            Thinking     = new ThinkingConfig { Enabled = false },
        });

        await new AgenticRepl(agent, mcpUrl).RunAsync();
    }
}
