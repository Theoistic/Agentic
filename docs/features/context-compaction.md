---
title: Context Compaction
parent: Features
nav_order: 5
---

# Context Compaction
{: .no_toc }

## Table of contents
{: .no_toc .text-delta }

1. TOC
{:toc}

---

Long conversations eventually exceed the model's context window. Context compaction automatically summarises older turns into a structured checkpoint and continues from there — preserving important information without hitting the token limit.

## Setup

```csharp
var agent = new Agent(lm, new AgentOptions
{
    Compaction = new CompactionOptions
    {
        MaxContextTokens    = 128_000,
        CompactionThreshold = 0.85,      // compact at 85% usage
        DefaultLevel        = CompactionLevel.Standard,
        HotTailTurns        = 4,         // keep the last 4 user turns verbatim
        AutoCompact         = true,      // compact automatically when threshold is reached
    },
});
```

## CompactionOptions Reference

| Property | Default | Description |
|----------|---------|-------------|
| `MaxContextTokens` | required | Total token budget for the context window |
| `CompactionThreshold` | `0.85` | Fraction of `MaxContextTokens` that triggers auto-compaction |
| `DefaultLevel` | `Standard` | Default summarisation depth |
| `HotTailTurns` | `4` | How many recent user turns to keep verbatim after compaction |
| `AutoCompact` | `true` | Automatically compact when the threshold is reached |

## Compaction Levels

| Level | What is preserved |
|-------|------------------|
| `Light` | Goals and next actions only — minimal tokens |
| `Standard` | Goals, decisions, status, and next steps |
| `Detailed` | Full nuance — decisions, rationale, edge cases, and key outputs |

Choose `Light` for long exploratory chats where only the final goal matters.  
Choose `Detailed` for technical or business-critical work where reasoning must be preserved.

## Manual Compaction

You can trigger compaction at any time, regardless of the threshold:

```csharp
var checkpoint = await agent.CompactAsync(CompactionLevel.Detailed);
Console.WriteLine(checkpoint.Summary);
```

## Resetting Without Compaction

To start fresh without compacting:

```csharp
agent.ResetConversation();
```

This clears the full history. Any information not yet compacted is lost.

## How It Works

When the estimated token count exceeds `MaxContextTokens × CompactionThreshold`:

1. The agent pauses before the next request
2. It sends a summarisation prompt to the model, asking it to produce a structured checkpoint
3. The checkpoint replaces the older turns in the history
4. The last `HotTailTurns` user messages are kept verbatim so the model has immediate context
5. The conversation continues from the checkpoint

The checkpoint is formatted as a structured document, not a flat summary, so key facts (decisions, outputs, next steps) are clearly separated and easy for the model to parse.

## Example: Long Research Session

```csharp
var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a research assistant.",
    Compaction = new CompactionOptions
    {
        MaxContextTokens    = 32_000,
        CompactionThreshold = 0.80,
        DefaultLevel        = CompactionLevel.Detailed,
        HotTailTurns        = 6,
        AutoCompact         = true,
    },
    OnEvent = e =>
    {
        if (e.Kind == AgentEventKind.TextDelta)
            Console.Write(e.Text);
    },
});

// This can run for many turns without hitting the context limit
while (true)
{
    Console.Write("\nYou: ");
    var input = Console.ReadLine();
    if (string.IsNullOrEmpty(input)) break;
    await agent.ChatStreamAsync(input);
    Console.WriteLine();
}
```
