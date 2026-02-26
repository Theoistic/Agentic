namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  Event model & options
// ═══════════════════════════════════════════════════════════════════════════

public enum AgentEventKind { UserInput, Reasoning, TextDelta, ToolCall, ToolResult, Answer, StepCompleted, WorkflowCompleted, Compacted }

public sealed class AgentEvent
{
    public required AgentEventKind Kind { get; init; }
    public string? ToolName { get; init; }
    public string? Arguments { get; init; }
    public string? Text { get; init; }
    public int Round { get; init; }
    /// <summary>Populated on <see cref="AgentEventKind.Answer"/> when the API returns usage data.</summary>
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? TotalTokens { get; init; }
}

public sealed class AgentOptions
{
    public string? SystemPrompt { get; set; }
    public double Temperature { get; set; }
    public Action<AgentEvent>? OnEvent { get; set; }
    public CompactionOptions? Compaction { get; set; }
}

public sealed class ToolInvocation
{
    public required string Name { get; init; }
    public required string Arguments { get; init; }
    public required string Result { get; init; }
}

public sealed class AgentResponse
{
    public required string Text { get; init; }
    public List<ToolInvocation> ToolInvocations { get; init; } = [];
    public ResponseUsage? Usage { get; init; }
    public override string ToString() => Text;
}
