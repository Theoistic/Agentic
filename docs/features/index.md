---
title: Features
nav_order: 3
has_children: true
---

# Features

Agentic is built around a set of composable features. Each feature is independent and can be used on its own or combined with others.

| Feature | Description |
|---------|-------------|
| [LM Client](lm-client) | OpenAI-compatible REST client with streaming, embeddings, vision and health-check |
| [Agent](agent) | Multi-turn streaming agent with automatic MCP tool orchestration |
| [Image Input](image-input) | Send images alongside text as URL, local file, or base64 |
| [Workflows](workflows) | Ordered multi-step execution with per-step async guardrails and retry |
| [Tool System](tools) | Define tools with `[Tool]` / `[ToolParam]` attributes; zero boilerplate |
| [Tool Context](tool-context) | HTTP headers forwarded to tool methods via `ToolContext` |
| [MCP Server](mcp-server) | Expose any `IAgentToolSet` over HTTP as a Model Context Protocol server |
| [Context Compaction](context-compaction) | Auto-summarise older history into a structured checkpoint |
| [Vector Storage](vector-storage) | `IStore` / `ICollection<T>` with SQLite or PostgreSQL + pgvector |
| [Reasoning Control](reasoning-control) | Control chain-of-thought effort at global, agent, or request level |
| [Inference Config](inference-config) | Sampling and penalty parameters with three-level override |
