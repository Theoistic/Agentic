---
title: Backend Router
parent: Features
nav_order: 13
---

# BackendRouter
{: .no_toc }

## Table of contents
{: .no_toc .text-delta }

1. TOC
{:toc}

---

`BackendRouter` composes multiple [`ILLMBackend`](lm-client#illmbackend) instances into a single backend and routes each call to the right one. The most common use-case is pairing a large chat model with a small specialised embedding model, but any number of named backends can be registered.

---

## How routing works

| Call type | Backend used |
|-----------|-------------|
| `RespondAsync(model: "name")` | The backend registered under `"name"` |
| `RespondAsync(model: null)` | The **default** backend |
| `RespondStreamingAsync(...)` | Same rules as `RespondAsync` |
| `EmbedAsync(...)` | The **embedding** backend; falls back to the default if none is designated |
| `EmbedBatchAsync(...)` | Same as `EmbedAsync` |
| `PingAsync()` | Pings **all** registered backends; returns `true` only when every one succeeds |

---

## Registering backends

Use the fluent `Add` method to build the router. Calls can be chained.

```csharp
var lm = new BackendRouter()
    .Add("qwen-9b",    chatBackend,  isDefault: true)
    .Add("embed-300m", embedBackend, isEmbedding: true);
```

### `Add` parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `name` | *(required)* | Routing key. Pass as the `model` argument to `RespondAsync` / `RespondStreamingAsync` to target this backend explicitly |
| `backend` | *(required)* | The `ILLMBackend` to register |
| `isDefault` | `false` | This backend handles requests where `model` is `null`. The **first non-embedding** backend is auto-designated default; pass `isDefault: true` to override |
| `isEmbedding` | `false` | This backend handles `EmbedAsync` / `EmbedBatchAsync` |

---

## Basic example — chat + embedding

The most common pattern: a large model for reasoning, a small model for vector embeddings.

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
    .Add("qwen-9b",    chatBackend,  isDefault: true)
    .Add("embed-300m", embedBackend, isEmbedding: true);

// Chat calls go to qwen-9b
var agent = new Agent(lm, new AgentOptions { SystemPrompt = "You are a helpful assistant." });
await agent.ChatStreamAsync("Explain quantum entanglement.");

// Embedding calls go to embed-300m
var vector = await lm.EmbedAsync("Hello, world!");
```

---

## Multi-model example — several chat backends

Register as many backends as needed and select between them at call time.

```csharp
await using var lm = new BackendRouter()
    .Add("fast",      fastBackend,      isDefault: true)   // quick answers, small model
    .Add("smart",     smartBackend)                        // complex reasoning
    .Add("ocr",       ocrBackend)                          // vision-heavy tasks
    .Add("embed-300m", embedBackend, isEmbedding: true);

// Default — routes to "fast"
await agent.ChatStreamAsync("What time is it?");

// Explicit routing via the model parameter
await agent.ChatStreamAsync("Refactor this entire module.", model: "smart");
await agent.ChatStreamAsync("Extract text from this receipt.", model: "ocr");

// Embeddings always go to "embed-300m"
var vector = await lm.EmbedAsync("search query");
```

The `model` string is matched **case-insensitively** against registered names. Passing an unrecognised name throws `InvalidOperationException` with the list of valid names.

---

## Disposal

`BackendRouter` disposes all registered backends when it is disposed. If backends are also wrapped in `await using` at the call site, disposal is safe — `NativeBackend` is idempotent.

```csharp
// Both patterns are safe
await using var chatBackend  = new NativeBackend(chatOptions,  LlamaBackend.Cuda);
await using var embedBackend = new NativeBackend(embedOptions, LlamaBackend.Cuda);
await using var lm = new BackendRouter()
    .Add("chat",  chatBackend,  isDefault: true)
    .Add("embed", embedBackend, isEmbedding: true);
```

---

## Using with `OpenAIBackend`

`BackendRouter` works with any `ILLMBackend` — mix local and remote backends freely.

```csharp
var remoteBackend = new OpenAIBackend(new LMConfig
{
    Endpoint  = "https://api.openai.com",
    ModelName = "gpt-4o",
    ApiKey    = "sk-...",
});

await using var localEmbed = new NativeBackend(embedOptions, LlamaBackend.Cpu);

await using var lm = new BackendRouter()
    .Add("gpt-4o", remoteBackend,  isDefault: true)
    .Add("embed",  localEmbed, isEmbedding: true);
```
