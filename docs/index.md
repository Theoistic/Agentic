---
title: Home
layout: home
nav_order: 1
---

# Agentic

{: .fs-9 }

A lightweight .NET library for building LLM-powered agents.
{: .fs-6 .fw-300 }

[Get Started](getting-started){: .btn .btn-primary .fs-5 .mb-4 .mb-md-0 .mr-2 }
[View on GitHub](https://github.com/Theoistic/Agentic){: .btn .fs-5 .mb-4 .mb-md-0 }

---

[![NuGet](https://img.shields.io/nuget/v/Theoistic.Agentic.svg)](https://www.nuget.org/packages/Theoistic.Agentic)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/Theoistic/Agentic/blob/main/LICENSE)

---

Agentic is a lightweight .NET library for building LLM-powered agents with streaming chat, MCP tool hosting, context compaction, and vector storage — all via a clean, attribute-driven API.

## Features

| Feature | Description |
|---------|-------------|
| [LM Client](features/lm-client) | OpenAI-compatible REST client with streaming, embeddings, vision and health-check |
| [Agent](features/agent) | Multi-turn streaming agent with automatic MCP tool orchestration |
| [Image Input](features/image-input) | Send images alongside text as URL, local file, or base64 |
| [Workflows](features/workflows) | Ordered multi-step execution with per-step async guardrails and retry |
| [Tool System](features/tools) | Define tools with `[Tool]` / `[ToolParam]` attributes; zero boilerplate |
| [Tool Context](features/tool-context) | HTTP headers forwarded to tool methods via `ToolContext` |
| [MCP Server](features/mcp-server) | Expose any `IAgentToolSet` over HTTP as a Model Context Protocol server |
| [Context Compaction](features/context-compaction) | Auto-summarise older history into a structured checkpoint |
| [Vector Storage](features/vector-storage) | `IStore` / `ICollection<T>` with SQLite or PostgreSQL + pgvector |
| [Reasoning Control](features/reasoning-control) | Control chain-of-thought effort at global, agent, or request level |
| [Inference Config](features/inference-config) | Sampling and penalty parameters with three-level override |

## Installation

```
dotnet add package Agentic
```

> **Requirements:** .NET 10 · ASP.NET Core (included via `Microsoft.AspNetCore.App` framework reference)

## Quick Example

```csharp
using Agentic;

// 1. Create the LM client
var lm = new LM(new LMConfig
{
    Endpoint  = "http://localhost:1234",
    ModelName = "your-model-name",
});

// 2. Create an agent
var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a helpful assistant.",
    OnEvent      = e =>
    {
        if (e.Kind == AgentEventKind.TextDelta)
            Console.Write(e.Text);
    },
});

// 3. Chat
await agent.ChatStreamAsync("Hello! What can you do?");
```

[Read the full Getting Started guide →](getting-started)
