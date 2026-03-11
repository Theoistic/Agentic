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

Long conversations eventually exceed the model's context window. Context compaction automatically trims or summarises older turns so the session can continue seamlessly without hitting the token limit.

> Context compaction is configured via `LmSessionOptions.Compaction` and applies to sessions created through `NativeBackend`. See [NativeBackend](native-backend) for setup details.

## Setup

Pass a `ConversationCompactionOptions` record when constructing `LmSessionOptions`:

```csharp
using Agentic.Runtime.Mantle;

var sessionOptions = new LmSessionOptions
{
    ModelPath    = @"/path/to/model.gguf",
    ToolRegistry = new ToolRegistry(),
    Compaction   = new ConversationCompactionOptions(
        MaxInputTokens:        4096,
        ReservedForGeneration: 256),
};
```

## `ConversationCompactionOptions` Reference

| Parameter | Default | Description |
|-----------|---------|-------------|
| `MaxInputTokens` | *(required)* | Total token budget for the prompt window |
| `ReservedForGeneration` | `512` | Tokens reserved for the model's output; deducted from `MaxInputTokens` to get the effective prompt budget |
| `Strategy` | `PinnedSystemFifo` | How older messages are discarded (see [Strategies](#strategies)) |
| `Level` | `Balanced` | Aggressiveness of compaction (see [Levels](#levels)) |
| `AlwaysKeepSystem` | `true` | Prevent the system prompt from being evicted |
| `HotTrailMessages` | `4` | Minimum number of recent messages always kept verbatim |

The effective prompt budget is `MaxInputTokens - ReservedForGeneration`.

## Strategies

| `ContextCompactionStrategy` | Description |
|-----------------------------|-------------|
| `FifoSlidingWindow` | Drop the oldest messages first (pure FIFO) |
| `PinnedSystemFifo` | Keep the system prompt pinned; drop oldest non-system messages |
| `MiddleOutElision` | Keep the beginning and end of the conversation; elide the middle |
| `HeuristicPruning` | Drop low-signal messages first (tool call results, short assistant turns) |
| `RollingSummarization` | Requires a custom `IConversationCompactor` implementation |
| `VectorAugmentedRecall` | Requires a custom `IConversationCompactor` implementation |

## Levels

| `ConversationCompactionLevel` | What is preserved |
|-------------------------------|------------------|
| `Light` | Minimal retention — fast, lowest token overhead |
| `Balanced` | Balanced retention of context and important turns |
| `Aggressive` | Maximum compression — retains only the most essential turns |

## Custom Compactor

For `RollingSummarization` or `VectorAugmentedRecall`, supply your own `IConversationCompactor`:

```csharp
public class MySummarizingCompactor : IConversationCompactor
{
    public IReadOnlyList<ChatMessage> Compact(
        IReadOnlyList<ChatMessage> messages,
        ConversationCompactionContext context)
    {
        // your summarization logic here
        return messages;
    }
}

var sessionOptions = new LmSessionOptions
{
    ModelPath            = @"/path/to/model.gguf",
    ToolRegistry         = new ToolRegistry(),
    Compaction           = new ConversationCompactionOptions(MaxInputTokens: 8192),
    ConversationCompactor = new MySummarizingCompactor(),
};
```

## Example: Long Research Session

```csharp
using Agentic;
using Agentic.Runtime.Core;
using Agentic.Runtime.Mantle;

var sessionOptions = new LmSessionOptions
{
    ModelPath    = @"/path/to/model.gguf",
    ToolRegistry = new ToolRegistry(),
    Compaction   = new ConversationCompactionOptions(
        MaxInputTokens:        8192,
        ReservedForGeneration: 512,
        Strategy:              ContextCompactionStrategy.PinnedSystemFifo,
        Level:                 ConversationCompactionLevel.Balanced,
        AlwaysKeepSystem:      true,
        HotTrailMessages:      6),
};

await using var lm = new NativeBackend(
    sessionOptions,
    backend:         LlamaBackend.Cuda,
    cudaVersion:     "12.4",
    installProgress: new Progress<(string msg, double pct)>(p => Console.Write($"\r[{p.pct:F0}%] {p.msg}")));

var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a research assistant.",
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
