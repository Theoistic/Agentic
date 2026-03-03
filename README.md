# Agentic

A lightweight .NET library for building LLM-powered agents with streaming chat, MCP tool hosting, context compaction, and vector storage — all via a clean, attribute-driven API.

[![NuGet](https://img.shields.io/nuget/v/Agentic.svg)](https://www.nuget.org/packages/Agentic)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

---

## Features

- **LM client** — OpenAI-compatible REST client (`/v1/responses`) with streaming, embeddings, vision and health-check
- **Agent** — multi-turn streaming agent with automatic MCP tool orchestration
- **Tool system** — define tools with `[Tool]` / `[ToolParam]` attributes; zero boilerplate
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