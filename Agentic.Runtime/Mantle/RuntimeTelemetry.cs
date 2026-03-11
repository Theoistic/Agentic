using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agentic.Runtime.Mantle;

/// <summary>
/// OpenTelemetry instrumentation for the Agentic Runtime (Mantle) framework.
/// <para>
/// Provides <see cref="ActivitySource"/> and <see cref="Meter"/> instances used
/// throughout the runtime for session management, native inference, and tool execution.
/// Both use the name <see cref="SourceName"/> (<c>"Agentic.Runtime"</c>):
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t =&gt; t.AddSource(RuntimeTelemetry.SourceName))
///     .WithMetrics(m =&gt; m.AddMeter(RuntimeTelemetry.SourceName));
/// </code>
/// </para>
/// </summary>
public static class RuntimeTelemetry
{
    /// <summary>
    /// The instrumentation source name for both the activity source and the meter.
    /// Register this with your OpenTelemetry provider to capture runtime telemetry.
    /// </summary>
    public const string SourceName = "Agentic.Runtime";

    /// <summary>Version string reported by the instrumentation sources.</summary>
    public const string Version = "1.0.0";

    /// <summary>Activity source for distributed tracing of runtime operations.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName, Version);

    /// <summary>Meter for recording runtime metrics.</summary>
    public static readonly Meter Meter = new(SourceName, Version);

    // ── Metrics instruments ──────────────────────────────────────────────

    /// <summary>Number of LM sessions created.</summary>
    public static readonly Counter<long> SessionsCreated =
        Meter.CreateCounter<long>("agentic.runtime.sessions.created", "{session}", "Number of LM sessions created.");

    /// <summary>Number of inference generate calls executed.</summary>
    public static readonly Counter<long> InferenceCalls =
        Meter.CreateCounter<long>("agentic.runtime.inference.calls", "{call}", "Number of inference generate calls.");

    /// <summary>Duration of inference generate calls in milliseconds.</summary>
    public static readonly Histogram<double> InferenceDuration =
        Meter.CreateHistogram<double>("agentic.runtime.inference.duration", "ms", "Duration of inference generate calls.");

    /// <summary>Total tokens generated during inference.</summary>
    public static readonly Counter<long> TokensGenerated =
        Meter.CreateCounter<long>("agentic.runtime.tokens.generated", "{token}", "Tokens generated during inference.");

    /// <summary>Number of runtime tool executions.</summary>
    public static readonly Counter<long> ToolExecutions =
        Meter.CreateCounter<long>("agentic.runtime.tool.executions", "{execution}", "Number of runtime tool executions.");

    /// <summary>Duration of runtime tool executions in milliseconds.</summary>
    public static readonly Histogram<double> ToolExecutionDuration =
        Meter.CreateHistogram<double>("agentic.runtime.tool.execution.duration", "ms", "Duration of runtime tool executions.");

    /// <summary>Number of runtime tool execution failures.</summary>
    public static readonly Counter<long> ToolExecutionErrors =
        Meter.CreateCounter<long>("agentic.runtime.tool.errors", "{error}", "Number of runtime tool execution failures.");

    /// <summary>Number of embedding operations in the runtime.</summary>
    public static readonly Counter<long> EmbeddingCalls =
        Meter.CreateCounter<long>("agentic.runtime.embedding.calls", "{call}", "Number of embedding operations.");

    /// <summary>Number of conversation compaction operations.</summary>
    public static readonly Counter<long> CompactionOperations =
        Meter.CreateCounter<long>("agentic.runtime.compaction.operations", "{operation}", "Number of conversation compaction operations.");

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Starts a new activity for the specified operation.</summary>
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
