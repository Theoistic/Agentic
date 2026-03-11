---
title: MCP Server + Agent
parent: Examples
nav_order: 3
---

# MCP Server + Agent
{: .no_toc }

A complete example of hosting an MCP server in one process and connecting an agent from another.

## Server (separate process / container)

```csharp
// Server/Program.cs
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAgenticMcp(opt =>
{
    opt.ApiKey          = "my-secret-key";
    opt.ToolCallTimeout = TimeSpan.FromSeconds(55);
    opt.ServerName      = "My Tool Server";
});

var app = builder.Build();
app.MapMcpServer("/mcp");

// Register as many tool sets as you need
var tools = app.Services.GetRequiredService<ToolRegistry>();
tools.Register(new WeatherTools());
tools.Register(new DatabaseTools(app.Services.GetRequiredService<AppDbContext>()));
tools.Register(new EmbeddingTools(app.Services.GetRequiredService<ILLMBackend>()));

await app.RunAsync("http://0.0.0.0:5100");
```

## Agent (client process)

```csharp
// Agent/Program.cs
var lm = new OpenAIBackend(new LMConfig
{
    Endpoint  = "http://localhost:1234",
    ModelName = "your-model-name",
});

var agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = "You are a helpful assistant with access to weather and database tools.",
    OnEvent      = e =>
    {
        if (e.Kind == AgentEventKind.TextDelta)
            Console.Write(e.Text);
    },
});

// Connect to the MCP server — authentication header is sent automatically
// if the URL contains ?key=... or you pass it via mcpHeaders
await agent.ChatStreamAsync(
    "What's the weather in Tokyo, and how many customers do we have in Japan?",
    mcpServerUrl: "http://localhost:5100/mcp");
```

## Docker Compose Example

```yaml
version: "3.9"
services:
  tool-server:
    build: ./Server
    ports:
      - "5100:5100"
    environment:
      - MCP_API_KEY=my-secret-key
      - DB_CONNECTION=Host=db;Database=myapp;Username=postgres;Password=secret
    depends_on:
      - db

  agent:
    build: ./Agent
    environment:
      - LM_ENDPOINT=http://lm-studio:1234
      - MCP_SERVER=http://tool-server:5100/mcp
    depends_on:
      - tool-server

  db:
    image: pgvector/pgvector:pg16
    environment:
      POSTGRES_PASSWORD: secret
      POSTGRES_DB: myapp
```

## What it demonstrates

- Running MCP server and agent in separate processes
- Authentication with `ApiKey`
- Registering multiple tool sets on one server
- Production-style deployment with Docker Compose
