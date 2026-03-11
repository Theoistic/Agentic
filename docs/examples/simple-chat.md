---
title: Simple Chat
parent: Examples
nav_order: 1
---

# Simple Chat
{: .no_toc }

A minimal conversational agent that streams responses to the console.

## Code

```csharp
using Agentic;

// 1. Configure the LM client
var lm = new OpenAIBackend(new LMConfig
{
    Endpoint  = "http://localhost:1234",   // LM Studio or any OpenAI-compatible server
    ModelName = "your-model-name",
});

// 2. Create the agent
var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a helpful, concise assistant.",
    OnEvent      = e =>
    {
        if (e.Kind == AgentEventKind.TextDelta)
            Console.Write(e.Text);
    },
});

// 3. Start chatting
Console.WriteLine("Type your message (press Enter twice to send, Ctrl+C to quit):\n");

while (true)
{
    Console.Write("You: ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input)) continue;

    Console.Write("Agent: ");
    await agent.ChatStreamAsync(input);
    Console.WriteLine("\n");
}
```

## What it demonstrates

- Creating an `OpenAIBackend` client pointing at a local model server
- Creating an `Agent` with a system prompt
- Streaming tokens to the console via the `OnEvent` callback
- Multi-turn conversation (each `ChatStreamAsync` call appends to history)

## Running

```
dotnet run
```

Make sure your LM Studio (or other server) is running at `http://localhost:1234` with the specified model loaded.

## Tips

- Change `SystemPrompt` to give the agent a different persona or expertise
- Add `Reasoning = ReasoningEffort.High` to `AgentOptions` for more thorough responses
- Call `agent.ResetConversation()` to start a fresh conversation
