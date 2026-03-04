namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  Event model & options
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Discriminates the kind of event raised by an <see cref="Agent"/> during a turn.</summary>
public enum AgentEventKind
{
    /// <summary>The user's input message was recorded.</summary>
    UserInput,
    /// <summary>The model emitted an intermediate reasoning / thinking chunk.</summary>
    Reasoning,
    /// <summary>A streaming text delta arrived from the model.</summary>
    TextDelta,
    /// <summary>The model invoked a tool.</summary>
    ToolCall,
    /// <summary>A tool call completed and returned its result.</summary>
    ToolResult,
    /// <summary>The model produced its final answer for this turn.</summary>
    Answer,
    /// <summary>A workflow step was completed.</summary>
    StepCompleted,
    /// <summary>All steps in a workflow were completed.</summary>
    WorkflowCompleted,
    /// <summary>Conversation history was compacted into a checkpoint.</summary>
    Compacted
}

/// <summary>An event raised by the <see cref="Agent"/> and delivered to <see cref="AgentOptions.OnEvent"/>.</summary>
public sealed class AgentEvent
{
    /// <summary>The kind of event that occurred.</summary>
    public required AgentEventKind Kind { get; init; }
    /// <summary>Name of the tool involved (set for <see cref="AgentEventKind.ToolCall"/> and <see cref="AgentEventKind.ToolResult"/>).</summary>
    public string? ToolName { get; init; }
    /// <summary>JSON-encoded tool arguments (set for <see cref="AgentEventKind.ToolCall"/>).</summary>
    public string? Arguments { get; init; }
    /// <summary>Text payload — delta text, tool result, answer, or compaction summary depending on <see cref="Kind"/>.</summary>
    public string? Text { get; init; }
    /// <summary>The turn index within the current conversation.</summary>
    public int Round { get; init; }
    /// <summary>Populated on <see cref="AgentEventKind.Answer"/> when the API returns usage data.</summary>
    public int? InputTokens { get; init; }
    /// <summary>Output token count (see <see cref="InputTokens"/>).</summary>
    public int? OutputTokens { get; init; }
    /// <summary>Total token count (see <see cref="InputTokens"/>).</summary>
    public int? TotalTokens { get; init; }
}

/// <summary>Configuration options for an <see cref="Agent"/> instance.</summary>
public sealed class AgentOptions
{
    /// <summary>System prompt prepended to every request. <c>null</c> means no system instruction.</summary>
    public string? SystemPrompt { get; set; }
    /// <summary>Sampling temperature (0 = deterministic). Passed directly to the model.</summary>
    public double Temperature { get; set; }
    /// <summary>Callback invoked for every <see cref="AgentEvent"/> during a turn.</summary>
    public Action<AgentEvent>? OnEvent { get; set; }
    /// <summary>Enables context compaction. <c>null</c> = compaction disabled.</summary>
    public CompactionOptions? Compaction { get; set; }
    /// <summary>
    /// Qwen thinking override for every call made by this agent.
    /// Overrides <see cref="LMConfig.Thinking"/> when set; <c>null</c> falls back to the LM-level default.
    /// </summary>
    public ThinkingConfig? Thinking { get; set; }
    /// <summary>
    /// Agent-level model override. Accepts a named alias from <see cref="LMConfig.Models"/> or a literal
    /// model ID. <c>null</c> falls back to <see cref="LMConfig.ModelName"/>.
    /// Overridable per-call via the <c>model</c> parameter on each agent method.
    /// </summary>
    public string? Model { get; set; }
}

/// <summary>Records a single tool call that was executed during an agent turn.</summary>
public sealed class ToolInvocation
{
    /// <summary>The tool name that was called.</summary>
    public required string Name { get; init; }
    /// <summary>JSON-encoded arguments supplied to the tool.</summary>
    public required string Arguments { get; init; }
    /// <summary>The string result returned by the tool.</summary>
    public required string Result { get; init; }
}

/// <summary>The result of a single agent turn, containing the model's text and any tool calls that were made.</summary>
public sealed class AgentResponse
{
    /// <summary>The final text produced by the model for this turn.</summary>
    public required string Text { get; init; }
    /// <summary>All tool calls that were executed during this turn, in order.</summary>
    public List<ToolInvocation> ToolInvocations { get; init; } = [];
    /// <summary>Token usage reported by the model for this turn. May be <c>null</c> if the server did not return usage data.</summary>
    public ResponseUsage? Usage { get; init; }
    /// <inheritdoc/>
    public override string ToString() => Text;
}
