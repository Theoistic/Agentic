---
title: Reasoning Control
parent: Features
nav_order: 10
---

# Reasoning Control
{: .no_toc }

## Table of contents
{: .no_toc .text-delta }

1. TOC
{:toc}

---

For models that support chain-of-thought reasoning (e.g. Qwen3), Agentic lets you control the reasoning effort at three levels. Each level overrides the one above it.

## Reasoning Effort Levels

| Value | Behaviour |
|-------|-----------|
| `None` | Disables chain-of-thought thinking (`enable_thinking=false`) |
| `Low` | Enables thinking with low effort |
| `Medium` | Enables thinking with medium effort |
| `High` | Enables thinking with high effort (slower, more thorough) |

## Level 1 — Global default (LMConfig)

Applies to every request made through this `OpenAIBackend` instance unless overridden.

```csharp
var lm = new OpenAIBackend(new LMConfig
{
    Endpoint  = "http://localhost:1234",
    ModelName = "Qwen/Qwen3-30B-A3B",
    Reasoning = ReasoningEffort.None,   // thinking off by default
});
```

## Level 2 — Per-agent default (AgentOptions)

Overrides the global `LMConfig.Reasoning` for this agent only.

```csharp
var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a helpful assistant.",
    Reasoning    = ReasoningEffort.High,   // override: high reasoning for this agent
});
```

## Level 3 — Per-request override

Pass `reasoning:` to any `Run*` / `Chat*` call. This takes precedence over both the agent and global defaults.

```csharp
// Reasoning off for a quick question
var response = await agent.ChatStreamAsync(
    "Quick question — what is 2 + 2?",
    reasoning: ReasoningEffort.None);

// High effort for a complex analysis
var response = await agent.ChatStreamAsync(
    "Analyse the trade-offs of this architecture design.",
    reasoning: ReasoningEffort.High);
```

## Precedence

```
per-request reasoning:
    → AgentOptions.Reasoning
        → LMConfig.Reasoning
            → not sent (model default)
```

> The highest level that is set wins. If none are set, no reasoning parameter is sent to the model and the model uses its default behaviour.

## Choosing the Right Level

| Scenario | Recommended Level |
|----------|------------------|
| Simple lookups, data extraction | `None` |
| General Q&A, summarisation | `Low` or not set |
| Code generation, analysis | `Medium` |
| Architecture decisions, complex reasoning | `High` |
| Cost-sensitive, high-volume workloads | `None` |

## Example: Mixed Reasoning in One Agent

```csharp
var agent = new Agent(lm, new AgentOptions
{
    Reasoning = ReasoningEffort.None,   // default: off for speed
});

// Quick data extraction — uses the agent default (None)
var data = await agent.ChatStreamAsync("Extract the date from: Invoice date: 2024-01-15");

// Complex analysis — override to High for this specific request
var analysis = await agent.ChatStreamAsync(
    "Now analyse the payment terms and flag any unusual clauses.",
    reasoning: ReasoningEffort.High);
```
