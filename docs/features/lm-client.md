---
title: LM Client
parent: Features
nav_order: 1
---

# LM Client
{: .no_toc }

## Table of contents
{: .no_toc .text-delta }

1. TOC
{:toc}

---

## `ILLMBackend`

The core abstraction over any inference source. Both `OpenAIBackend` and `NativeBackend` implement it, and `Agent` accepts any `ILLMBackend` — you can swap backends or inject mocks without changing any agent code.

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

Implement this interface to add your own backend (Ollama proxy, Anthropic wrapper, test stub, etc.) and pass it straight to `Agent`.

To combine multiple `ILLMBackend` implementations — for example a large chat model alongside a small embedding model — see [`BackendRouter`](backend-router).

---

## `OpenAIBackend`

`OpenAIBackend` connects to any OpenAI-compatible `/v1/responses` endpoint. It supports streaming, embeddings, named model aliases, and per-request inference overrides.

```csharp
using Agentic;

var lm = new OpenAIBackend(new LMConfig
{
    Endpoint       = "http://localhost:1234",   // LM Studio or any OpenAI-compatible server
    ModelName      = "your-model-name",
    ApiKey         = "sk-...",                  // optional Bearer token
    EmbeddingModel = "your-embedding-model",    // optional — needed for vector storage
    Models =
    {
        ["advanced"] = "qwen3.5-9b",
        ["ocr"]      = "lightonocr-2-1b",
    },
});
```

For local llama.cpp inference see [NativeBackend](native-backend).

## `LMConfig` Reference

| Property | Default | Description |
|----------|---------|-------------|
| `Endpoint` | `"http://localhost:5454"` | Base URL of the OpenAI-compatible API server |
| `ModelName` | `"liquid/lfm2.5-1.2b"` | Default model identifier sent in every request |
| `ApiKey` | `""` | Bearer token sent in the `Authorization` header |
| `EmbeddingModel` | `null` | Model used for `EmbedAsync` / `EmbedBatchAsync` |
| `Reasoning` | `null` | Global reasoning effort default (see [Reasoning Control](reasoning-control)) |
| `Inference` | `null` | Global sampling parameter defaults (see [Inference Config](inference-config)) |
| `Models` | `{}` | Named aliases resolved by `ResolveModel(key)` (see [Model Selection](agent#model-selection)) |

## Compatible Providers

- [LM Studio](https://lmstudio.ai/) — local models via OpenAI-compatible API
- [Ollama](https://ollama.com/) — local models (use `/v1` endpoint)
- [OpenAI](https://platform.openai.com/) — `https://api.openai.com`
- Any server implementing the `/v1/responses` OpenAI REST API

## Direct API Calls

You can call the backend directly without an agent:

### Text response

```csharp
var result = await lm.RespondAsync(
    [ResponseInput.User("What is the capital of France?")]);

Console.WriteLine(result.OutputText);
```

### Streaming response

```csharp
await foreach (var chunk in lm.RespondStreamingAsync(
    [ResponseInput.User("Tell me a story about a dragon.")]))
{
    if (chunk.Delta is not null)
        Console.Write(chunk.Delta);
}
```

### Generate embeddings

```csharp
var vector = await lm.EmbedAsync("Hello, world!");
// vector is a float[] representing the semantic embedding
```

### Vision (image input)

```csharp
var result = await lm.RespondAsync(
    [ResponseInput.User("What is in this image?", ["https://example.com/photo.jpg"])]);
```

See [Image Input](image-input) for full details on vision usage.

### Health check

```csharp
bool isHealthy = await lm.PingAsync();
```

## Model Selection

Define short aliases in `LMConfig.Models` and reference them at the agent or per-call level:

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

// Per-agent default
var ocrAgent = new Agent(lm, new AgentOptions { Model = "ocr" });

// Per-request override
await agent.ChatStreamAsync("Analyse this.", model: "advanced");
```

See [Agent — Model Selection](agent#model-selection) for the full precedence rules.
