using Microsoft.Extensions.Logging;

namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  Event model & options
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Discriminates the kind of event raised by an <see cref="Agent"/> during a turn.</summary>
public enum AgentEventKind
{
    /// <summary>The user's input message was recorded.</summary>
    UserInput,
    /// <summary>The effective system prompt / instructions were dispatched to the model.</summary>
    SystemPrompt,
    /// <summary>An MCP tool server was declared for this request.</summary>
    ToolDeclaration,
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
    WorkflowCompleted
}

/// <summary>An event raised by the <see cref="Agent"/> and delivered to <see cref="AgentOptions.OnEvent"/> and <see cref="AgentOptions.Logger"/>.</summary>
public sealed class AgentEvent
{
    /// <summary>The kind of event that occurred.</summary>
    public required AgentEventKind Kind { get; init; }
    /// <summary>Name of the tool involved (set for <see cref="AgentEventKind.ToolCall"/> and <see cref="AgentEventKind.ToolResult"/>).</summary>
    public string? ToolName { get; init; }
    /// <summary>JSON-encoded tool arguments (set for <see cref="AgentEventKind.ToolCall"/>).</summary>
    public string? Arguments { get; init; }
    /// <summary>Text payload — delta text, tool result, answer, system prompt, or compaction summary depending on <see cref="Kind"/>.</summary>
    public string? Text { get; init; }
    /// <summary>The turn index within the current conversation.</summary>
    public int Round { get; init; }
    /// <summary>Populated on <see cref="AgentEventKind.Answer"/> when the API returns usage data.</summary>
    public int? InputTokens { get; init; }
    /// <summary>Output token count (see <see cref="InputTokens"/>).</summary>
    public int? OutputTokens { get; init; }
    /// <summary>Total token count (see <see cref="InputTokens"/>).</summary>
    public int? TotalTokens { get; init; }
    /// <summary>MCP server label (set for <see cref="AgentEventKind.ToolDeclaration"/>).</summary>
    public string? ServerLabel { get; init; }
    /// <summary>MCP server URL (set for <see cref="AgentEventKind.ToolDeclaration"/>).</summary>
    public string? ServerUrl { get; init; }
    /// <summary>Allowed tool names for this server (set for <see cref="AgentEventKind.ToolDeclaration"/>; <c>null</c> = all tools).</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }
}

/// <summary>Configuration options for an <see cref="Agent"/> instance.</summary>
public sealed class AgentOptions
{
    /// <summary>System prompt prepended to every request. <c>null</c> means no system instruction.</summary>
    public string? SystemPrompt { get; set; }
    /// <summary>
    /// Inference parameters (temperature, top-p, top-k, etc.) for every call made by this agent.
    /// Overrides <see cref="LMConfig.Inference"/> when set; <c>null</c> falls back to the LM-level default.
    /// </summary>
    public InferenceConfig? Inference { get; set; }
    /// <summary>Callback invoked for every <see cref="AgentEvent"/> during a turn.</summary>
    public Action<AgentEvent>? OnEvent { get; set; }
    /// <summary>
    /// Reasoning effort for every call made by this agent.
    /// Overrides <see cref="LMConfig.Reasoning"/> when set; <c>null</c> falls back to the LM-level default.
    /// </summary>
    public ReasoningEffort? Reasoning { get; set; }
    /// <summary>
    /// Agent-level model override. Accepts a named alias from <see cref="LMConfig.Models"/> or a literal
    /// model ID. <c>null</c> falls back to <see cref="LMConfig.ModelName"/>.
    /// Overridable per-call via the <c>model</c> parameter on each agent method.
    /// </summary>
    public string? Model { get; set; }
    /// <summary>
    /// Optional logger. When set, every <see cref="AgentEvent"/> is automatically written using structured
    /// log messages — no manual <see cref="OnEvent"/> switch required.
    /// <see cref="AgentEventKind.TextDelta"/> is written at <c>Trace</c> level; all others at <c>Debug</c>
    /// or <c>Information</c> level so standard log-level filters keep the output clean.
    /// </summary>
    public ILogger? Logger { get; set; }
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
