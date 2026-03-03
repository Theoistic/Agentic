# Agentic

A lightweight .NET library for building LLM-powered agents with streaming chat, MCP tool hosting, context compaction, and vector storage — all via a clean, attribute-driven API.

[![NuGet](https://img.shields.io/nuget/v/Agentic.svg)](https://www.nuget.org/packages/Agentic)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

---

## Features

- **LM client** — OpenAI-compatible REST client (`/v1/responses`) with streaming, embeddings, vision and health-check
- **Agent** — multi-turn streaming agent with automatic MCP tool orchestration
- **Workflows** — ordered multi-step execution with per-step async guardrails and automatic retry
- **Tool system** — define tools with `[Tool]` / `[ToolParam]` attributes; zero boilerplate
- **Tool context** — HTTP headers from MCP requests are automatically forwarded to tool methods via `ToolContext`
- **MCP server** — expose any `IAgentToolSet` over HTTP as a Model Context Protocol server in one line
- **Context compaction** — automatically summarise older conversation history into a structured checkpoint before the context window fills up
- **Vector storage** — `IStore` / `ICollection<T>` with SQLite (default) or PostgreSQL + pgvector backends

---

## Installation

```
dotnet add package Agentic
```

> **Requirements:** .NET 10 · ASP.NET Core (included via `Microsoft.AspNetCore.App` framework reference)

---

## Quick Start

### 1 — Create an LM client

```csharp
using Agentic;

var lm = new LM(new LMConfig
{
    Endpoint       = "http://localhost:1234",   // LM Studio or any OpenAI-compatible server
    ModelName      = "your-model-name",
    EmbeddingModel = "your-embedding-model",    // optional
});
```

### 2 — Chat with the agent

```csharp
var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a helpful assistant.",
    OnEvent      = e =>
    {
        if (e.Kind == AgentEventKind.TextDelta)
            Console.Write(e.Text);
    },
});

// Single-turn (no history)
var response = await agent.RunAsync("Hello!", mcpServerUrl: "http://localhost:5100/mcp");

// Multi-turn streaming (maintains conversation history)
await agent.ChatStreamAsync("What did I just say?", mcpServerUrl: "http://localhost:5100/mcp");
```

### 3 — Define tools

```csharp
using System.ComponentModel;

public class WeatherTools : IAgentToolSet
{
    [Tool, Description("Get the current weather for a city.")]
    public Task<string> GetWeather(
        [ToolParam("City name")] string city,
        [ToolParam("Unit: celsius or fahrenheit")] string unit = "celsius")
    {
        return Task.FromResult($"The weather in {city} is 22 °{(unit == "fahrenheit" ? "F" : "C")} and sunny.");
    }
}
```

### 4 — Host an MCP server

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAgenticMcp(opt =>
{
    opt.ApiKey          = "my-secret-key";   // optional Bearer-token auth
    opt.ToolCallTimeout = TimeSpan.FromSeconds(55);
});

var app = builder.Build();
app.MapMcpServer("/mcp");

var tools = app.Services.GetRequiredService<ToolRegistry>();
tools.Register(new WeatherTools());

await app.RunAsync();
```

The MCP server exposes all registered tools over SSE + JSON-RPC so any MCP-compatible client (LM Studio, Claude Desktop, …) can call them.

---

## Context Compaction

When the conversation approaches the model's context limit, Agentic can automatically compress older turns into a structured checkpoint and continue from there:

```csharp
var agent = new Agent(lm, new AgentOptions
{
    Compaction = new CompactionOptions
    {
        MaxContextTokens    = 128_000,
        CompactionThreshold = 0.85,      // compact at 85 % usage
        DefaultLevel        = CompactionLevel.Standard,
        HotTailTurns        = 4,         // keep the last 4 user turns verbatim
        AutoCompact         = true,
    },
});

// Manual compaction
var checkpoint = await agent.CompactAsync(CompactionLevel.Detailed);

// Reset conversation while keeping no history
agent.ResetConversation();
```

Compaction levels:

| Level | What is kept |
|-------|-------------|
| `Light` | Goals + next actions only |
| `Standard` | Goals, decisions, status, next steps |
| `Detailed` | Full nuance — decisions, rationale, edge cases, key outputs |

---

## Vector Storage

```csharp
// SQLite (default)
services.AddStore();

// PostgreSQL + pgvector
// appsettings.json:  "Database": { "Provider": "postgres", "ConnectionString": "Host=..." }
services.AddStore(configuration);

// In-memory (testing)
services.AddInMemoryStore();
```

Work with typed collections:

```csharp
var store      = services.BuildServiceProvider().GetRequiredService<IStore>();
var articles   = store.Collection<Article>("articles");

var id = await articles.InsertAsync(new Article { Title = "Hello" }, embedding: vector);
await articles.UpsertAsync(id, new Article { Title = "Updated" });

var results = await articles.SearchAsync(queryVector, topK: 5);
foreach (var r in results)
    Console.WriteLine($"{r.Score:F4}  {r.Document.Title}");
```

---

## MCP Server Options

| Property | Default | Description |
|----------|---------|-------------|
| `ApiKey` | `null` | Bearer token required on every request (`null` = open) |
| `AllowedOrigins` | `null` | CORS origin allowlist for browser clients |
| `ToolCallTimeout` | `55 s` | Cancels tool calls that exceed this duration and returns a clean error |
| `ServerName` | `"Agentic"` | Reported to MCP clients during `initialize` |
| `ProtocolVersion` | `"2025-03-26"` | MCP protocol version advertised |

---

## AgentOptions

| Property | Default | Description |
|----------|---------|-------------|
| `SystemPrompt` | `null` | Instruction text prepended to every request |
| `Temperature` | `0` | Sampling temperature |
| `OnEvent` | `null` | Callback fired for every `AgentEvent` |
| `Compaction` | `null` | Enable context compaction (see above) |
| `Thinking` | `null` | Per-agent thinking default (see [Thinking Control](#thinking-control)) |

---

## Thinking Control

For models that support chain-of-thought reasoning (e.g. Qwen3), you can toggle thinking on or off at three levels. Each level overrides the one above it.

### 1 — Global default (LMConfig)

Applies to every request made through this `LM` instance unless overridden.

```csharp
var lm = new LM(new LMConfig
{
    Endpoint  = "http://localhost:1234",
    ModelName = "Qwen/Qwen3-30B-A3B",
    Thinking  = new ThinkingConfig { Enabled = false },  // thinking off by default
});
```

### 2 — Per-agent default (AgentOptions)

Overrides the global `LMConfig.Thinking` for this agent only.

```csharp
var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a helpful assistant.",
    Thinking     = new ThinkingConfig { Enabled = true },  // override: thinking on for this agent
});
```

### 3 — Per-request override

Pass `thinking:` directly to any `Run*` / `Chat*` call. This takes precedence over both the agent and global defaults.

```csharp
// Thinking explicitly off for this one call
var response = await agent.ChatStreamAsync(
    "Quick question — what is 2 + 2?",
    mcpServerUrl: "http://localhost:5100/mcp",
    thinking: new ThinkingConfig { Enabled = false });

// Thinking explicitly on for this one call
var response = await agent.ChatStreamAsync(
    "Analyse the trade-offs of this architecture design.",
    mcpServerUrl: "http://localhost:5100/mcp",
    thinking: new ThinkingConfig { Enabled = true });
```

> **Precedence:** per-request → per-agent (`AgentOptions.Thinking`) → global (`LMConfig.Thinking`) → not sent (model default).

---

## Workflows

A `Workflow` is an ordered sequence of named steps that the agent executes in turn. Each step carries an instruction for the model and an optional **verification callback** — a guardrail that must return `true` before the agent advances to the next step. If a step's guard fails, the agent is prompted to continue with the remaining steps until every guard passes or `maxRounds` is exhausted.

### Defining a workflow

```csharp
var workflow = new Workflow("Process Invoice")
    .Step(
        name:        "Extract header",
        instruction: "Extract the invoice number, date, vendor name, currency, and total amount.",
        verify:      ctx => ctx.ToolInvocations.Any(t => t.ToolName == "save_invoice_header"))

    .Step(
        name:        "Extract line items",
        instruction: "Extract every line item into the database.",
        verify:      ctx => ctx.ToolInvocations.Any(t => t.ToolName == "save_invoice_lines"))

    .Step(
        name:        "Confirm totals",
        instruction: "Sum the line item values and confirm they match the invoice total.",
        verify:      ctx => ctx.ResponseText.Contains("match", StringComparison.OrdinalIgnoreCase));
```

Steps are verified **in order** — the agent cannot skip ahead. If a guard fails for step N, verification stops there even if later steps would have passed.

### Running a workflow

```csharp
var result = await agent.RunWorkflowAsync(
    workflow,
    input:        "Process the attached invoice.",
    mcpServerUrl: "http://localhost:5100/mcp",
    maxRounds:    10);

if (result.Completed)
    Console.WriteLine("All steps completed.");
else
{
    var pending = result.Steps.Where(s => s.Status == WorkflowStepStatus.Pending);
    Console.WriteLine($"Incomplete steps: {string.Join(", ", pending.Select(s => s.Name))}");
}
```

### WorkflowStep reference

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Short label used in prompts and `StepCompleted` events |
| `Instruction` | `string` | Text appended to the system prompt describing what the model must do |
| `Verify` | `Func<WorkflowContext, Task<bool>>?` | Async guardrail; `null` means the step auto-completes after each round |
| `Status` | `WorkflowStepStatus` | `Pending` or `Completed` — updated by the agent after each round |

### WorkflowContext (inside a verify callback)

| Property | Description |
|----------|-------------|
| `ToolInvocations` | All tool calls made so far in this workflow run |
| `ResponseText` | Accumulated model output across all rounds |

### WorkflowResult

| Property | Description |
|----------|-------------|
| `Completed` | `true` when every step reached `Completed` |
| `Text` | Combined model response text from all rounds |
| `Steps` | Final state of each step |
| `ToolInvocations` | All tool calls made during the run, in order |

### Guardrail patterns

```csharp
// Tool was called at least once
verify: ctx => ctx.ToolInvocations.Any(t => t.ToolName == "save_data")

// Tool was called with a specific argument value
verify: ctx => ctx.ToolInvocations
    .Any(t => t.ToolName == "save_data" && t.Arguments.Contains("\"status\":\"ok\""))

// Model confirmed something in its output
verify: ctx => ctx.ResponseText.Contains("confirmed", StringComparison.OrdinalIgnoreCase)

// Async database check
verify: async ctx =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    return await db.Invoices.AnyAsync(i => i.Id == invoiceId && i.LineItems.Count > 0);
}
```

---

## Tool Context

When an MCP request arrives, Agentic automatically captures all HTTP headers and makes them available to tool methods via `ToolContext`. This lets tools read authentication tokens, tenant IDs, correlation headers, or any other request metadata — without `IHttpContextAccessor`.

### Declaring `ToolContext` on a tool method

Add a `ToolContext` parameter to any `[Tool]` method. The framework injects it automatically (just like `CancellationToken`). It is invisible to the model — `ToolContext` never appears in the JSON schema sent to the LLM.

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

### How headers flow

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

Every header on the inbound HTTP request is captured into a **case-insensitive** dictionary. Standard headers (`Authorization`, `Content-Type`, …) and custom headers (`X-Tenant-Id`, `X-Correlation-Id`, …) are all available.

### Sending custom headers from a client

Any HTTP client calling the MCP endpoint can attach headers that your tools will receive:

```bash
curl -X POST http://localhost:5100/mcp \
  -H "Authorization: Bearer my-key" \
  -H "X-Tenant-Id: acme-corp" \
  -H "X-Declaration-Scope: import" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"save_invoice","arguments":{"invoiceNumber":"INV-001","vendor":"Acme","total":1500}}}'
```

Inside the tool, `context.GetHeader("X-Tenant-Id")` returns `"acme-corp"`.

### ToolContext API reference

| Member | Type | Description |
|--------|------|-------------|
| `Headers` | `IReadOnlyDictionary<string, string>` | All HTTP headers from the MCP request (case-insensitive keys) |
| `Properties` | `IReadOnlyDictionary<string, object?>` | Arbitrary key-value data set by the caller |
| `GetHeader(name)` | `string?` | Convenience — returns the header value or `null` |
| `Get<T>(key)` | `T?` | Typed lookup into `Properties`; returns `default` if absent or wrong type |
| `Empty` | `ToolContext` *(static)* | Singleton with no headers or properties |

### Scope guardrail pattern

A common use-case is enforcing business scope rules inside tools. For example, preventing cross-tenant writes or restricting operations to a declared customs scope:

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

---

## Built-in Tools

### `EmbeddingTools`

| Tool | Description |
|------|-------------|
| `embed` | Generate an embedding vector for a text string |
| `compare_similarity` | Cosine similarity between a reference text and a list of others |

Register with:

```csharp
toolRegistry.Register(new EmbeddingTools(lm));
```

---

## License

MIT — see [LICENSE](LICENSE).