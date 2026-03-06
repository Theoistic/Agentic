---
title: Getting Started
nav_order: 2
---

# Getting Started
{: .no_toc }

## Table of contents
{: .no_toc .text-delta }

1. TOC
{:toc}

---

## Installation

Add the NuGet package to your project:

```
dotnet add package Agentic
```

> **Requirements:** .NET 10 · ASP.NET Core (included via `Microsoft.AspNetCore.App` framework reference)

---

## Step 1 — Create an LM client

The `LM` class is your connection to any OpenAI-compatible language model server.

```csharp
using Agentic;

var lm = new LM(new LMConfig
{
    Endpoint       = "http://localhost:1234",   // LM Studio or any OpenAI-compatible server
    ModelName      = "your-model-name",
    EmbeddingModel = "your-embedding-model",    // optional — needed for vector storage
});
```

Compatible with:
- [LM Studio](https://lmstudio.ai/)
- [Ollama](https://ollama.com/) (via OpenAI-compatible endpoint)
- [OpenAI API](https://platform.openai.com/)
- Any OpenAI-compatible REST API (`/v1/responses`)

---

## Step 2 — Chat with the agent

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
var response = await agent.RunAsync("Hello!");

// Multi-turn streaming (maintains conversation history)
await agent.ChatStreamAsync("What did I just say?");
```

---

## Step 3 — Define tools

Use the `[Tool]` and `[ToolParam]` attributes to expose methods to the model with zero boilerplate:

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
}
```

---

## Step 4 — Host an MCP server

Expose your tools over HTTP so any MCP-compatible client can call them:

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

The MCP server exposes all registered tools over SSE + JSON-RPC so any MCP-compatible client (LM Studio, Claude Desktop, etc.) can call them.

---

## What's Next?

Explore the individual feature documentation or jump straight into examples:

- [LM Client](features/lm-client) — configuration options and direct API calls
- [Agent](features/agent) — multi-turn chat, events, and options
- [Workflows](features/workflows) — multi-step agent pipelines
- [Tool System](features/tools) — building tools with attributes
- [MCP Server](features/mcp-server) — hosting tools over HTTP
- [Context Compaction](features/context-compaction) — managing long conversations
- [Vector Storage](features/vector-storage) — semantic search and embeddings
- [Examples](examples/) — real-world usage patterns
