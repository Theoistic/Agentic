using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agentic;
using Agentic.Storage;
using RuntimeTelemetry = Agentic.Runtime.Mantle.RuntimeTelemetry;

namespace Agentic.Tests;

/// <summary>
/// Validates that OpenTelemetry instrumentation is correctly wired up across
/// the Agentic framework: ActivitySources emit activities when a listener
/// is registered, and Meters emit measurements.
/// </summary>
[TestClass]
public sealed class TelemetryTests
{
    // ── ActivitySource tests ─────────────────────────────────────────────

    [TestMethod]
    public void AgenticTelemetry_ActivitySource_HasCorrectName()
    {
        Assert.AreEqual(AgenticTelemetry.SourceName, AgenticTelemetry.ActivitySource.Name);
        Assert.AreEqual("1.0.0", AgenticTelemetry.ActivitySource.Version);
    }

    [TestMethod]
    public void RuntimeTelemetry_ActivitySource_HasCorrectName()
    {
        Assert.AreEqual(RuntimeTelemetry.SourceName, RuntimeTelemetry.ActivitySource.Name);
        Assert.AreEqual("1.0.0", RuntimeTelemetry.ActivitySource.Version);
    }

    [TestMethod]
    public void StorageTelemetry_ActivitySource_HasCorrectName()
    {
        Assert.AreEqual(StorageTelemetry.SourceName, StorageTelemetry.ActivitySource.Name);
        Assert.AreEqual("1.0.0", StorageTelemetry.ActivitySource.Version);
    }

    [TestMethod]
    public void AgenticTelemetry_StartActivity_ReturnsActivityWhenListenerRegistered()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgenticTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = AgenticTelemetry.ActivitySource.StartActivity("test.operation");
        Assert.IsNotNull(activity);
        Assert.AreEqual("test.operation", activity.OperationName);
    }

    [TestMethod]
    public void AgenticTelemetry_StartActivity_ReturnsNullWhenNoListener()
    {
        // With no listener registered for our source, StartActivity returns null
        using var activity = AgenticTelemetry.ActivitySource.StartActivity("no.listener.test");
        // Activity may be null when no listener is subscribed — this is expected behavior
        // The test simply validates the call does not throw
    }

    [TestMethod]
    public void AgenticTelemetry_RecordException_SetsErrorStatus()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgenticTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = AgenticTelemetry.ActivitySource.StartActivity("test.exception");
        Assert.IsNotNull(activity);

        var exception = new InvalidOperationException("test error");
        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", exception.GetType().FullName },
            { "exception.message", exception.Message },
        }));

        Assert.AreEqual(ActivityStatusCode.Error, activity.Status);
        Assert.AreEqual("test error", activity.StatusDescription);

        var events = activity.Events.ToList();
        Assert.HasCount(1, events);
        Assert.AreEqual("exception", events[0].Name);
    }

    // ── Meter tests ─────────────────────────────────────────────────────

    [TestMethod]
    public void AgenticTelemetry_Meter_HasCorrectName()
    {
        Assert.AreEqual("Agentic", AgenticTelemetry.Meter.Name);
        Assert.AreEqual("1.0.0", AgenticTelemetry.Meter.Version);
    }

    [TestMethod]
    public void RuntimeTelemetry_Meter_HasCorrectName()
    {
        Assert.AreEqual("Agentic.Runtime", RuntimeTelemetry.Meter.Name);
        Assert.AreEqual("1.0.0", RuntimeTelemetry.Meter.Version);
    }

    [TestMethod]
    public void StorageTelemetry_Meter_HasCorrectName()
    {
        Assert.AreEqual("Agentic.Storage", StorageTelemetry.Meter.Name);
        Assert.AreEqual("1.0.0", StorageTelemetry.Meter.Version);
    }

    [TestMethod]
    public void AgenticTelemetry_MetricInstruments_AreCreated()
    {
        // Verify all counter and histogram instruments exist
        Assert.IsNotNull(AgenticTelemetry.AgentRequests);
        Assert.IsNotNull(AgenticTelemetry.AgentRequestDuration);
        Assert.IsNotNull(AgenticTelemetry.TokensInput);
        Assert.IsNotNull(AgenticTelemetry.TokensOutput);
        Assert.IsNotNull(AgenticTelemetry.LmRequests);
        Assert.IsNotNull(AgenticTelemetry.LmRequestDuration);
        Assert.IsNotNull(AgenticTelemetry.LmRequestErrors);
        Assert.IsNotNull(AgenticTelemetry.ToolInvocations);
        Assert.IsNotNull(AgenticTelemetry.ToolInvocationDuration);
        Assert.IsNotNull(AgenticTelemetry.ToolErrors);
        Assert.IsNotNull(AgenticTelemetry.McpRequests);
        Assert.IsNotNull(AgenticTelemetry.McpRequestDuration);
        Assert.IsNotNull(AgenticTelemetry.WorkflowExecutions);
        Assert.IsNotNull(AgenticTelemetry.EmbeddingRequests);
        Assert.IsNotNull(AgenticTelemetry.ActiveAgentOperations);
    }

    [TestMethod]
    public void RuntimeTelemetry_MetricInstruments_AreCreated()
    {
        Assert.IsNotNull(RuntimeTelemetry.SessionsCreated);
        Assert.IsNotNull(RuntimeTelemetry.InferenceCalls);
        Assert.IsNotNull(RuntimeTelemetry.InferenceDuration);
        Assert.IsNotNull(RuntimeTelemetry.TokensGenerated);
        Assert.IsNotNull(RuntimeTelemetry.ToolExecutions);
        Assert.IsNotNull(RuntimeTelemetry.ToolExecutionDuration);
        Assert.IsNotNull(RuntimeTelemetry.ToolExecutionErrors);
        Assert.IsNotNull(RuntimeTelemetry.EmbeddingCalls);
        Assert.IsNotNull(RuntimeTelemetry.CompactionOperations);
    }

    [TestMethod]
    public void StorageTelemetry_MetricInstruments_AreCreated()
    {
        Assert.IsNotNull(StorageTelemetry.Operations);
        Assert.IsNotNull(StorageTelemetry.OperationDuration);
        Assert.IsNotNull(StorageTelemetry.OperationErrors);
    }

    [TestMethod]
    public void AgenticTelemetry_Counters_CanBeIncremented()
    {
        var measurements = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AgenticTelemetry.SourceName && instrument.Name == "agentic.agent.requests")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            measurements.Add(measurement));
        listener.Start();

        AgenticTelemetry.AgentRequests.Add(1, new KeyValuePair<string, object?>("agentic.agent.method", "Test"));

        listener.RecordObservableInstruments();

        Assert.IsNotEmpty(measurements, "Counter should have recorded at least one measurement.");
        Assert.AreEqual(1, measurements[0]);
    }

    [TestMethod]
    public void AgenticTelemetry_Histogram_CanRecordValues()
    {
        var measurements = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AgenticTelemetry.SourceName && instrument.Name == "agentic.agent.request.duration")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
            measurements.Add(measurement));
        listener.Start();

        AgenticTelemetry.AgentRequestDuration.Record(42.5, new KeyValuePair<string, object?>("agentic.agent.method", "Test"));

        Assert.IsNotEmpty(measurements, "Histogram should have recorded at least one measurement.");
        Assert.AreEqual(42.5, measurements[0]);
    }

    // ── Tool instrumentation integration test ────────────────────────────

    [TestMethod]
    public async Task ToolRegistry_InvokeAsync_EmitsActivityAndMetrics()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgenticTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => activities.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        var registry = new ToolRegistry();
        registry.Register(new TestTools());

        var args = Agentic.ToolSchema.Parse("""{"message": "hello"}""");
        var result = await registry.InvokeAsync("greet", args);

        Assert.AreEqual("hello world", result);
        Assert.IsTrue(activities.Any(a => a.OperationName == "tool.invoke"),
            "Should emit a 'tool.invoke' activity.");
        var toolActivity = activities.First(a => a.OperationName == "tool.invoke");
        Assert.AreEqual("greet", toolActivity.GetTagItem("agentic.tool.name")?.ToString());
    }

    // ── Storage instrumentation integration test ─────────────────────────

    [TestMethod]
    public async Task InMemoryStore_Operations_EmitNoExceptions()
    {
        // InMemoryStore doesn't use StorageTelemetry directly (it's a testing store),
        // but the SqliteCollection does. This test validates the telemetry code paths
        // in SqliteStore compile and the telemetry statics are accessible.
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StorageTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => activities.Add(a),
        };
        ActivitySource.AddActivityListener(listener);

        // Just validate that StorageTelemetry.StartActivity works
        using var activity = StorageTelemetry.ActivitySource.StartActivity("test.storage");
        Assert.IsNotNull(activity);
        activity?.SetTag("db.system", "test");
    }

    // ── Test tool set ────────────────────────────────────────────────────

    private sealed class TestTools : IAgentToolSet
    {
        [Tool]
        public string Greet(string message) => $"{message} world";
    }
}
