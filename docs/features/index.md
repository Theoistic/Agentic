---
title: Features
nav_order: 3
has_children: true
---

# Features

Agentic is built around a set of composable features. Each feature is independent and can be used on its own or combined with others.

| Feature | Description |
|---------|-------------|
| [`ILLMBackend`](lm-client#illmbackend) | Unified abstraction over any inference source; swap backends without touching agent code |
| [`OpenAIBackend`](lm-client) | OpenAI-compatible REST client with streaming, embeddings, vision and health-check |
| [`NativeBackend`](native-backend) | Local llama.cpp inference with auto-install from GitHub releases |
| [`BackendRouter`](backend-router) | Compose multiple backends; route chat by model name, embeddings to a dedicated model |
| [`LlamaRuntimeInstaller`](native-backend#llamaruntimeinstaller) | On-demand runtime installer for CPU, CUDA and Vulkan on Windows and Linux |
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
