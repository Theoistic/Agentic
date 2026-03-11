---
title: Agent
parent: Features
nav_order: 2
---

# Agent
{: .no_toc }

## Table of contents
{: .no_toc .text-delta }

1. TOC
{:toc}

---

The `Agent` class drives multi-turn conversations with an LLM. It accepts any `ILLMBackend`, maintains conversation history, streams responses token-by-token, and automatically orchestrates MCP tool calls.

## Creating an Agent

```csharp
var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a helpful assistant.",
    OnEvent      = e =>
    {
        if (e.Kind == AgentEventKind.TextDelta)
            Console.Write(e.Text);
    },
});
```

## Running Conversations

### Turn methods

| Method | History | Streaming |
|--------|---------|-----------|
| `RunAsync` | No | No |
| `RunStreamAsync` | No | Yes |
| `ChatAsync` | Yes | No |
| `ChatStreamAsync` | Yes | Yes |

Every method has a `(text, images, ...)` overload for multimodal input. All return `Task<AgentResponse>`.

### Single-turn (no history)

```csharp
var response = await agent.RunAsync(
    "Summarise the following text: ...",
    mcpServerUrl: "http://localhost:5100/mcp");   // optional
```

### Multi-turn streaming

```csharp
// Each call appends to the conversation history
await agent.ChatStreamAsync("What is the weather in Paris?");
await agent.ChatStreamAsync("What about London?");
await agent.ChatStreamAsync("Compare both cities.");
```

### Resetting the conversation

```csharp
// Clears history without compaction
agent.ResetConversation();
```

## `AgentResponse`

| Property | Description |
|----------|-------------|
| `Text` | Final model text for this turn |
| `ToolInvocations` | All tool calls executed during the turn, in order |
| `Usage` | Token usage (`InputTokens`, `OutputTokens`, `TotalTokens`); may be `null` |

## `AgentOptions` Reference

| Property | Default | Description |
|----------|---------|-------------|
| `SystemPrompt` | `null` | Instruction text prepended to every request |
| `OnEvent` | `null` | Callback fired for every `AgentEvent` |
| `Reasoning` | `null` | Per-agent reasoning effort (see [Reasoning Control](reasoning-control)) |
| `Inference` | `null` | Per-agent sampling parameters (see [Inference Config](inference-config)) |
| `Model` | `null` | Per-agent model default (see [Model Selection](#model-selection)) |
| `Logger` | `null` | `ILogger` — events are written automatically; no `OnEvent` switch needed |

## Agent Events

The `OnEvent` callback receives `AgentEvent` instances as the agent processes a request:

| `AgentEventKind` | Description |
|------------------|-------------|
| `UserInput` | User message was received |
| `SystemPrompt` | System prompt dispatched to the model |
| `ToolDeclaration` | An MCP server was declared for this request |
| `Reasoning` | Model emitted a thinking / reasoning chunk |
| `TextDelta` | A streaming text delta arrived from the model |
| `ToolCall` | The model invoked a tool |
| `ToolResult` | A tool returned its result |
| `Answer` | The model produced its final answer for this turn |
| `StepCompleted` | A workflow step was verified and completed |
| `WorkflowCompleted` | All workflow steps were completed |

```csharp
var agent = new Agent(lm, new AgentOptions
{
    OnEvent = e => e.Kind switch
    {
        AgentEventKind.TextDelta    => Console.Write(e.Text),
        AgentEventKind.ToolCall     => Console.WriteLine($"\n[Tool] {e.ToolName}({e.Arguments})"),
        AgentEventKind.ToolResult   => Console.WriteLine($"[Result] {e.Text}"),
        AgentEventKind.Answer       => Console.WriteLine("\n[Done]"),
        _                           => default,
    },
});
```

### Logging instead of `OnEvent`

Pass an `ILogger` to have all events written automatically without wiring `OnEvent` manually:

```csharp
var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a helpful assistant.",
    Logger       = loggerFactory.CreateLogger<Agent>(),
});
```

`TextDelta` is written at `Trace` level; all other events at `Debug` or `Information` so standard log-level filters keep the output clean.

## Model Selection

The model to use is resolved in order of precedence (highest wins):

1. Per-request `model:` parameter
2. `AgentOptions.Model`
3. `LMConfig.ModelName`

```csharp
// Per-request override
var response = await agent.ChatStreamAsync(
    "Quick summary please.",
    model: "advanced");   // resolved via LMConfig.Models alias map
```

## Using Tools with an Agent

Point the agent at a running [MCP server](mcp-server) via the `mcpServerUrl` parameter:

```csharp
var response = await agent.RunAsync(
    "What is the weather in Tokyo?",
    mcpServerUrl: "http://localhost:5100/mcp");
```

The agent will automatically discover available tools, call them as needed, and continue the conversation with the results.

## Dependency Injection

Register the agent in an ASP.NET Core or generic host:

```csharp
builder.Services.AddSingleton<ILLMBackend>(_ => new OpenAIBackend(new LMConfig
{
    Endpoint  = builder.Configuration["LM:Endpoint"]!,
    ModelName = builder.Configuration["LM:Model"]!,
}));

builder.Services.AddScoped<Agent>(sp => new Agent(
    sp.GetRequiredService<ILLMBackend>(),
    new AgentOptions { SystemPrompt = "You are a helpful assistant." }));
```
