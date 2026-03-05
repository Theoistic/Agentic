namespace Agentic.Cli;

public class TollInvoice
{
    public int     Id            { get; set; }
    public string  InvoiceNumber { get; set; } = "";
    public string  Supplier      { get; set; } = "";
    public string  Buyer         { get; set; } = "";
    public string  InvoiceDate   { get; set; } = "";
    public string  Currency      { get; set; } = "USD";
    public string? PdfPath       { get; set; }
    public DateTime ScannedAt   { get; set; } = DateTime.UtcNow;

    public List<TollLineItem> LineItems { get; set; } = [];
}

public class TollLineItem
{
    public int     Id                 { get; set; }
    public int     TollInvoiceId      { get; set; }
    public TollInvoice Invoice        { get; set; } = null!;
    public int     LineNumber         { get; set; }
    public string  ProductDescription { get; set; } = "";
    public decimal Quantity           { get; set; }
    public string  Unit               { get; set; } = "";
    public decimal UnitPrice          { get; set; }
    public decimal TotalPrice         { get; set; }
    public string? HsCode             { get; set; }
    public string? HsDescription      { get; set; }
    public string? CountryOfOrigin    { get; set; }
    public int?    PageNumber         { get; set; }
}
