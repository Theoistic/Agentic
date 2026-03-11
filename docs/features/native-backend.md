---
title: Native Backend
parent: Features
nav_order: 12
---

# Native Backend
{: .no_toc }

## Table of contents
{: .no_toc .text-delta }

1. TOC
{:toc}

---

`NativeBackend` runs inference locally through `Agentic.Runtime` (llama.cpp). It implements `ILLMBackend` so it is a drop-in replacement for `OpenAIBackend`. The session is initialized lazily on first use, and when constructed with a `LlamaBackend` the runtime binaries are downloaded and installed automatically if not already present.

---

## Quick Start

```csharp
using Agentic;
using Agentic.Runtime.Core;
using Agentic.Runtime.Mantle;

var sessionOptions = new LmSessionOptions
{
    ModelPath    = @"/path/to/model.gguf",
    ToolRegistry = new ToolRegistry(),
    Compaction   = new ConversationCompactionOptions(MaxInputTokens: 4096),
};

await using var lm = new NativeBackend(
    sessionOptions,
    backend:         LlamaBackend.Cuda,
    cudaVersion:     "12.4",
    installProgress: new Progress<(string msg, double pct)>(p => Console.Write($"\r[{p.pct:F0}%] {p.msg}")));

var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a helpful assistant.",
    OnEvent      = e => { if (e.Kind == AgentEventKind.TextDelta) Console.Write(e.Text); },
});

await agent.ChatStreamAsync("Hello!");
```

On first run the correct llama.cpp release is downloaded and extracted. Every subsequent run skips the download entirely.

---

## Constructor Overloads

### Explicit backend directory — you already have the binaries

```csharp
var sessionOptions = new LmSessionOptions
{
    BackendDirectory = @"C:\llama-runtime\cuda-b8269",
    ModelPath        = @"C:\models\qwen.gguf",
    ToolRegistry     = new ToolRegistry(),
    Compaction       = new ConversationCompactionOptions(MaxInputTokens: 4096),
    ContextTokens    = 8192,
    MaxToolRounds    = 32,
};

await using var lm = new NativeBackend(sessionOptions);
```

### Auto-install — downloads the runtime on first run

```csharp
var sessionOptions = new LmSessionOptions
{
    ModelPath     = @"C:\models\qwen.gguf",
    ToolRegistry  = new ToolRegistry(),
    Compaction    = new ConversationCompactionOptions(MaxInputTokens: 4096),
    ContextTokens = 8192,
    MaxToolRounds = 32,
    // BackendDirectory is omitted — resolved automatically
};

await using var lm = new NativeBackend(
    sessionOptions,
    backend:         LlamaBackend.Cuda,
    cudaVersion:     "12.4",       // null = pick highest CUDA 12.x available
    releaseTag:      "b8269",      // null = latest release
    installProgress: new Progress<(string msg, double pct)>(
        p => Console.Write($"\r[{p.pct:F0}%] {p.msg}")));
```

### `NativeBackend` members

| Member | Description |
|--------|-------------|
| `BackendDirectory` | Resolved path to the native binaries after the session is first initialized; `null` before first use |

---

## `LmSessionOptions` Reference

| Property | Default | Description |
|----------|---------|-------------|
| `ModelPath` | *(required)* | Full path to the GGUF model file |
| `ToolRegistry` | *(required)* | Tool registry available to the session |
| `Compaction` | *(required)* | Conversation compaction policy (see [Context Compaction](context-compaction)) |
| `BackendDirectory` | `""` | Directory containing llama.cpp binaries. Omit when using auto-install |
| `ContextTokens` | `8192` | Total KV cache token capacity |
| `ResetContextTokens` | `2048` | Reserved context for context-reset operations |
| `BatchTokens` | `1024` | Prompt evaluation batch size |
| `MicroBatchTokens` | `1024` | Internal llama.cpp micro-batch size |
| `MaxToolRounds` | `10` | Maximum tool-call rounds per turn |
| `Threads` | `null` | CPU thread count (`null` = llama.cpp default) |
| `FlashAttention` | `false` | Enable flash attention when supported |
| `OffloadKvCacheToGpu` | `true` | Offload KV cache to GPU |
| `UseMmap` | `true` | Memory-map the model file |
| `DefaultRequest` | `null` | Default generation settings (temperature, top-p, etc.) for every turn |
| `Logger` | `null` | `ILogger` for session and engine diagnostics |

---

## `LlamaRuntimeInstaller`

`LlamaRuntimeInstaller` downloads and extracts llama.cpp native binaries from [ggml-org/llama.cpp](https://github.com/ggml-org/llama.cpp) GitHub releases. Installed runtimes are cached under `DefaultInstallRoot` and reused on subsequent runs with no network call.

```csharp
using Agentic.Runtime.Core;

// Ensure installed, return the binary directory
string backendDir = await LlamaRuntimeInstaller.EnsureInstalledAsync(
    backend:     LlamaBackend.Cuda,
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

## Full Example with Workflow

```csharp
using Agentic;
using Agentic.Runtime.Core;
using Agentic.Runtime.Mantle;

var sessionOptions = new LmSessionOptions
{
    ModelPath    = @"/models/qwen3.5-9b-q4.gguf",
    ToolRegistry = new ToolRegistry(),
    Compaction   = new ConversationCompactionOptions(
        MaxInputTokens:        8192,
        ReservedForGeneration: 256),
    ContextTokens = 8192,
    MaxToolRounds = 32,
    DefaultRequest = new ResponseRequest
    {
        MaxOutputTokens = 1024,
        EnableThinking  = false,
    },
};

await using var lm = new NativeBackend(
    sessionOptions,
    backend:         LlamaBackend.Cuda,
    cudaVersion:     "12.4",
    installProgress: new Progress<(string msg, double pct)>(
        p => Console.Write($"\r[{p.pct:F0}%] {p.msg}")));

var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a helpful assistant.",
    OnEvent      = e => { if (e.Kind == AgentEventKind.TextDelta) Console.Write(e.Text); },
});

await agent.ChatStreamAsync("Explain the Pythagorean theorem.");
```

---

## Dual-model setup with `BackendRouter`

A common local pattern is to pair a large model for chat and reasoning with a small specialised model for vector embeddings. [`BackendRouter`](backend-router) wires them together as a single `ILLMBackend`:

```csharp
var chatOptions = new Mantle.LmSessionOptions
{
    ModelPath     = @"/models/qwen3.5-9b-q4.gguf",
    ContextTokens = 8192,
    MaxToolRounds = 32,
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
    .Add("qwen-9b",    chatBackend,  isDefault: true)
    .Add("embed-300m", embedBackend, isEmbedding: true);
```

All `RespondAsync` / `RespondStreamingAsync` calls go to the chat model; all `EmbedAsync` / `EmbedBatchAsync` calls go to the embedding model. See [BackendRouter](backend-router) for full routing rules and multi-model examples.
