# OpenTelemetry Integration

Agentic provides **built-in OpenTelemetry instrumentation** for distributed tracing, metrics, and logs across all framework layers. The instrumentation uses the standard .NET `System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics` APIs, so **no additional NuGet packages are added** to the library itself.

## Quick Setup

Register the Agentic instrumentation sources with your OpenTelemetry SDK:

```csharp
using Agentic;
using Agentic.Runtime.Mantle;
using Agentic.Storage;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(AgenticTelemetry.SourceName)    // "Agentic"
        .AddSource(RuntimeTelemetry.SourceName)    // "Agentic.Runtime"
        .AddSource(StorageTelemetry.SourceName)    // "Agentic.Storage"
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(AgenticTelemetry.SourceName)     // "Agentic"
        .AddMeter(RuntimeTelemetry.SourceName)     // "Agentic.Runtime"
        .AddMeter(StorageTelemetry.SourceName)     // "Agentic.Storage"
        .AddOtlpExporter())
    .WithLogging(logging => logging
        .AddOtlpExporter());
```

## Source Names

| Source | Name | Description |
|--------|------|-------------|
| `AgenticTelemetry.SourceName` | `"Agentic"` | Agent operations, LM backend calls, tool invocations, MCP requests |
| `RuntimeTelemetry.SourceName` | `"Agentic.Runtime"` | Native inference, session management, runtime tool execution |
| `StorageTelemetry.SourceName` | `"Agentic.Storage"` | SQLite and PostgreSQL storage operations |

## Traces (Activities)

### Agentic (main framework)

| Activity | Kind | Description |
|----------|------|-------------|
| `agent.run` | Internal | Single-shot agent request (Run/RunAsync) |
| `agent.chat` | Internal | Multi-turn chat request (Chat/ChatAsync) |
| `agent.run_stream` | Internal | Single-shot streaming request |
| `agent.chat_stream` | Internal | Multi-turn streaming request |
| `agent.workflow` | Internal | Workflow execution across multiple steps |
| `lm.respond` | Client | OpenAI-compatible `/v1/responses` call |
| `lm.embed` | Client | Single embedding request |
| `lm.embed_batch` | Client | Batch embedding request |
| `lm.ping` | Client | LM server health check |
| `tool.invoke` | Internal | Tool invocation via ToolRegistry |
| `mcp.handle` | Server | MCP JSON-RPC request handling |

### Agentic.Runtime

| Activity | Kind | Description |
|----------|------|-------------|
| `runtime.session.create` | Internal | LM session creation and model loading |
| `runtime.session.create_response` | Internal | Full response generation |
| `runtime.session.embed` | Internal | Embedding generation |
| `runtime.engine.generate_tokens` | Internal | Token-by-token inference |
| `runtime.tool.execute` | Internal | Runtime tool execution (local or remote) |

### Agentic.Storage

| Activity | Kind | Description |
|----------|------|-------------|
| `storage.insert` | Client | Document insert |
| `storage.upsert` | Client | Document upsert |
| `storage.delete` | Client | Document delete |
| `storage.get` | Client | Document retrieval |
| `storage.search` | Client | Vector similarity search |

### Trace Attributes

Activities are enriched with standard [OpenTelemetry semantic conventions](https://opentelemetry.io/docs/specs/semconv/) where applicable:

| Attribute | Example | Used In |
|-----------|---------|---------|
| `gen_ai.system` | `"openai_compatible"` | LM calls |
| `gen_ai.request.model` | `"gpt-4o"` | Agent & LM calls |
| `gen_ai.response.id` | `"resp_abc123"` | LM responses |
| `gen_ai.usage.input_tokens` | `150` | Agent & session responses |
| `gen_ai.usage.output_tokens` | `320` | Agent & session responses |
| `gen_ai.usage.total_tokens` | `470` | Agent responses |
| `server.address` | `"http://localhost:1234"` | LM calls |
| `db.system` | `"sqlite"`, `"postgresql"` | Storage operations |
| `agentic.agent.method` | `"Run"`, `"ChatStream"` | Agent operations |
| `agentic.agent.has_images` | `true` | Vision requests |
| `agentic.tool.name` | `"search_files"` | Tool invocations |
| `agentic.mcp.method` | `"tools/call"` | MCP requests |
| `agentic.workflow.name` | `"data-pipeline"` | Workflow execution |
| `agentic.workflow.steps` | `3` | Workflow execution |
| `agentic.workflow.completed` | `true` | Workflow completion |

## Metrics

### Agentic (main framework)

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `agentic.agent.requests` | Counter | `{request}` | Agent operations started |
| `agentic.agent.request.duration` | Histogram | `ms` | Agent operation duration |
| `agentic.agent.active_operations` | UpDownCounter | `{operation}` | Currently active operations |
| `agentic.agent.tokens.input` | Counter | `{token}` | Input tokens consumed |
| `agentic.agent.tokens.output` | Counter | `{token}` | Output tokens produced |
| `agentic.lm.requests` | Counter | `{request}` | LM backend requests |
| `agentic.lm.request.duration` | Histogram | `ms` | LM request duration |
| `agentic.lm.request.errors` | Counter | `{error}` | LM request failures |
| `agentic.tool.invocations` | Counter | `{invocation}` | Tool invocations |
| `agentic.tool.invocation.duration` | Histogram | `ms` | Tool invocation duration |
| `agentic.tool.errors` | Counter | `{error}` | Tool invocation failures |
| `agentic.mcp.requests` | Counter | `{request}` | MCP requests handled |
| `agentic.mcp.request.duration` | Histogram | `ms` | MCP request duration |
| `agentic.workflow.executions` | Counter | `{execution}` | Workflow executions |
| `agentic.embedding.requests` | Counter | `{request}` | Embedding requests |

### Agentic.Runtime

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `agentic.runtime.sessions.created` | Counter | `{session}` | Sessions created |
| `agentic.runtime.inference.calls` | Counter | `{call}` | Inference calls |
| `agentic.runtime.inference.duration` | Histogram | `ms` | Inference duration |
| `agentic.runtime.tokens.generated` | Counter | `{token}` | Tokens generated |
| `agentic.runtime.tool.executions` | Counter | `{execution}` | Tool executions |
| `agentic.runtime.tool.execution.duration` | Histogram | `ms` | Tool execution duration |
| `agentic.runtime.tool.errors` | Counter | `{error}` | Tool execution failures |
| `agentic.runtime.embedding.calls` | Counter | `{call}` | Embedding calls |
| `agentic.runtime.compaction.operations` | Counter | `{operation}` | Compaction operations |

### Agentic.Storage

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `agentic.storage.operations` | Counter | `{operation}` | Storage operations |
| `agentic.storage.operation.duration` | Histogram | `ms` | Operation duration |
| `agentic.storage.errors` | Counter | `{error}` | Operation failures |

## Logs

Agentic uses `Microsoft.Extensions.Logging.ILogger` throughout the framework. When you configure OpenTelemetry logging, all Agentic log messages are automatically exported:

```csharp
var agent = new Agent(lm, new AgentOptions
{
    Logger = loggerFactory.CreateLogger<Agent>(),
});
```

The agent automatically logs structured events at appropriate levels:
- **Information**: User input, tool calls/results, answers, workflow steps
- **Debug**: System prompts, tool declarations, reasoning
- **Trace**: Streaming text deltas

## Example: Full Setup with Jaeger + Prometheus

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(AgenticTelemetry.SourceName)
        .AddSource(RuntimeTelemetry.SourceName)
        .AddSource(StorageTelemetry.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")))
    .WithMetrics(metrics => metrics
        .AddMeter(AgenticTelemetry.SourceName)
        .AddMeter(RuntimeTelemetry.SourceName)
        .AddMeter(StorageTelemetry.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());

var app = builder.Build();
app.MapPrometheusScrapingEndpoint();
```

## Architecture

The instrumentation is zero-dependency — it uses only the built-in .NET `System.Diagnostics` APIs:

- **`ActivitySource`** for distributed traces (compatible with OpenTelemetry, Zipkin, Jaeger)
- **`Meter`** for metrics (compatible with OpenTelemetry, Prometheus)
- **`ILogger`** for logs (compatible with OpenTelemetry logging bridge)

No OpenTelemetry SDK packages are added to the library. The consuming application controls which exporters and samplers to use.
