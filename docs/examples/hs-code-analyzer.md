---
title: HS Code Analyzer
parent: Examples
nav_order: 6
---

# HS Code Analyzer
{: .no_toc }

A real-world scenario from the Agentic CLI: a vision-powered agent that reads PDF invoices, extracts line items, and assigns Harmonised System (HS) customs codes to each product.

## Overview

This scenario shows how to combine:
- **Vision** — render PDF pages as images and feed them to a vision model
- **Tools** — structured data extraction to a database
- **Workflows** — multi-step verification to ensure completeness
- **ToolContext** — per-request scope headers for multi-tenant isolation

## Components

### PdfTools — Vision-based PDF reading

```csharp
public class PdfTools(ILLMBackend lm) : IAgentToolSet
{
    [Tool, Description(
        "Get the page count and basic info of a PDF. " +
        "Accepts a local file path or HTTP/HTTPS URL.")]
    public async Task<string> GetPdfInfo(
        [ToolParam("Local file path or HTTP/HTTPS URL to the PDF")] string pdfPath)
    {
        using var reader = DocLib.Instance.GetDocReader(
            await EnsureLocalAsync(pdfPath),
            new PageDimensions(1.0));
        return $"PDF has {reader.GetPageCount()} page(s).";
    }

    [Tool, Description(
        "Render one page of a PDF and OCR it with vision AI. " +
        "Returns all extracted text for that page. " +
        "Call GetPdfInfo first to know how many pages exist. Page index is 0-based.")]
    public async Task<string> ScanPdfPage(
        [ToolParam("Local file path or HTTP/HTTPS URL to the PDF")] string pdfPath,
        [ToolParam("Zero-based page index to scan")] int pageIndex = 0,
        [ToolParam("Instruction for the vision model")] string prompt =
            "Extract all invoice content: supplier, buyer, invoice number, date, currency, " +
            "and every line item with its product description, quantity, unit price, total, " +
            "and country of origin.")
    {
        var local             = await EnsureLocalAsync(pdfPath);
        var (dataUrl, _)      = RenderPageToDataUrl(local, pageIndex);
        var resp              = await lm.RespondAsync(
            [ResponseInput.User(prompt, [dataUrl])],
            reasoning: ReasoningEffort.None);
        return resp.OutputText ?? "No text extracted.";
    }
}
```

### TollInvoiceTools — Structured data storage

```csharp
public class TollInvoiceTools(TollInvoiceDbContext db) : IAgentToolSet
{
    [Tool, Description("Save an invoice header.")]
    public async Task<string> SaveInvoiceHeader(
        [ToolParam("Declaration scope from X-Declaration-Scope header")] string scope,
        [ToolParam("Invoice number")] string invoiceNumber,
        /* ... other fields ... */
        ToolContext context)
    {
        var declaredScope = context.GetHeader("X-Declaration-Scope") ?? scope;
        db.Invoices.Add(new TollInvoice { /* ... */ });
        await db.SaveChangesAsync();
        return $"Header saved for invoice {invoiceNumber}";
    }

    [Tool, Description("Assign an HS code to a line item.")]
    public async Task<string> AssignHsCode(
        [ToolParam("Line item ID")] int lineItemId,
        [ToolParam("HS code (e.g. 8471.30)")] string hsCode,
        [ToolParam("HS code description")] string description)
    {
        var item = await db.LineItems.FindAsync(lineItemId)
            ?? throw new Exception($"Line item {lineItemId} not found.");
        item.HsCode      = hsCode;
        item.HsCodeDesc  = description;
        await db.SaveChangesAsync();
        return $"HS code {hsCode} assigned to item {lineItemId}.";
    }
}
```

## Workflow

```csharp
var workflow = new Workflow("HS Code Classification")
    .Step(
        name:        "Scan invoice pages",
        instruction:
            "Use get_pdf_info to get the page count, then scan every page with scan_pdf_page. " +
            "Collect all line items from the invoice.",
        verify:      ctx => ctx.ToolInvocations.Any(t => t.ToolName == "scan_pdf_page"))

    .Step(
        name:        "Save invoice header",
        instruction: "Save the invoice header using save_invoice_header.",
        verify:      ctx => ctx.ToolInvocations.Any(t => t.ToolName == "save_invoice_header"))

    .Step(
        name:        "Save all line items",
        instruction: "Save every line item using save_line_item. Do not skip any.",
        verify:      ctx => ctx.ToolInvocations.Count(t => t.ToolName == "save_line_item") > 0)

    .Step(
        name:        "Assign HS codes",
        instruction:
            "For each saved line item, research and assign the correct HS code using assign_hs_code. " +
            "Use your knowledge of customs classification.",
        verify:      ctx => ctx.ToolInvocations.Any(t => t.ToolName == "assign_hs_code"));
```

## Running the Scenario

```csharp
var result = await agent.RunWorkflowAsync(
    workflow,
    input:        $"Process this invoice PDF: {pdfPath}",
    mcpServerUrl: "http://localhost:5100/mcp",
    maxRounds:    25);

if (result.Completed)
    Console.WriteLine("Invoice fully processed and classified.");
```

## What it demonstrates

- Vision + tool calls in the same workflow
- Using `ToolContext` for multi-tenant data isolation
- Multi-step workflow with real database verification
- Combining multiple tool sets (PDF tools + database tools)
- A production-grade customs/trade classification pipeline
