---
title: MCP Server
parent: Features
nav_order: 4
---

# MCP Server
{: .no_toc }

## Table of contents
{: .no_toc .text-delta }

1. TOC
{:toc}

---

Agentic makes it trivial to expose your tools as a [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server. Any MCP-compatible client — including LM Studio, Claude Desktop, and other agents — can discover and call your tools over HTTP.

## Setup

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAgenticMcp(opt =>
{
    opt.ApiKey          = "my-secret-key";   // optional Bearer-token auth
    opt.ToolCallTimeout = TimeSpan.FromSeconds(55);
});

var app = builder.Build();
app.MapMcpServer("/mcp");

var tools = app.Services.GetRequiredService<ToolRegistry>();
tools.Register(new WeatherTools());

await app.RunAsync();
```

## McpOptions Reference

| Property | Default | Description |
|----------|---------|-------------|
| `ApiKey` | `null` | Bearer token required on every request (`null` = open, no authentication) |
| `AllowedOrigins` | `null` | CORS origin allowlist for browser clients |
| `ToolCallTimeout` | `55 s` | Cancels tool calls that exceed this duration and returns a clean error |
| `ServerName` | `"Agentic"` | Reported to MCP clients during `initialize` |
| `ProtocolVersion` | `"2025-03-26"` | MCP protocol version advertised to clients |

## Protocol

The MCP server communicates over **Server-Sent Events (SSE)** using the **JSON-RPC 2.0** protocol. The supported MCP methods are:

| Method | Description |
|--------|-------------|
| `initialize` | Handshake — returns server capabilities and protocol version |
| `tools/list` | Returns the list of available tools and their JSON schemas |
| `tools/call` | Invokes a tool with the provided arguments |

## Connecting from an Agent

Point any Agentic `Agent` at your MCP server:

```csharp
var response = await agent.RunAsync(
    "What is the weather in Paris?",
    mcpServerUrl: "http://localhost:5100/mcp");
```

## Connecting from LM Studio

1. Open **LM Studio** → Settings → **MCP Servers**
2. Click **Add server**
3. Enter your server URL: `http://localhost:5100/mcp`
4. If you have an API key set, add `Authorization: Bearer <your-key>` as a header

## Connecting from Claude Desktop

Add the server to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "my-agentic-server": {
      "url": "http://localhost:5100/mcp",
      "headers": {
        "Authorization": "Bearer my-secret-key"
      }
    }
  }
}
```

## Testing the Server

Use `curl` to call a tool directly:

```bash
curl -X POST http://localhost:5100/mcp \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer my-secret-key" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "tools/call",
    "params": {
      "name": "get_weather",
      "arguments": { "city": "Paris", "unit": "celsius" }
    }
  }'
```

## Security

- Set `ApiKey` to require a `Bearer` token on all requests
- Use `AllowedOrigins` to restrict which domains can call the server from a browser
- Run behind HTTPS in production (e.g. behind an nginx reverse proxy or Azure App Service)
- Tool calls are automatically cancelled after `ToolCallTimeout` to prevent hanging requests
