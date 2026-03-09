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

The `Agent` class drives multi-turn conversations with an LLM. It maintains conversation history, streams responses token-by-token, and automatically orchestrates MCP tool calls.

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

## AgentOptions Reference

| Property | Default | Description |
|----------|---------|-------------|
| `SystemPrompt` | `null` | Instruction text prepended to every request |
| `OnEvent` | `null` | Callback fired for every `AgentEvent` |
| `Compaction` | `null` | Enable context compaction (see [Context Compaction](context-compaction)) |
| `Reasoning` | `null` | Per-agent reasoning effort (see [Reasoning Control](reasoning-control)) |
| `Inference` | `null` | Per-agent sampling parameters (see [Inference Config](inference-config)) |
| `Model` | `null` | Per-agent model default (see [Model Selection](#model-selection)) |

## Agent Events

The `OnEvent` callback receives `AgentEvent` instances as the agent processes a request:

| `AgentEventKind` | Description |
|------------------|-------------|
| `TextDelta` | A new token or chunk of text from the model — stream this to the user |
| `ToolCall` | The model is about to invoke a tool |
| `ToolResult` | A tool has returned its result |
| `StepCompleted` | A workflow step has been verified and completed |
| `Done` | The full response is complete |

```csharp
var agent = new Agent(lm, new AgentOptions
{
    OnEvent = e => e.Kind switch
    {
        AgentEventKind.TextDelta    => Console.Write(e.Text),
        AgentEventKind.ToolCall     => Console.WriteLine($"\n[Tool] {e.ToolName}({e.Arguments})"),
        AgentEventKind.ToolResult   => Console.WriteLine($"[Result] {e.Result}"),
        AgentEventKind.Done         => Console.WriteLine("\n[Done]"),
        _                           => default,
    },
});
```

## Model Selection

The model to use is resolved in order of precedence (highest wins):

1. Per-request `model:` parameter
2. `AgentOptions.Model`
3. `LMConfig.ModelName`

```csharp
// Per-request override
var response = await agent.ChatStreamAsync(
    "Quick summary please.",
    model: "gpt-4o-mini");
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
builder.Services.AddSingleton<LM>(_ => new LM(new LMConfig
{
    Endpoint  = builder.Configuration["LM:Endpoint"]!,
    ModelName = builder.Configuration["LM:Model"]!,
}));

builder.Services.AddScoped<Agent>(sp => new Agent(
    sp.GetRequiredService<LM>(),
    new AgentOptions { SystemPrompt = "You are a helpful assistant." }));
```
