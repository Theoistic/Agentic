---
title: Tool System
parent: Features
nav_order: 3
---

# Tool System
{: .no_toc }

## Table of contents
{: .no_toc .text-delta }

1. TOC
{:toc}

---

Agentic's tool system lets you expose .NET methods to the language model using simple attributes. No boilerplate, no schema writing — just decorate your methods and register them.

## Defining Tools

Implement `IAgentToolSet` and annotate methods with `[Tool]`:

```csharp
using System.ComponentModel;

public class WeatherTools : IAgentToolSet
{
    [Tool, Description("Get the current weather for a city.")]
    public Task<string> GetWeather(
        [ToolParam("City name")] string city,
        [ToolParam("Unit: celsius or fahrenheit")] string unit = "celsius")
    {
        return Task.FromResult(
            $"The weather in {city} is 22 °{(unit == "fahrenheit" ? "F" : "C")} and sunny.");
    }

    [Tool, Description("List cities with active weather alerts.")]
    public Task<string[]> GetAlerts()
    {
        return Task.FromResult(new[] { "Paris", "London" });
    }
}
```

## Attributes Reference

| Attribute | Target | Description |
|-----------|--------|-------------|
| `[Tool]` | Method | Marks the method as an LLM-callable tool |
| `[ToolParam("description")]` | Parameter | Describes the parameter to the model |
| `[Description("...")]` | Method | The tool description shown to the model (from `System.ComponentModel`) |

## Special Parameters

These parameter types are injected automatically and are **invisible to the model**:

| Type | Description |
|------|-------------|
| `CancellationToken` | Cancelled when the tool call times out |
| `ToolContext` | HTTP request headers from the MCP request (see [Tool Context](tool-context)) |

## Registering Tools

### With an MCP server

```csharp
var app = builder.Build();
app.MapMcpServer("/mcp");

var tools = app.Services.GetRequiredService<ToolRegistry>();
tools.Register(new WeatherTools());
```

### Direct registration (without MCP)

```csharp
var registry = new ToolRegistry();
registry.Register(new WeatherTools());
```

## Supported Return Types

Tools can return any type that can be serialised to JSON:

```csharp
// String
public Task<string> GetName() => Task.FromResult("Alice");

// Object (serialised to JSON)
public Task<WeatherReport> GetWeather(string city) => ...;

// Collections
public Task<string[]> GetCities() => ...;

// Void (returns confirmation message)
public async Task SaveData(string value) { ... }
```

## Built-in Tools

### `EmbeddingTools`

Provides semantic embedding and similarity comparison tools to the model:

| Tool | Description |
|------|-------------|
| `embed` | Generate an embedding vector for a text string |
| `compare_similarity` | Cosine similarity between a reference text and a list of others |

```csharp
toolRegistry.Register(new EmbeddingTools(lm));
```

## Example: Multi-tool class

```csharp
public class DatabaseTools : IAgentToolSet
{
    private readonly AppDbContext _db;

    public DatabaseTools(AppDbContext db) => _db = db;

    [Tool, Description("Find a customer by name.")]
    public async Task<string> FindCustomer(
        [ToolParam("Customer full name")] string name)
    {
        var customer = await _db.Customers
            .FirstOrDefaultAsync(c => c.Name == name);
        return customer is null
            ? $"No customer found with name '{name}'."
            : $"Found: {customer.Name}, ID: {customer.Id}";
    }

    [Tool, Description("Get all orders for a customer ID.")]
    public async Task<string> GetOrders(
        [ToolParam("Customer ID")] int customerId)
    {
        var orders = await _db.Orders
            .Where(o => o.CustomerId == customerId)
            .ToListAsync();
        return orders.Count == 0
            ? "No orders found."
            : string.Join("\n", orders.Select(o => $"Order #{o.Id}: {o.Total:C}"));
    }
}
```
