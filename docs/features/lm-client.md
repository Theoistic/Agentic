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

The `LM` class is the entry point for all communication with a language model server. It targets any OpenAI-compatible REST API (`/v1/responses`) and supports streaming, embeddings, vision input, and health checks.

## Configuration

```csharp
using Agentic;

var lm = new LM(new LMConfig
{
    Endpoint       = "http://localhost:1234",   // LM Studio or any OpenAI-compatible server
    ModelName      = "your-model-name",
    EmbeddingModel = "your-embedding-model",    // optional — needed for vector storage
});
```

## LMConfig Reference

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Endpoint` | `string` | required | Base URL of the OpenAI-compatible server |
| `ModelName` | `string` | required | Model identifier to use for chat/completions |
| `EmbeddingModel` | `string?` | `null` | Model identifier for embedding requests |
| `Reasoning` | `ReasoningEffort?` | `null` | Global reasoning effort default (see [Reasoning Control](reasoning-control)) |
| `Inference` | `InferenceConfig?` | `null` | Global sampling parameter defaults (see [Inference Config](inference-config)) |

## Compatible Providers

- [LM Studio](https://lmstudio.ai/) — local models via OpenAI-compatible API
- [Ollama](https://ollama.com/) — local models (use `/v1` endpoint)
- [OpenAI](https://platform.openai.com/) — `https://api.openai.com`
- Any server implementing the `/v1/responses` OpenAI REST API

## Direct API Calls

You can call the LM directly without an agent:

### Text response

```csharp
var result = await lm.RespondAsync(
    [ResponseInput.User("What is the capital of France?")]);

Console.WriteLine(result.OutputText);
```

### Streaming response

```csharp
await foreach (var chunk in lm.StreamAsync(
    [ResponseInput.User("Tell me a story about a dragon.")]))
{
    Console.Write(chunk);
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
bool isHealthy = await lm.HealthCheckAsync();
```

## Model Selection

You can override the model at the agent or request level without changing `LMConfig`:

```csharp
// Per-agent model
var agent = new Agent(lm, new AgentOptions
{
    Model = "gpt-4o",
});

// Per-request model override
var response = await agent.ChatStreamAsync(
    "Summarise this document.",
    model: "gpt-4o-mini");
```

See [Model Selection](agent#model-selection) for the full precedence rules.
