---
title: Inference Config
parent: Features
nav_order: 11
---

# Inference Config
{: .no_toc }

## Table of contents
{: .no_toc .text-delta }

1. TOC
{:toc}

---

`InferenceConfig` groups sampling and penalty parameters that control how the model generates text. It follows the same three-level override pattern as [Reasoning Control](reasoning-control). Only fields that are non-`null` are forwarded to the server — unset fields fall through to the next level.

## Parameters Reference

| Field | Type | Description |
|-------|------|-------------|
| `Temperature` | `double?` | Sampling temperature. Higher values produce more varied output |
| `TopP` | `double?` | Top-p (nucleus) sampling cutoff |
| `TopK` | `int?` | Top-k — limits the candidate token pool |
| `MinP` | `double?` | Minimum probability threshold for token selection |
| `PresencePenalty` | `double?` | Penalises tokens that have already appeared in the output |
| `RepetitionPenalty` | `double?` | Multiplier applied to logits of previously-seen tokens |

## Level 1 — Global default (LMConfig)

```csharp
var lm = new OpenAIBackend(new LMConfig
{
    Endpoint  = "http://localhost:1234",
    ModelName = "your-model-name",
    Inference = new InferenceConfig
    {
        Temperature       = 0.7,
        TopP              = 0.8,
        TopK              = 20,
        MinP              = 0.0,
        PresencePenalty   = 1.5,
        RepetitionPenalty = 1.0,
    },
});
```

## Level 2 — Per-agent default (AgentOptions)

```csharp
var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a creative writer.",
    Inference    = new InferenceConfig
    {
        Temperature       = 1.2,
        RepetitionPenalty = 1.1,
    },
});
```

## Level 3 — Per-request override

Pass `inference:` to any `Run*` / `Chat*` call:

```csharp
// Deterministic output for structured extraction
var result = await agent.ChatStreamAsync(
    "Extract the invoice number from this text.",
    inference: new InferenceConfig { Temperature = 0.0 });

// Creative variation for brainstorming
var result = await agent.ChatStreamAsync(
    "Give me 10 creative names for my product.",
    inference: new InferenceConfig { Temperature = 1.4, TopP = 0.95 });
```

## Precedence

```
per-request inference:
    → AgentOptions.Inference (field-by-field merge)
        → LMConfig.Inference (field-by-field merge)
            → not sent (model default)
```

> Fields are merged individually. For example, if `LMConfig` sets `Temperature = 0.7` and `AgentOptions` sets only `RepetitionPenalty = 1.1`, the effective config uses `Temperature = 0.7` and `RepetitionPenalty = 1.1`.

## Recommended Presets

### Deterministic / structured extraction

```csharp
new InferenceConfig { Temperature = 0.0, TopP = 1.0 }
```

### Balanced (general purpose)

```csharp
new InferenceConfig { Temperature = 0.7, TopP = 0.9 }
```

### Creative writing

```csharp
new InferenceConfig { Temperature = 1.2, TopP = 0.95, RepetitionPenalty = 1.1 }
```

### Code generation

```csharp
new InferenceConfig { Temperature = 0.2, TopP = 0.95, TopK = 40 }
```
