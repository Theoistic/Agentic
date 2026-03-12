using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agentic;

/// <summary>
/// Central OpenTelemetry instrumentation for the Agentic framework.
/// <para>
/// Provides <see cref="ActivitySource"/> (distributed tracing) and <see cref="Meter"/>
/// (metrics) instances used throughout the library. Both use the name
/// <see cref="SourceName"/> (<c>"Agentic"</c>), which consumers register with their
/// OpenTelemetry SDK configuration:
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t =&gt; t.AddSource(AgenticTelemetry.SourceName))
///     .WithMetrics(m =&gt; m.AddMeter(AgenticTelemetry.SourceName));
/// </code>
/// </para>
/// </summary>
public static class AgenticTelemetry
{
    /// <summary>
    /// The instrumentation source name used for both <see cref="ActivitySource"/> and <see cref="Meter"/>.
    /// Register this with your OpenTelemetry provider to capture Agentic telemetry.
    /// </summary>
    public const string SourceName = "Agentic";

    /// <summary>Version string reported by the instrumentation sources.</summary>
    public const string Version = "1.0.0";

    /// <summary>Activity source for distributed tracing of agent operations, LM calls, tool invocations, and MCP requests.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName, Version);

    /// <summary>Meter for recording agent and LM metrics (request counts, durations, token usage, etc.).</summary>
    public static readonly Meter Meter = new(SourceName, Version);

    // ── Metrics instruments ──────────────────────────────────────────────

    /// <summary>Total number of agent operations started (Run, Chat, RunStream, ChatStream).</summary>
    public static readonly Counter<long> AgentRequests =
        Meter.CreateCounter<long>("agentic.agent.requests", "{request}", "Number of agent operations started.");

    /// <summary>Duration of agent operations in milliseconds.</summary>
    public static readonly Histogram<double> AgentRequestDuration =
        Meter.CreateHistogram<double>("agentic.agent.request.duration", "ms", "Duration of agent operations.");

    /// <summary>Total input tokens consumed across all LM calls.</summary>
    public static readonly Counter<long> TokensInput =
        Meter.CreateCounter<long>("agentic.agent.tokens.input", "{token}", "Input tokens consumed.");

    /// <summary>Total output tokens produced across all LM calls.</summary>
    public static readonly Counter<long> TokensOutput =
        Meter.CreateCounter<long>("agentic.agent.tokens.output", "{token}", "Output tokens produced.");

    /// <summary>Number of LM backend requests dispatched (RespondAsync, RespondStreamingAsync, EmbedAsync, etc.).</summary>
    public static readonly Counter<long> LmRequests =
        Meter.CreateCounter<long>("agentic.lm.requests", "{request}", "Number of LM backend requests.");

    /// <summary>Duration of individual LM backend requests in milliseconds.</summary>
    public static readonly Histogram<double> LmRequestDuration =
        Meter.CreateHistogram<double>("agentic.lm.request.duration", "ms", "Duration of LM backend requests.");

    /// <summary>Number of LM backend request failures.</summary>
    public static readonly Counter<long> LmRequestErrors =
        Meter.CreateCounter<long>("agentic.lm.request.errors", "{error}", "Number of LM backend request failures.");

    /// <summary>Number of tool invocations executed.</summary>
    public static readonly Counter<long> ToolInvocations =
        Meter.CreateCounter<long>("agentic.tool.invocations", "{invocation}", "Number of tool invocations.");

    /// <summary>Duration of tool invocations in milliseconds.</summary>
    public static readonly Histogram<double> ToolInvocationDuration =
        Meter.CreateHistogram<double>("agentic.tool.invocation.duration", "ms", "Duration of tool invocations.");

    /// <summary>Number of tool invocation failures.</summary>
    public static readonly Counter<long> ToolErrors =
        Meter.CreateCounter<long>("agentic.tool.errors", "{error}", "Number of tool invocation failures.");

    /// <summary>Number of MCP JSON-RPC requests handled.</summary>
    public static readonly Counter<long> McpRequests =
        Meter.CreateCounter<long>("agentic.mcp.requests", "{request}", "Number of MCP JSON-RPC requests handled.");

    /// <summary>Duration of MCP request handling in milliseconds.</summary>
    public static readonly Histogram<double> McpRequestDuration =
        Meter.CreateHistogram<double>("agentic.mcp.request.duration", "ms", "Duration of MCP request handling.");

    /// <summary>Number of workflow executions started.</summary>
    public static readonly Counter<long> WorkflowExecutions =
        Meter.CreateCounter<long>("agentic.workflow.executions", "{execution}", "Number of workflow executions.");

    /// <summary>Number of embedding requests (single or batch).</summary>
    public static readonly Counter<long> EmbeddingRequests =
        Meter.CreateCounter<long>("agentic.embedding.requests", "{request}", "Number of embedding requests.");

    /// <summary>Number of active agent operations currently in progress.</summary>
    public static readonly UpDownCounter<long> ActiveAgentOperations =
        Meter.CreateUpDownCounter<long>("agentic.agent.active_operations", "{operation}", "Number of active agent operations.");

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a new <see cref="Activity"/> for the specified operation.
    /// Returns <c>null</c> when no listener is registered.
    /// </summary>
    internal static Activity? StartActivity(string operationName, ActivityKind kind = ActivityKind.Internal)
        => ActivitySource.StartActivity(operationName, kind);

    /// <summary>Records an exception on an activity and sets the error status.</summary>
    internal static void RecordException(Activity? activity, Exception ex)
    {
        if (activity is null) return;
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName },
            { "exception.message", ex.Message },
            { "exception.stacktrace", ex.StackTrace },
        }));
    }
}
