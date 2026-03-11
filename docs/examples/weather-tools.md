---
title: Weather Tools
parent: Examples
nav_order: 2
---

# Weather Tools
{: .no_toc }

A complete example showing how to define tools and connect them to an agent.

## Tools Implementation

```csharp
using System.ComponentModel;

public class WeatherTools : IAgentToolSet
{
    [Tool, Description("Get the current weather for a city.")]
    public Task<string> GetWeather(
        [ToolParam("City name")] string city,
        [ToolParam("Unit: celsius or fahrenheit")] string unit = "celsius")
    {
        // In a real app, call a weather API here
        return Task.FromResult(
            $"The weather in {city} is 22 °{(unit == "fahrenheit" ? "F" : "C")} and sunny.");
    }

    [Tool, Description("Get a 3-day weather forecast for a city.")]
    public Task<string> GetForecast(
        [ToolParam("City name")] string city)
    {
        return Task.FromResult(
            $"Forecast for {city}:\n" +
            $"  Day 1: 22°C, sunny\n" +
            $"  Day 2: 18°C, partly cloudy\n" +
            $"  Day 3: 15°C, rain");
    }

    [Tool, Description("List cities that currently have weather alerts.")]
    public Task<string[]> GetAlertCities()
    {
        return Task.FromResult(new[] { "Paris", "London", "Berlin" });
    }
}
```

## MCP Server

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAgenticMcp(opt =>
{
    opt.ToolCallTimeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();
app.MapMcpServer("/mcp");

var tools = app.Services.GetRequiredService<ToolRegistry>();
tools.Register(new WeatherTools());

await app.RunAsync();   // Listening on http://localhost:5100
```

## Agent

```csharp
var lm = new OpenAIBackend(new LMConfig
{
    Endpoint  = "http://localhost:1234",
    ModelName = "your-model-name",
});

var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a weather assistant. Use the available tools to answer questions.",
    OnEvent      = e =>
    {
        switch (e.Kind)
        {
            case AgentEventKind.TextDelta:
                Console.Write(e.Text);
                break;
            case AgentEventKind.ToolCall:
                Console.WriteLine($"\n[Calling tool: {e.ToolName}]");
                break;
            case AgentEventKind.ToolResult:
                Console.WriteLine($"[Tool returned: {e.Result}]");
                break;
        }
    },
});

// The agent will automatically call weather tools as needed
await agent.ChatStreamAsync(
    "What's the weather like in Paris and London? Which one should I visit today?",
    mcpServerUrl: "http://localhost:5100/mcp");
```

## Expected Output

```
[Calling tool: get_weather]
[Tool returned: The weather in Paris is 22 °C and sunny.]
[Calling tool: get_weather]
[Tool returned: The weather in London is 22 °C and sunny.]

Both Paris and London are enjoying lovely weather today at 22°C and sunny skies!
For a city break, either would be a great choice...
```

## What it demonstrates

- Defining a multi-method `IAgentToolSet`
- Hosting tools on an MCP server
- Connecting an agent to the MCP server
- Observing tool calls and results via `AgentEventKind`
