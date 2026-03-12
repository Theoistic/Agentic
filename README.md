# Agentic

A lightweight .NET library for building LLM-powered agents with streaming chat, MCP tool hosting, context compaction, and vector storage â€” targeting both remote OpenAI-compatible APIs and local llama.cpp runtimes through a unified `ILLMBackend` abstraction.

[![NuGet](https://img.shields.io/nuget/v/Theoistic.Agentic.svg)](https://www.nuget.org/packages/Theoistic.Agentic)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

---

## Features

- **`ILLMBackend`** â€” unified abstraction over any inference source; swap backends without touching agent code
- **`BackendRouter`** â€” compose multiple backends behind one `ILLMBackend`, with separate routing for chat and embeddings
- **`OpenAIBackend`** â€” OpenAI-compatible REST client (`/v1/responses`) with streaming, embeddings, vision and named model aliases
- **`NativeBackend`** â€” local llama.cpp inference via `Agentic.Runtime`; auto-downloads and installs the right binaries from GitHub on first run
- **`LlamaRuntimeInstaller`** â€” on-demand runtime installer supporting CPU, CUDA and Vulkan backends on Windows and Linux, with optional release pinning
- **Agent** â€” multi-turn streaming agent with automatic MCP tool orchestration
- **Image input** â€” send images alongside text in any agent turn as a URL, local file, or base64 data URL
- **Workflows** â€” ordered multi-step execution with per-step async guardrails and automatic retry
- **Tool system** â€” define tools with `[Tool]` / `[ToolParam]` attributes; zero boilerplate
- **Tool context** â€” HTTP headers from MCP requests forwarded to tool methods via `ToolContext`
- **MCP server** â€” expose any `IAgentToolSet` over HTTP as a Model Context Protocol server in one line
- **Context compaction** â€” automatically summarise older conversation history into a structured checkpoint before the context window fills up
- **Vector storage** â€” `IStore` / `ICollection<T>` with SQLite (default) or PostgreSQL + pgvector backends

---

## Installation

```
dotnet add package Theoistic.Agentic
```

> **Requirements:** .NET 10 Â· ASP.NET Core (included via `Microsoft.AspNetCore.App` framework reference)

---

## Quick Start

### Remote API (OpenAI-compatible server)

```csharp
using Agentic;

// Connect to LM Studio, OpenRouter, or any OpenAI-compatible endpoint
var lm = new OpenAIBackend(new LMConfig
{
    Endpoint  = "http://localhost:1234",
    ModelName = "your-model-name",
});

var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a helpful assistant.",
    OnEvent      = e => { if (e.Kind == AgentEventKind.TextDelta) Console.Write(e.Text); },
});

await agent.ChatStreamAsync("Hello!");
```

### Local (llama.cpp â€” auto-installs runtime)

```csharp
using Agentic;
using Agentic.Runtime.Core;

var sessionOptions = new Mantle.LmSessionOptions
{
    ModelPath = @"/path/to/model.gguf",
    // BackendDirectory is resolved automatically by NativeBackend
};

await using var lm = new NativeBackend(
    sessionOptions,
    backend:         LlamaBackend.Cuda,
    cudaVersion:     "12.4",   // null = pick highest available
    installProgress: new Progress<(string msg, double pct)>(p => Console.Write($"\r[{p.pct:F0}%] {p.msg}")));

var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a helpful assistant.",
    OnEvent      = e => { if (e.Kind == AgentEventKind.TextDelta) Console.Write(e.Text); },
});

await agent.ChatStreamAsync("Hello!");
```

On first run the correct llama.cpp release is downloaded and extracted to `%LOCALAPPDATA%\Agentic\llama-runtime\`. Every subsequent run skips the download entirely.

---

## `ILLMBackend`

The core abstraction. Both `OpenAIBackend` and `NativeBackend` implement it, and `Agent` accepts any implementation â€” you can swap backends or inject mocks without changing any agent code.

```csharp
public interface ILLMBackend
{
    Task<ResponseResponse> RespondAsync(string input, ...);
    Task<ResponseResponse> RespondAsync(IEnumerable<ResponseInput> input, ...);

    IAsyncEnumerable<StreamEvent> RespondStreamingAsync(string input, ...);
    IAsyncEnumerable<StreamEvent> RespondStreamingAsync(IEnumerable<ResponseInput> input, ...);

    Task<float[]>        EmbedAsync(string input, CancellationToken ct = default);
    Task<List<float[]>>  EmbedBatchAsync(IEnumerable<string> inputs, CancellationToken ct = default);

    Task<bool>           PingAsync(CancellationToken ct = default);
}
```

Implement this interface to add your own backend (Ollama, Anthropic proxy, test stub, etc.) and pass it straight to `Agent`.

---

## `BackendRouter`

`BackendRouter` lets you register multiple named `ILLMBackend` instances and expose them as a single backend. Chat requests route to the default backend unless you pass a specific `model` name; embedding requests always route to the backend marked as the embedding backend.

### Chat + embedding example

```csharp
using Agentic;
using Agentic.Runtime.Core;
using Mantle = Agentic.Runtime.Mantle;

var chatOptions = new Mantle.LmSessionOptions
{
    ModelPath     = @"/models/qwen3.5-9b-q4.gguf",
    ContextTokens = 8192,
    MaxToolRounds = 32,
    DefaultRequest = new Mantle.ResponseRequest
    {
        MaxOutputTokens = 1024,
        EnableThinking  = false,
    },
};

var embedOptions = new Mantle.LmSessionOptions
{
    ModelPath     = @"/models/embeddinggemma-300m-qat-q4.gguf",
    ContextTokens = 2048,
    BatchTokens   = 512,
};

await using var chatBackend  = new NativeBackend(chatOptions,  LlamaBackend.Cuda);
await using var embedBackend = new NativeBackend(embedOptions, LlamaBackend.Cuda);

await using var lm = new BackendRouter()
    .Add("chat",  chatBackend,  isDefault: true)
    .Add("embed", embedBackend, isEmbedding: true);

// Chat calls go to the default chat backend
var agent = new Agent(lm, new AgentOptions { SystemPrompt = "You are a helpful assistant." });
await agent.ChatStreamAsync("Summarise this document.");

// Embedding calls go to the embedding backend
var vector = await lm.EmbedAsync("What is the warranty period?");

// You can also route chat explicitly by name when multiple chat backends are registered
var response = await lm.RespondAsync("Explain this code.", model: "chat");
```

### Routing rules

| Call | Backend used |
|------|--------------|
| `RespondAsync(model: "name")` | The backend registered under `name` |
| `RespondAsync(model: null)` | The default chat backend |
| `RespondStreamingAsync(...)` | Same rules as `RespondAsync` |
| `EmbedAsync(...)` | The backend added with `isEmbedding: true` |
| `EmbedBatchAsync(...)` | Same as `EmbedAsync` |

This is useful when you want a larger model for chat and reasoning, but a smaller faster model for embeddings.

---

## `OpenAIBackend`

Connects to any OpenAI-compatible `/v1/responses` endpoint. Supports streaming, embeddings, named model aliases and per-request inference overrides.

```csharp
var lm = new OpenAIBackend(new LMConfig
{
    Endpoint       = "http://localhost:1234",   // LM Studio, OpenRouter, Azure OpenAI, â€¦
    ModelName      = "qwen3.5-4b",             // default model
    ApiKey         = "sk-...",                 // optional Bearer token
    EmbeddingModel = "text-embedding-qwen3-0.6b",
    Models =
    {
        ["advanced"] = "qwen3.5-9b",
        ["ocr"]      = "lightonocr-2-1b",
    },
    Reasoning = ReasoningEffort.None,          // global default; override per-agent or per-call
    Inference = new InferenceConfig { Temperature = 0.7 },
});
```

### `LMConfig` reference

| Property | Default | Description |
|----------|---------|-------------|
| `Endpoint` | `"http://localhost:5454"` | Base URL of the OpenAI-compatible API server |
| `ModelName` | `"liquid/lfm2.5-1.2b"` | Default model identifier sent in every request |
| `ApiKey` | `""` | Bearer token sent in the `Authorization` header |
| `EmbeddingModel` | `null` | Model used for `EmbedAsync` / `EmbedBatchAsync` |
| `Reasoning` | `null` | Global reasoning effort default |
| `Inference` | `null` | Global inference parameter defaults |
| `Models` | `{}` | Named aliases resolved by `ResolveModel(key)` |

---

## `NativeBackend`

Runs inference locally through `Agentic.Runtime` (llama.cpp). The session is initialized lazily on first use. When constructed with a `LlamaBackend`, the runtime is downloaded and installed automatically if not already present.

### Constructor overloads

**Explicit backend directory** â€” you already have the binaries:

```csharp
var sessionOptions = new Mantle.LmSessionOptions
{
    BackendDirectory = @"C:\llama-runtime\cuda-b8269",
    ModelPath        = @"C:\models\qwen.gguf",
    ContextTokens    = 8192,
    MaxToolRounds    = 32,
};

await using var lm = new NativeBackend(sessionOptions);
```

**Auto-install** â€” downloads the runtime on first run:

```csharp
var sessionOptions = new Mantle.LmSessionOptions
{
    ModelPath     = @"C:\models\qwen.gguf",
    ContextTokens = 8192,
    MaxToolRounds = 32,
    // BackendDirectory is omitted â€” resolved automatically
};

await using var lm = new NativeBackend(
    sessionOptions,
    backend:         LlamaBackend.Cuda,
    cudaVersion:     "12.4",       // null = pick highest CUDA 12.x available
    releaseTag:      "b8269",      // null = latest release
    installProgress: new Progress<(string msg, double pct)>(
        p => Console.Write($"\r[{p.pct:F0}%] {p.msg}")));
```

### `NativeBackend` properties

| Member | Description |
|--------|-------------|
| `BackendDirectory` | Resolved path to the native binaries after the session is first initialized; `null` before first use |

### `LmSessionOptions` reference

| Property | Default | Description |
|----------|---------|-------------|
| `BackendDirectory` | `""` | Directory containing llama.cpp binaries. Omit when using auto-install |
| `ModelPath` | *(required)* | Full path to the GGUF model file |
| `ContextTokens` | `8192` | Total KV cache token capacity |
| `ResetContextTokens` | `2048` | Reserved context for context-reset operations |
| `BatchTokens` | `1024` | Prompt evaluation batch size |
| `MicroBatchTokens` | `1024` | Internal llama.cpp micro-batch size |
| `MaxToolRounds` | `10` | Maximum tool-call rounds per turn |
| `Threads` | `null` | CPU thread count (`null` = llama.cpp default) |
| `FlashAttention` | `false` | Enable flash attention when supported |
| `OffloadKvCacheToGpu` | `true` | Offload KV cache to GPU |
| `UseMmap` | `true` | Memory-map model file |

---

## `LlamaRuntimeInstaller`

Downloads and extracts llama.cpp native binaries from [ggml-org/llama.cpp](https://github.com/ggml-org/llama.cpp) GitHub releases. Installed runtimes are cached under `DefaultInstallRoot` and reused on subsequent runs with no network call.

```csharp
using Agentic.Runtime.Core;

// Ensure installed, return the binary directory
string backendDir = await LlamaRuntimeInstaller.EnsureInstalledAsync(
    backend:    LlamaBackend.Cuda,
    cudaVersion: "12.4",      // null = pick the highest CUDA 12.x asset automatically
    releaseTag:  "b8269",     // null = always use the latest published release
    progress:    new Progress<(string msg, double pct)>(p => Console.Write($"\r[{p.pct:F0}%] {p.msg}")));

// Check what is already installed (no download)
string? existing = LlamaRuntimeInstaller.FindInstalled(LlamaBackend.Cuda, releaseTag: "b8269");
```

### `LlamaBackend` enum

| Value | Description |
|-------|-------------|
| `Cpu` | CPU-only inference (AVX2 preferred, falls back to AVX / noavx) |
| `Cuda` | NVIDIA CUDA GPU acceleration |
| `Vulkan` | Vulkan GPU acceleration (AMD / Intel / NVIDIA) |

### Install root

| Platform | Default path |
|----------|-------------|
| Windows | `%LOCALAPPDATA%\Agentic\llama-runtime\` |
| Linux / macOS | `~/.local/share/Agentic/llama-runtime/` |

Each installed release occupies its own subdirectory named `{backend}-{tag}` (e.g. `cuda-b8269`), so multiple versions coexist safely.

### Release pinning

By default the installer always fetches the **latest** published release. To pin to a specific build, pass `releaseTag`:

```csharp
// Always use b8269, regardless of what is latest on GitHub
await LlamaRuntimeInstaller.EnsureInstalledAsync(LlamaBackend.Cuda, releaseTag: "b8269");
```

If `b8269` is already installed the call returns immediately. Pass `forceReinstall: true` to re-download even when the directory exists.

---

## Agent

`Agent` accepts any `ILLMBackend` and exposes four turn styles â€” single-shot or multi-turn, streaming or non-streaming â€” each with an optional images overload.

```csharp
var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a helpful assistant.",
    OnEvent      = e =>
    {
        if (e.Kind == AgentEventKind.TextDelta) Console.Write(e.Text);
    },
});

agent.RegisterTools(new MyTools());
```

### Turn methods

| Method | History | Streaming |
|--------|---------|-----------|
| `RunAsync` | No | No |
| `RunStreamAsync` | No | Yes |
| `ChatAsync` | Yes | No |
| `ChatStreamAsync` | Yes | Yes |

Every method has a `(text, images, ...)` overload for multimodal input. All return `Task<AgentResponse>`.

```csharp
// Single-shot
var response = await agent.RunAsync("Summarise this document.", mcpServerUrl: "http://localhost:5100/mcp");

// Multi-turn (maintains history via previousResponseId chaining)
await agent.ChatStreamAsync("What did I just ask about?");

// Multi-turn with an image
await agent.ChatStreamAsync(
    "What is in this diagram?",
    images:       ["https://example.com/diagram.png"],
    mcpServerUrl: "http://localhost:5100/mcp");

// Reset conversation history
agent.ResetConversation();
```

### `AgentResponse`

| Property | Description |
|----------|-------------|
| `Text` | Final model text for this turn |
| `ToolInvocations` | All tool calls executed during the turn, in order |
| `Usage` | Token usage (`InputTokens`, `OutputTokens`, `TotalTokens`); may be `null` |

### `AgentOptions`

| Property | Default | Description |
|----------|---------|-------------|
| `SystemPrompt` | `null` | Instruction text prepended to every request |
| `OnEvent` | `null` | Callback fired for every `AgentEvent` during a turn |
| `Reasoning` | `null` | Agent-level reasoning effort (see [Reasoning Control](#reasoning-control)) |
| `Inference` | `null` | Agent-level sampling parameters (see [Inference Config](#inference-config)) |
| `Model` | `null` | Agent-level model default (see [Model Selection](#model-selection)) |
| `Logger` | `null` | `ILogger` â€” events are written automatically; no `OnEvent` switch needed |

### `AgentEvent` / `AgentEventKind`

| Kind | When fired |
|------|-----------|
| `UserInput` | User message was received |
| `SystemPrompt` | System prompt dispatched to the model |
| `ToolDeclaration` | An MCP server was declared for this request |
| `Reasoning` | Model emitted a thinking / reasoning chunk |
| `TextDelta` | Streaming text delta arrived |
| `ToolCall` | Model invoked a tool |
| `ToolResult` | Tool returned its result |
| `Answer` | Model produced its final answer |
| `StepCompleted` | A workflow step completed |
| `WorkflowCompleted` | All workflow steps completed |

---

## Tools

### Defining tools

Add `[Tool]` to any public method on an `IAgentToolSet` class. Parameters get `[ToolParam]` descriptions â€” these form the JSON Schema sent to the model.

```csharp
using System.ComponentModel;

public class WeatherTools : IAgentToolSet
{
    [Tool, Description("Get the current weather for a city.")]
    public Task<string> GetWeather(
        [ToolParam("City name")]  string city,
        [ToolParam("Unit: celsius or fahrenheit")] string unit = "celsius")
    {
        return Task.FromResult($"The weather in {city} is 22 Â°{(unit == "fahrenheit" ? "F" : "C")}.");
    }
}
```

`[Tool]` accepts an optional explicit name: `[Tool("get_weather")]`. By default the method name is converted to `snake_case`.

`CancellationToken` and `ToolContext` parameters are injected automatically and never appear in the model's tool schema.

### Registering tools

```csharp
// On an agent
agent.RegisterTools(new WeatherTools());

// On a shared ToolRegistry
toolRegistry.Register(new WeatherTools());
toolRegistry.Register(new WeatherTools(), replaceExisting: true);
```

### `IAgentToolSet` variants

| Interface | Description |
|-----------|-------------|
| `IAgentToolSet` | Standard tool set |
| `IDisposableToolSet` | Tool set with async cleanup (`ValueTask DisposeAsync()`) |

---

## MCP Server

Expose any `IAgentToolSet` over HTTP as a Model Context Protocol (MCP) server. Any MCP-compatible client (LM Studio, Claude Desktop, â€¦) can call the tools.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAgenticMcp(opt =>
{
    opt.ApiKey          = "my-secret-key";         // optional Bearer-token auth
    opt.ToolCallTimeout = TimeSpan.FromSeconds(55);
});

var app = builder.Build();
app.MapMcpServer("/mcp");

var tools = app.Services.GetRequiredService<ToolRegistry>();
tools.Register(new WeatherTools());

await app.RunAsync();
```

### MCP server options

| Property | Default | Description |
|----------|---------|-------------|
| `ApiKey` | `null` | Bearer token required on every request (`null` = open) |
| `AllowedOrigins` | `null` | CORS origin allowlist for browser clients |
| `ToolCallTimeout` | `55 s` | Cancels tool calls that take too long and returns a clean error |
| `ServerName` | `"Agentic"` | Reported to MCP clients during `initialize` |
| `ProtocolVersion` | `"2025-03-26"` | MCP protocol version advertised |

---

## Workflows

A `Workflow` is an ordered sequence of steps the agent executes in turn. Each step has an instruction for the model and an optional **verification callback** that must return `true` before the agent advances.

```csharp
var workflow = new Workflow("Process Invoice")
    .Step(
        name:        "Extract header",
        instruction: "Extract the invoice number, date, vendor name, and total.",
        verify:      ctx => ctx.ToolInvocations.Any(t => t.Name == "save_invoice_header"))
    .Step(
        name:        "Extract line items",
        instruction: "Extract every line item.",
        verify:      ctx => ctx.ToolInvocations.Any(t => t.Name == "save_invoice_lines"))
    .Step(
        name:        "Confirm totals",
        instruction: "Sum the line items and confirm they match the total.",
        verify:      ctx => ctx.ResponseText.Contains("match", StringComparison.OrdinalIgnoreCase));

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
    Console.WriteLine($"Incomplete: {string.Join(", ", pending.Select(s => s.Name))}");
}
```

### `WorkflowContext` (inside a verify callback)

| Property | Description |
|----------|-------------|
| `ToolInvocations` | All tool calls made so far in this workflow run |
| `ResponseText` | Accumulated model output across all rounds |

### `WorkflowResult`

| Property | Description |
|----------|-------------|
| `Completed` | `true` when every step reached `Completed` |
| `Text` | Combined model response text from all rounds |
| `Steps` | Final state of each step |
| `ToolInvocations` | All tool calls made during the run, in order |

### Guardrail patterns

```csharp
// Tool was called
verify: ctx => ctx.ToolInvocations.Any(t => t.Name == "save_data")

// Model confirmed something in its text output
verify: ctx => ctx.ResponseText.Contains("confirmed", StringComparison.OrdinalIgnoreCase)

// Async database check
verify: async ctx =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    return await db.Invoices.AnyAsync(i => i.Id == invoiceId);
}
```

---

## Context Compaction

When a conversation approaches the model's context limit, Agentic can compress older turns into a structured checkpoint and continue seamlessly.

> Context compaction is currently used internally by `Agentic.Runtime` (native sessions). The `ConversationCompactionOptions` type is part of `Agentic.Runtime.Mantle` and is configured via `LmSessionOptions.Compaction`.

```csharp
var sessionOptions = new Mantle.LmSessionOptions
{
    ModelPath  = @"/path/to/model.gguf",
    Compaction = new Mantle.ConversationCompactionOptions(
        MaxInputTokens:        4096,
        ReservedForGeneration: 256),
};
```

---

## Vector Storage

```csharp
// SQLite (default â€” no extra configuration required)
services.AddStore();

// PostgreSQL + pgvector
// appsettings.json:  "Database": { "Provider": "postgres", "ConnectionString": "Host=..." }
services.AddStore(configuration);

// In-memory (testing)
services.AddInMemoryStore();
```

Work with typed collections:

```csharp
var store    = services.BuildServiceProvider().GetRequiredService<IStore>();
var articles = store.Collection<Article>("articles");

var id = await articles.InsertAsync(new Article { Title = "Hello" }, embedding: vector);
await articles.UpsertAsync(id, new Article { Title = "Updated" });

var results = await articles.SearchAsync(queryVector, topK: 5);
foreach (var r in results)
    Console.WriteLine($"{r.Score:F4}  {r.Document.Title}");
```

---

## Reasoning Control

For models that support chain-of-thought reasoning (e.g. Qwen3), control thinking effort at three levels. Each level overrides the one above it.

| Value | Behaviour |
|-------|-----------|
| `None` | Disable thinking (`enable_thinking=false`) |
| `Low` | Enable thinking with low effort |
| `Medium` | Enable thinking with medium effort |
| `High` | Enable thinking with high effort |

```csharp
// 1 â€” Global default on the backend config
var lm = new OpenAIBackend(new LMConfig { ..., Reasoning = ReasoningEffort.None });

// 2 â€” Per-agent default
var agent = new Agent(lm, new AgentOptions { Reasoning = ReasoningEffort.High });

// 3 â€” Per-request override (highest precedence)
await agent.ChatStreamAsync("Complex question...", reasoning: ReasoningEffort.High);
await agent.ChatStreamAsync("Quick question...",   reasoning: ReasoningEffort.None);
```

> **Precedence:** per-request â†’ `AgentOptions.Reasoning` â†’ `LMConfig.Reasoning` â†’ not sent (model default).

---

## Inference Config

Sampling and penalty parameters are grouped in `InferenceConfig` and follow the same three-level precedence. Only non-`null` fields are forwarded to the server.

| Field | Type | Description |
|-------|------|-------------|
| `Temperature` | `double?` | Sampling temperature |
| `TopP` | `double?` | Top-p (nucleus) sampling cutoff |
| `TopK` | `int?` | Top-k token pool limit |
| `MinP` | `double?` | Minimum token probability threshold |
| `PresencePenalty` | `double?` | Penalty for tokens already in the output |
| `RepetitionPenalty` | `double?` | Multiplier on logits of previously-seen tokens |

```csharp
// 1 â€” Global default
var lm = new OpenAIBackend(new LMConfig { ..., Inference = new InferenceConfig { Temperature = 0.7 } });

// 2 â€” Per-agent default
var agent = new Agent(lm, new AgentOptions { Inference = new InferenceConfig { Temperature = 1.2 } });

// 3 â€” Per-request override
await agent.ChatStreamAsync("Write a poem.", inference: new InferenceConfig { Temperature = 1.5 });
```

> **Precedence:** per-request â†’ `AgentOptions.Inference` â†’ `LMConfig.Inference` â†’ not sent (server default).

---

## Model Selection

Define short aliases in `LMConfig.Models` and reference them at the agent or per-call level.

```csharp
var lm = new OpenAIBackend(new LMConfig
{
    Endpoint  = "http://localhost:1234",
    ModelName = "qwen3.5-4b",                    // default
    Models =
    {
        ["advanced"] = "qwen3.5-9b",
        ["ocr"]      = "lightonocr-2-1b",
    },
});
```

```csharp
// Per-agent default
var ocrAgent = new Agent(lm, new AgentOptions { Model = "ocr" });

// Per-request override
await agent.ChatStreamAsync("Analyse this.", model: "advanced");

// Raw model ID (not in the alias map)
await agent.RunAsync("Summarise.", model: "some-other-model-id");
```

> **Precedence:** per-request `model:` â†’ `AgentOptions.Model` â†’ `LMConfig.ModelName`.

---

## Image Input (Vision)

All four agent turn methods have an `images` overload. Images participate in conversation history (`previousResponseId` chaining) normally.

```csharp
// From a URL
await agent.ChatStreamAsync("What is in this image?", images: ["https://example.com/photo.jpg"]);

// From a local file (reads and base64-encodes automatically)
var dataUrl = InputImageContent.FromFile("diagram.png").ImageUrl!;
await agent.ChatStreamAsync("Describe this diagram.", images: [dataUrl]);

// Multiple images
await agent.ChatStreamAsync("Compare these two layouts.", images: [urlA, urlB]);
```

`ResponseInput.User(text, images)` wraps text as `input_text` and each image string as `input_image`.

> For images on private/internal hosts that the LM server cannot reach, pass a `data:image/â€¦;base64,â€¦` data URL. `InputImageContent.FromFile` does this automatically.

---

## Tool Context

When an MCP request arrives, Agentic captures all HTTP headers and makes them available to `[Tool]` methods via `ToolContext`. It is injected automatically â€” the model never sees it in the JSON schema.

```csharp
public class InvoiceTools : IAgentToolSet
{
    [Tool, Description("Save an invoice header to the database.")]
    public async Task<string> SaveInvoice(
        [ToolParam("Invoice number")] string invoiceNumber,
        ToolContext context,
        CancellationToken ct)
    {
        var tenant = context.GetHeader("X-Tenant-Id");
        // ... save using tenant context ...
        return $"Invoice {invoiceNumber} saved (tenant={tenant})";
    }
}
```

### `ToolContext` API

| Member | Type | Description |
|--------|------|-------------|
| `Headers` | `IReadOnlyDictionary<string, string>` | All HTTP headers (case-insensitive keys) |
| `Properties` | `IReadOnlyDictionary<string, object?>` | Arbitrary key-value data |
| `GetHeader(name)` | `string?` | Returns header value or `null` |
| `Get<T>(key)` | `T?` | Typed lookup into `Properties` |
| `Empty` | *(static)* | Singleton with no headers or properties |

---

## Built-in Tools

### `EmbeddingTools`

| Tool | Description |
|------|-------------|
| `embed` | Generate an embedding vector for a text string |
| `compare_similarity` | Cosine similarity between a reference text and a list of candidates |

```csharp
toolRegistry.Register(new EmbeddingTools(lm));
```

---

## License

MIT â€” see [LICENSE](LICENSE).
