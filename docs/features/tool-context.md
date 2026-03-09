---
title: Tool Context
parent: Features
nav_order: 8
---

# Tool Context
{: .no_toc }

## Table of contents
{: .no_toc .text-delta }

1. TOC
{:toc}

---

When an MCP request arrives, Agentic captures all HTTP headers and makes them available to tool methods via `ToolContext`. This lets tools read authentication tokens, tenant IDs, correlation headers, or any other request metadata — without `IHttpContextAccessor`.

## Declaring ToolContext on a Tool Method

Add a `ToolContext` parameter to any `[Tool]` method. The framework injects it automatically, just like `CancellationToken`. It is **invisible to the model** — `ToolContext` never appears in the JSON schema sent to the LLM.

```csharp
public class InvoiceTools : IAgentToolSet
{
    [Tool, Description("Save an invoice header to the database.")]
    public async Task<string> SaveInvoice(
        [ToolParam("Invoice number")] string invoiceNumber,
        [ToolParam("Vendor name")]    string vendor,
        [ToolParam("Total amount")]   decimal total,
        ToolContext context,
        CancellationToken ct)
    {
        // Read any header forwarded from the original HTTP request
        var scope  = context.GetHeader("X-Declaration-Scope");
        var tenant = context.GetHeader("X-Tenant-Id");

        // ... save to database using scope / tenant ...

        return $"Invoice {invoiceNumber} saved (scope={scope})";
    }
}
```

## How Headers Flow

```
HTTP request → MCP endpoint → BuildToolContext(HttpContext)
  ↓                                  ↓
  All request headers        ToolContext { Headers, Properties }
                                         ↓
                              ToolRegistry.InvokeAsync
                                         ↓
                              BindArguments auto-injects ToolContext
                                         ↓
                              Your [Tool] method receives it
```

Every header on the inbound HTTP request is captured into a **case-insensitive** dictionary. Standard headers (`Authorization`, `Content-Type`, etc.) and custom headers (`X-Tenant-Id`, `X-Correlation-Id`, etc.) are all available.

## Sending Custom Headers from a Client

Any HTTP client calling the MCP endpoint can attach headers that your tools will receive:

```bash
curl -X POST http://localhost:5100/mcp \
  -H "Authorization: Bearer my-key" \
  -H "X-Tenant-Id: acme-corp" \
  -H "X-Declaration-Scope: import" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "tools/call",
    "params": {
      "name": "save_invoice",
      "arguments": {
        "invoiceNumber": "INV-001",
        "vendor": "Acme",
        "total": 1500
      }
    }
  }'
```

Inside the tool, `context.GetHeader("X-Tenant-Id")` returns `"acme-corp"`.

## ToolContext API Reference

| Member | Type | Description |
|--------|------|-------------|
| `Headers` | `IReadOnlyDictionary<string, string>` | All HTTP headers from the MCP request (case-insensitive keys) |
| `Properties` | `IReadOnlyDictionary<string, object?>` | Arbitrary key-value data set by the caller |
| `GetHeader(name)` | `string?` | Convenience — returns the header value or `null` |
| `Get<T>(key)` | `T?` | Typed lookup into `Properties`; returns `default` if absent or wrong type |
| `Empty` | `ToolContext` *(static)* | Singleton with no headers or properties |

## Scope Guardrail Pattern

A common use-case is enforcing business scope rules inside tools — for example, preventing cross-tenant writes or restricting operations to a declared customs scope:

```csharp
[Tool, Description("Delete a line item from the declaration.")]
public Task<string> DeleteLineItem(
    [ToolParam("Line item ID")] int lineItemId,
    ToolContext context)
{
    var scope = context.GetHeader("X-Declaration-Scope")
        ?? throw new InvalidOperationException("Missing X-Declaration-Scope header.");

    if (scope != "import")
        return Task.FromResult($"Denied: delete not allowed under scope '{scope}'.");

    // ... perform delete ...
    return Task.FromResult($"Line item {lineItemId} deleted.");
}
```

## Multi-tenant Pattern

```csharp
public class OrderTools : IAgentToolSet
{
    private readonly IOrderRepository _orders;

    public OrderTools(IOrderRepository orders) => _orders = orders;

    [Tool, Description("Get orders for the current tenant.")]
    public async Task<string> GetOrders(
        [ToolParam("Optional status filter")] string? status,
        ToolContext context)
    {
        var tenantId = context.GetHeader("X-Tenant-Id")
            ?? throw new InvalidOperationException("Missing X-Tenant-Id header.");

        var orders = await _orders.GetForTenantAsync(tenantId, status);
        return $"Found {orders.Count} orders for tenant {tenantId}.";
    }
}
```
