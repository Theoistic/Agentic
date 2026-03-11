---
title: Invoice Workflow
parent: Examples
nav_order: 4
---

# Invoice Workflow
{: .no_toc }

A multi-step workflow that processes an invoice image through structured extraction stages.

## Tools

```csharp
public class InvoiceTools : IAgentToolSet
{
    private readonly InvoiceDbContext _db;
    public InvoiceTools(InvoiceDbContext db) => _db = db;

    [Tool, Description("Save the invoice header information.")]
    public async Task<string> SaveInvoiceHeader(
        [ToolParam("Invoice number")]   string invoiceNumber,
        [ToolParam("Invoice date")]     string date,
        [ToolParam("Vendor name")]      string vendor,
        [ToolParam("Currency code")]    string currency,
        [ToolParam("Total amount")]     decimal total,
        ToolContext context)
    {
        var tenantId = context.GetHeader("X-Tenant-Id") ?? "default";

        _db.Invoices.Add(new Invoice
        {
            TenantId      = tenantId,
            InvoiceNumber = invoiceNumber,
            Date          = DateOnly.Parse(date),
            Vendor        = vendor,
            Currency      = currency,
            Total         = total,
        });
        await _db.SaveChangesAsync();
        return $"Header saved: {invoiceNumber}";
    }

    [Tool, Description("Save a single invoice line item.")]
    public async Task<string> SaveLineItem(
        [ToolParam("Invoice number")]      string invoiceNumber,
        [ToolParam("Product description")] string description,
        [ToolParam("Quantity")]            decimal quantity,
        [ToolParam("Unit price")]          decimal unitPrice,
        [ToolParam("Line total")]          decimal lineTotal)
    {
        var invoice = await _db.Invoices
            .FirstAsync(i => i.InvoiceNumber == invoiceNumber);

        _db.LineItems.Add(new LineItem
        {
            InvoiceId   = invoice.Id,
            Description = description,
            Quantity    = quantity,
            UnitPrice   = unitPrice,
            Total       = lineTotal,
        });
        await _db.SaveChangesAsync();
        return $"Line item saved: {description}";
    }
}
```

## Workflow Definition

```csharp
var invoiceNumber = "";

var workflow = new Workflow("Process Invoice")
    .Step(
        name:        "Extract header",
        instruction:
            "Extract the invoice number, date, vendor name, currency, and total amount. " +
            "Call save_invoice_header with the extracted values.",
        verify:      ctx =>
        {
            var call = ctx.ToolInvocations.FirstOrDefault(t => t.ToolName == "save_invoice_header");
            if (call is not null)
                invoiceNumber = ExtractArgument(call.Arguments, "invoiceNumber");
            return Task.FromResult(call is not null);
        })

    .Step(
        name:        "Extract line items",
        instruction:
            "Extract every line item from the invoice. " +
            "Call save_line_item for each one. Do not skip any items.",
        verify: async ctx =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var invoice = await db.Invoices
                .Include(i => i.LineItems)
                .FirstOrDefaultAsync(i => i.InvoiceNumber == invoiceNumber);
            return invoice?.LineItems.Count > 0;
        })

    .Step(
        name:        "Confirm totals",
        instruction:
            "Sum the line item totals and confirm they match the invoice total. " +
            "State explicitly whether they match or there is a discrepancy.",
        verify:      ctx =>
            Task.FromResult(ctx.ResponseText.Contains("match", StringComparison.OrdinalIgnoreCase)
                         || ctx.ResponseText.Contains("discrepancy", StringComparison.OrdinalIgnoreCase)));
```

## Running the Workflow

```csharp
var lm = new OpenAIBackend(new LMConfig
{
    Endpoint  = "http://localhost:1234",
    ModelName = "your-vision-model",
});

var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are an invoice processing assistant.",
    OnEvent      = e =>
    {
        if (e.Kind == AgentEventKind.TextDelta)   Console.Write(e.Text);
        if (e.Kind == AgentEventKind.StepCompleted) Console.WriteLine($"\n✓ Step complete");
    },
});

var result = await agent.RunWorkflowAsync(
    workflow,
    input:        "Process this invoice image.",
    images:       ["/tmp/invoice.jpg"],
    mcpServerUrl: "http://localhost:5100/mcp",
    maxRounds:    15);

if (result.Completed)
    Console.WriteLine($"\n✅ All {result.Steps.Count} steps completed.");
else
{
    var failed = result.Steps.Where(s => s.Status == WorkflowStepStatus.Pending);
    Console.WriteLine($"\n❌ Failed steps: {string.Join(", ", failed.Select(s => s.Name))}");
}
```

## What it demonstrates

- Multi-step workflow with ordered, verified execution
- Async database guardrails in verify callbacks
- `ToolContext` for tenant isolation
- Combining vision (image input) with tool calls
- Tracking state between workflow steps using closures
