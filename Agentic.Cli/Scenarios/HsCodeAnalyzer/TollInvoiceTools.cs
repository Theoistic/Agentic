using System.ComponentModel;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Agentic.Cli;

public class TollInvoiceTools(TollInvoiceDbContext db) : IAgentToolSet
{
    // ── Invoice ───────────────────────────────────────────────────────────

    [Tool, Description(
        "Create a new toll invoice in the database. Returns the new invoice ID.")]
    public async Task<string> CreateInvoice(
        [ToolParam("Invoice number from the document")]  string invoiceNumber,
        [ToolParam("Supplier / exporter name")]          string supplier,
        [ToolParam("Buyer / importer name")]             string buyer,
        [ToolParam("Invoice date (any readable format)")] string invoiceDate,
        [ToolParam("Currency code, e.g. USD")]           string currency = "USD",
        [ToolParam("Source PDF path or URL")]            string? pdfPath = null)
    {
        var inv = new TollInvoice
        {
            InvoiceNumber = invoiceNumber,
            Supplier      = supplier,
            Buyer         = buyer,
            InvoiceDate   = invoiceDate,
            Currency      = currency,
            PdfPath       = pdfPath,
            ScannedAt     = DateTime.UtcNow,
        };
        db.Invoices.Add(inv);
        await db.SaveChangesAsync();
        return $"Invoice created with ID {inv.Id}.";
    }

    [Tool, Description("List all invoices in the database.")]
    public async Task<string> ListInvoices()
    {
        var list = await db.Invoices
            .OrderByDescending(i => i.ScannedAt)
            .ToListAsync();

        if (list.Count == 0) return "No invoices found.";

        var sb = new StringBuilder();
        foreach (var inv in list)
            sb.AppendLine($"  [{inv.Id}] {inv.InvoiceNumber}  {inv.Supplier} → {inv.Buyer}  {inv.InvoiceDate}  {inv.Currency}");
        return sb.ToString().TrimEnd();
    }

    [Tool, Description("Get full details of one invoice including all its line items.")]
    public async Task<string> GetInvoice(
        [ToolParam("Invoice ID returned by CreateInvoice")] int invoiceId)
    {
        var inv = await db.Invoices
            .Include(i => i.LineItems)
            .FirstOrDefaultAsync(i => i.Id == invoiceId);

        if (inv is null) return $"Invoice {invoiceId} not found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Invoice #{inv.InvoiceNumber} (ID {inv.Id})");
        sb.AppendLine($"  Supplier : {inv.Supplier}");
        sb.AppendLine($"  Buyer    : {inv.Buyer}");
        sb.AppendLine($"  Date     : {inv.InvoiceDate}  Currency: {inv.Currency}");
        sb.AppendLine($"  PDF      : {inv.PdfPath ?? "(none)"}");
        sb.AppendLine($"  Scanned  : {inv.ScannedAt:u}");
        sb.AppendLine($"  Items    : {inv.LineItems.Count}");

        if (inv.LineItems.Count > 0)
        {
            sb.AppendLine(new string('─', 80));
            foreach (var item in inv.LineItems.OrderBy(l => l.LineNumber))
            {
                sb.AppendLine($"  [{item.Id}] Line {item.LineNumber}  {item.ProductDescription}");
                sb.AppendLine($"        Qty {item.Quantity} {item.Unit}  @ {item.UnitPrice}  = {item.TotalPrice} {inv.Currency}");
                sb.AppendLine($"        HS  {item.HsCode ?? "(unclassified)"}  {item.HsDescription ?? ""}");
                if (item.CountryOfOrigin is not null)
                    sb.AppendLine($"        COO {item.CountryOfOrigin}");
                if (item.PageNumber is not null)
                    sb.AppendLine($"        Page {item.PageNumber}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ── Line items ────────────────────────────────────────────────────────

    [Tool, Description(
        "Add a product line item to an existing invoice. Returns the new line item ID.")]
    public async Task<string> AddLineItem(
        [ToolParam("Invoice ID to attach this line to")]      int     invoiceId,
        [ToolParam("Line number on the original document")]   int     lineNumber,
        [ToolParam("Product description")]                    string  productDescription,
        [ToolParam("Quantity")]                               decimal quantity,
        [ToolParam("Unit of measure, e.g. PCS, KG, M")]      string  unit,
        [ToolParam("Unit price")]                             decimal unitPrice,
        [ToolParam("Total line price")]                       decimal totalPrice,
        [ToolParam("Country of origin if stated")]            string? countryOfOrigin = null,
        [ToolParam("PDF page number this item was found on")] int?    pageNumber       = null)
    {
        var inv = await db.Invoices.FindAsync(invoiceId);
        if (inv is null) return $"Invoice {invoiceId} not found.";

        var item = new TollLineItem
        {
            TollInvoiceId      = invoiceId,
            LineNumber         = lineNumber,
            ProductDescription = productDescription,
            Quantity           = quantity,
            Unit               = unit,
            UnitPrice          = unitPrice,
            TotalPrice         = totalPrice,
            CountryOfOrigin    = countryOfOrigin,
            PageNumber         = pageNumber,
        };
        db.LineItems.Add(item);
        await db.SaveChangesAsync();
        return $"Line item {item.Id} added to invoice {invoiceId}.";
    }

    [Tool, Description(
        "Update the HS code and description for a line item after classification.")]
    public async Task<string> UpdateLineItemHsCode(
        [ToolParam("Line item ID returned by AddLineItem")]           int    lineItemId,
        [ToolParam("HS code string, e.g. 8471.30.00")]               string hsCode,
        [ToolParam("Human-readable HS description for the product")] string hsDescription)
    {
        var item = await db.LineItems.FindAsync(lineItemId);
        if (item is null) return $"Line item {lineItemId} not found.";

        item.HsCode        = hsCode;
        item.HsDescription = hsDescription;
        await db.SaveChangesAsync();
        return $"Line item {lineItemId} updated: {hsCode} — {hsDescription}";
    }

    [Tool, Description("Update any field of an existing line item (description, quantities, prices, etc.).")]
    public async Task<string> UpdateLineItem(
        [ToolParam("Line item ID")]                                  int      lineItemId,
        [ToolParam("New product description (null = keep current)")] string?  productDescription = null,
        [ToolParam("New quantity (null = keep current)")]            decimal? quantity            = null,
        [ToolParam("New unit (null = keep current)")]                string?  unit                = null,
        [ToolParam("New unit price (null = keep current)")]          decimal? unitPrice           = null,
        [ToolParam("New total price (null = keep current)")]         decimal? totalPrice          = null,
        [ToolParam("Country of origin (null = keep current)")]       string?  countryOfOrigin     = null)
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
    public async Task<string> DeleteLineItem(
        [ToolParam("Line item ID to remove")] int lineItemId)
    {
        var item = await db.LineItems.FindAsync(lineItemId);
        if (item is null) return $"Line item {lineItemId} not found.";
        db.LineItems.Remove(item);
        await db.SaveChangesAsync();
        return $"Line item {lineItemId} deleted.";
    }
}
