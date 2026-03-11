using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agentic.Storage;

/// <summary>
/// OpenTelemetry instrumentation for Agentic Storage operations.
/// <para>
/// Uses the source name <see cref="SourceName"/> (<c>"Agentic.Storage"</c>):
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t =&gt; t.AddSource(StorageTelemetry.SourceName))
///     .WithMetrics(m =&gt; m.AddMeter(StorageTelemetry.SourceName));
/// </code>
/// </para>
/// </summary>
public static class StorageTelemetry
{
    /// <summary>The instrumentation source name.</summary>
    public const string SourceName = "Agentic.Storage";

    /// <summary>Version string reported by the instrumentation sources.</summary>
    public const string Version = "1.0.0";

    /// <summary>Activity source for distributed tracing of storage operations.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName, Version);

    /// <summary>Meter for recording storage metrics.</summary>
    public static readonly Meter Meter = new(SourceName, Version);

    /// <summary>Number of storage operations (insert, upsert, delete, get, search, scan).</summary>
    public static readonly Counter<long> Operations =
        Meter.CreateCounter<long>("agentic.storage.operations", "{operation}", "Number of storage operations.");

    /// <summary>Duration of storage operations in milliseconds.</summary>
    public static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("agentic.storage.operation.duration", "ms", "Duration of storage operations.");

    /// <summary>Number of storage operation failures.</summary>
    public static readonly Counter<long> OperationErrors =
        Meter.CreateCounter<long>("agentic.storage.errors", "{error}", "Number of storage operation failures.");

    internal static Activity? StartActivity(string operationName, ActivityKind kind = ActivityKind.Client)
        => ActivitySource.StartActivity(operationName, kind);

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
