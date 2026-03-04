using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  Agent
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Multi-turn LLM agent with streaming, MCP tool orchestration, workflow execution,
/// and optional context compaction.
/// </summary>
public sealed class Agent : IAsyncDisposable
{
    private readonly LM _lm;
    private string? _lastResponseId;

    /// <summary>The tool registry used by this agent. Pre-populated tools are sent to MCP servers.</summary>
    public ToolRegistry Tools { get; }
    /// <summary>Configuration options supplied at construction time.</summary>
    public AgentOptions Options { get; }
    /// <summary>Context manager responsible for token tracking and compaction. <c>null</c> when compaction is disabled.</summary>
    public ContextManager? Context { get; }

    /// <summary>Initialises a new agent with its own internal <see cref="ToolRegistry"/>.</summary>
    /// <param name="lm">The LM client used for all API calls.</param>
    /// <param name="options">Agent options; a default instance is used when <c>null</c>.</param>
    public Agent(LM lm, AgentOptions? options = null) { _lm = lm; Tools = new(); Options = options ?? new(); Context = Options.Compaction is not null ? new ContextManager(Options.Compaction) : null; }
    /// <summary>Initialises a new agent with a shared <see cref="ToolRegistry"/>.</summary>
    /// <param name="lm">The LM client used for all API calls.</param>
    /// <param name="tools">A shared tool registry (e.g. injected via DI).</param>
    /// <param name="options">Agent options; a default instance is used when <c>null</c>.</param>
    public Agent(LM lm, ToolRegistry tools, AgentOptions? options = null) { _lm = lm; Tools = tools; Options = options ?? new(); Context = Options.Compaction is not null ? new ContextManager(Options.Compaction) : null; }

    /// <summary>Registers a tool set and returns this agent for fluent chaining.</summary>
    /// <typeparam name="T">The tool set type.</typeparam>
    /// <param name="toolSet">The tool set instance to register.</param>
    /// <param name="replaceExisting">When <c>true</c>, any prior registration of the same type is replaced.</param>
    public Agent RegisterTools<T>(T toolSet, bool replaceExisting = false) where T : IAgentToolSet
    { Tools.Register(toolSet, replaceExisting); return this; }

    /// <summary>Clears the response-chain ID and resets the context manager, starting a fresh conversation.</summary>
    public void ResetConversation() { _lastResponseId = null; Context?.Reset(); }

    /// <summary>
    /// Manually trigger context compaction. Compresses older conversation history
    /// into a structured checkpoint and resets the response chain so the next call
    /// replays only the checkpoint + hot tail.
    /// </summary>
    public async Task<Checkpoint?> CompactAsync(CompactionLevel? level = null,
        int? targetTokens = null, CancellationToken ct = default)
    {
        if (Context is null) return null;
        var checkpoint = await Context.CompactAsync(_lm, level, targetTokens, ct);
        if (checkpoint is null) return null;
        _lastResponseId = null;
        Emit(AgentEventKind.Compacted,
            text: $"Compacted to {checkpoint.Level} checkpoint (#{checkpoint.CompactionCount})");
        return checkpoint;
    }

    /// <summary>Single-shot /v1/responses request with MCP tools. Does not maintain conversation history.</summary>
    /// <param name="input">The user message to send.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="thinking">Per-call thinking override. Overrides <see cref="AgentOptions.Thinking"/> when set.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> RunAsync(
        string input, string mcpServerUrl, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ThinkingConfig? thinking = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: input);
        var resp = await _lm.RespondAsync(input,
            instructions: Options.SystemPrompt, temperature: Options.Temperature,
            tools: [ToolDefinition.Mcp(serverLabel ?? "agentic", mcpServerUrl, allowedTools, mcpHeaders)],
            thinking: thinking ?? Options.Thinking, ct: ct);
        return ParseOutput(resp);
    }

    /// <summary>Single-shot /v1/responses with text and images. Does not maintain conversation history.</summary>
    /// <param name="text">The user message text.</param>
    /// <param name="images">Image URLs or base64 data URLs.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="thinking">Per-call thinking override. Overrides <see cref="AgentOptions.Thinking"/> when set.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> RunAsync(
        string text, IEnumerable<string> images, string mcpServerUrl, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ThinkingConfig? thinking = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: text);
        var resp = await _lm.RespondAsync([ResponseInput.User(text, images)],
            instructions: Options.SystemPrompt, temperature: Options.Temperature,
            tools: [ToolDefinition.Mcp(serverLabel ?? "agentic", mcpServerUrl, allowedTools, mcpHeaders)],
            thinking: thinking ?? Options.Thinking, ct: ct);
        return ParseOutput(resp);
    }

    /// <summary>Multi-turn /v1/responses with <c>previous_response_id</c> chaining. Maintains conversation history across calls.</summary>
    /// <param name="input">The user message for this turn.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="thinking">Per-call thinking override. Overrides <see cref="AgentOptions.Thinking"/> when set.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> ChatAsync(
        string input, string mcpServerUrl, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ThinkingConfig? thinking = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: input);
        if (Context?.ShouldCompact == true) await CompactAsync(ct: ct);

        var instructions = Context?.GetEffectiveSystemPrompt(Options.SystemPrompt) ?? Options.SystemPrompt;
        var tools = new List<ToolDefinition> { ToolDefinition.Mcp(serverLabel ?? "agentic", mcpServerUrl, allowedTools, mcpHeaders) };
        var effectiveThinking = thinking ?? Options.Thinking;

        ResponseResponse resp;
        if (Context is not null && _lastResponseId is null && Context.IsCheckpointed)
        {
            var inputs = Context.GetHotTailAsInput();
            inputs.Add(new ResponseInput { Role = "user", Content = input });
            resp = await _lm.RespondAsync(inputs, instructions: instructions,
                temperature: Options.Temperature, tools: tools, thinking: effectiveThinking, ct: ct);
        }
        else
        {
            resp = await _lm.RespondAsync(input, instructions: instructions,
                previousResponseId: _lastResponseId, temperature: Options.Temperature,
                tools: tools, thinking: effectiveThinking, ct: ct);
        }

        _lastResponseId = resp.ResponseId;
        var result = ParseOutput(resp);
        RecordResponse(input, result);
        return result;
    }

    /// <summary>Multi-turn /v1/responses with text and images. Maintains conversation history across calls.</summary>
    /// <param name="text">The user message text.</param>
    /// <param name="images">Image URLs or base64 data URLs.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="thinking">Per-call thinking override. Overrides <see cref="AgentOptions.Thinking"/> when set.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> ChatAsync(
        string text, IEnumerable<string> images, string mcpServerUrl, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ThinkingConfig? thinking = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: text);
        if (Context?.ShouldCompact == true) await CompactAsync(ct: ct);

        var instructions   = Context?.GetEffectiveSystemPrompt(Options.SystemPrompt) ?? Options.SystemPrompt;
        var tools          = new List<ToolDefinition> { ToolDefinition.Mcp(serverLabel ?? "agentic", mcpServerUrl, allowedTools, mcpHeaders) };
        var effectiveThinking = thinking ?? Options.Thinking;
        var userInput      = ResponseInput.User(text, images);

        ResponseResponse resp;
        if (Context is not null && _lastResponseId is null && Context.IsCheckpointed)
        {
            var inputs = Context.GetHotTailAsInput();
            inputs.Add(userInput);
            resp = await _lm.RespondAsync(inputs, instructions: instructions,
                temperature: Options.Temperature, tools: tools, thinking: effectiveThinking, ct: ct);
        }
        else
        {
            resp = await _lm.RespondAsync([userInput], instructions: instructions,
                previousResponseId: _lastResponseId, temperature: Options.Temperature,
                tools: tools, thinking: effectiveThinking, ct: ct);
        }

        _lastResponseId = resp.ResponseId;
        var result = ParseOutput(resp);
        RecordResponse(text, result);
        return result;
    }

    /// <summary>Single-shot streaming /v1/responses with real-time events. Does not maintain conversation history.</summary>
    /// <param name="input">The user message to send.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="thinking">Per-call thinking override. Overrides <see cref="AgentOptions.Thinking"/> when set.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> RunStreamAsync(
        string input, string mcpServerUrl, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ThinkingConfig? thinking = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: input);
        return await ConsumeStreamAsync(_lm.RespondStreamingAsync(input,
            instructions: Options.SystemPrompt, temperature: Options.Temperature,
            tools: [ToolDefinition.Mcp(serverLabel ?? "agentic", mcpServerUrl, allowedTools, mcpHeaders)],
            thinking: thinking ?? Options.Thinking, ct: ct));
    }

    /// <summary>Single-shot streaming /v1/responses with text and images, with real-time events. Does not maintain conversation history.</summary>
    /// <param name="text">The user message text.</param>
    /// <param name="images">Image URLs or base64 data URLs.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="thinking">Per-call thinking override. Overrides <see cref="AgentOptions.Thinking"/> when set.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> RunStreamAsync(
        string text, IEnumerable<string> images, string mcpServerUrl, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ThinkingConfig? thinking = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: text);
        return await ConsumeStreamAsync(_lm.RespondStreamingAsync([ResponseInput.User(text, images)],
            instructions: Options.SystemPrompt, temperature: Options.Temperature,
            tools: [ToolDefinition.Mcp(serverLabel ?? "agentic", mcpServerUrl, allowedTools, mcpHeaders)],
            thinking: thinking ?? Options.Thinking, ct: ct));
    }

    /// <summary>Multi-turn streaming /v1/responses with <c>previous_response_id</c> chaining and real-time events.</summary>
    /// <param name="input">The user message for this turn.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="thinking">Per-call thinking override. Overrides <see cref="AgentOptions.Thinking"/> when set.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> ChatStreamAsync(
        string input, string mcpServerUrl, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ThinkingConfig? thinking = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: input);
        if (Context?.ShouldCompact == true) await CompactAsync(ct: ct);

        var instructions = Context?.GetEffectiveSystemPrompt(Options.SystemPrompt) ?? Options.SystemPrompt;
        var tools = new List<ToolDefinition> { ToolDefinition.Mcp(serverLabel ?? "agentic", mcpServerUrl, allowedTools, mcpHeaders) };
        var effectiveThinking = thinking ?? Options.Thinking;

        AgentResponse response;
        if (Context is not null && _lastResponseId is null && Context.IsCheckpointed)
        {
            var inputs = Context.GetHotTailAsInput();
            inputs.Add(new ResponseInput { Role = "user", Content = input });
            response = await ConsumeStreamAsync(_lm.RespondStreamingAsync(inputs,
                instructions: instructions, temperature: Options.Temperature,
                tools: tools, thinking: effectiveThinking, ct: ct));
        }
        else
        {
            response = await ConsumeStreamAsync(_lm.RespondStreamingAsync(input,
                instructions: instructions, previousResponseId: _lastResponseId,
                temperature: Options.Temperature, tools: tools, thinking: effectiveThinking, ct: ct));
        }

        RecordResponse(input, response);
        return response;
    }

    /// <summary>Multi-turn streaming /v1/responses with text and images, with real-time events.</summary>
    /// <param name="text">The user message text.</param>
    /// <param name="images">Image URLs or base64 data URLs.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="thinking">Per-call thinking override. Overrides <see cref="AgentOptions.Thinking"/> when set.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> ChatStreamAsync(
        string text, IEnumerable<string> images, string mcpServerUrl, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ThinkingConfig? thinking = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: text);
        if (Context?.ShouldCompact == true) await CompactAsync(ct: ct);

        var instructions      = Context?.GetEffectiveSystemPrompt(Options.SystemPrompt) ?? Options.SystemPrompt;
        var tools             = new List<ToolDefinition> { ToolDefinition.Mcp(serverLabel ?? "agentic", mcpServerUrl, allowedTools, mcpHeaders) };
        var effectiveThinking = thinking ?? Options.Thinking;
        var userInput         = ResponseInput.User(text, images);

        AgentResponse response;
        if (Context is not null && _lastResponseId is null && Context.IsCheckpointed)
        {
            var inputs = Context.GetHotTailAsInput();
            inputs.Add(userInput);
            response = await ConsumeStreamAsync(_lm.RespondStreamingAsync(inputs,
                instructions: instructions, temperature: Options.Temperature,
                tools: tools, thinking: effectiveThinking, ct: ct));
        }
        else
        {
            response = await ConsumeStreamAsync(_lm.RespondStreamingAsync([userInput],
                instructions: instructions, previousResponseId: _lastResponseId,
                temperature: Options.Temperature, tools: tools, thinking: effectiveThinking, ct: ct));
        }

        RecordResponse(text, response);
        return response;
    }

    // ── Workflow execution ────────────────────────────────────────────────

    /// <summary>
    /// Executes a multi-step workflow. The agent is guided through the defined steps
    /// and each step is verified before advancing. Uses streaming for real-time output.
    /// </summary>
    /// <param name="workflow">The workflow definition containing ordered steps.</param>
    /// <param name="input">The initial user message that starts the workflow.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="thinking">Per-call thinking override. Overrides <see cref="AgentOptions.Thinking"/> when set.</param>
    /// <param name="maxRounds">Maximum number of model turns before the workflow is aborted.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<WorkflowResult> RunWorkflowAsync(
        Workflow workflow, string input, string mcpServerUrl,
        string? serverLabel = null, List<string>? allowedTools = null,
        Dictionary<string, string>? mcpHeaders = null,
        ThinkingConfig? thinking = null, int maxRounds = 10, CancellationToken ct = default)
    {
        workflow.Reset();
        Emit(AgentEventKind.UserInput, text: input);

        var allInvocations = new List<ToolInvocation>();
        var allText = new StringBuilder();
        var systemPrompt = BuildWorkflowPrompt(workflow, Options.SystemPrompt);
        var mcpTools = new List<ToolDefinition> { ToolDefinition.Mcp(serverLabel ?? "agentic", mcpServerUrl, allowedTools, mcpHeaders) };
        var currentInput = input;
        var effectiveThinking = thinking ?? Options.Thinking;

        for (int round = 0; round < maxRounds; round++)
        {
            var response = await ConsumeStreamAsync(
                _lm.RespondStreamingAsync(currentInput,
                    instructions: systemPrompt, previousResponseId: _lastResponseId,
                    temperature: Options.Temperature, tools: mcpTools,
                    thinking: effectiveThinking, ct: ct));

            allInvocations.AddRange(response.ToolInvocations);
            if (allText.Length > 0) allText.AppendLine();
            allText.Append(response.Text);

            var ctx = new WorkflowContext
            {
                ToolInvocations = allInvocations,
                ResponseText = allText.ToString(),
            };
            await VerifyStepsAsync(workflow, ctx);

            if (workflow.Steps.All(s => s.Status == WorkflowStepStatus.Completed))
            {
                Emit(AgentEventKind.WorkflowCompleted, text: workflow.Name);
                break;
            }

            var pending = workflow.Steps.Where(s => s.Status != WorkflowStepStatus.Completed);
            currentInput = $"Continue with the remaining workflow steps: {string.Join(", ", pending.Select(s => s.Name))}";
        }

        return new()
        {
            Text = allText.ToString(),
            Completed = workflow.Steps.All(s => s.Status == WorkflowStepStatus.Completed),
            Steps = workflow.Steps.ToList(),
            ToolInvocations = allInvocations,
        };
    }

    private async Task VerifyStepsAsync(Workflow workflow, WorkflowContext ctx)
    {
        foreach (var step in workflow.Steps)
        {
            if (step.Status == WorkflowStepStatus.Completed) continue;

            var passed = step.Verify is null || await step.Verify(ctx);
            if (passed)
            {
                step.Status = WorkflowStepStatus.Completed;
                Emit(AgentEventKind.StepCompleted, text: step.Name);
            }
            else
            {
                break; // steps are verified in order — stop at first incomplete
            }
        }
    }

    private static string BuildWorkflowPrompt(Workflow workflow, string? basePrompt)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(basePrompt))
            sb.AppendLine(basePrompt).AppendLine();

        sb.AppendLine($"## Workflow: {workflow.Name}");
        sb.AppendLine("Complete the following steps IN ORDER. Do not skip steps.");
        sb.AppendLine("After each step, confirm what you did before proceeding.\n");

        for (int i = 0; i < workflow.Steps.Count; i++)
            sb.AppendLine($"{i + 1}. [{workflow.Steps[i].Name}] {workflow.Steps[i].Instruction}");

        return sb.ToString();
    }

    // ── Stream consumption ────────────────────────────────────────────────

    private async Task<AgentResponse> ConsumeStreamAsync(IAsyncEnumerable<StreamEvent> events)
    {
        var textBuilder = new StringBuilder();
        var invocations = new List<ToolInvocation>();
        var pendingTools      = new Dictionary<int, string>();  // output_index → tool name
        var emittedToolCalls  = new HashSet<int>();             // indexes where ToolCall was already fired
        ResponseUsage? usage = null;

        await foreach (var evt in events)
        {
            switch (evt.Type)
            {
                case "response.output_text.delta":
                    Emit(AgentEventKind.TextDelta, text: evt.Delta);
                    textBuilder.Append(evt.Delta);
                    break;

                case "response.output_item.added" when evt.Item?.Type is "mcp_call":
                    pendingTools[evt.OutputIndex] = evt.Item.Tool ?? evt.Item.Name ?? "";
                    break;

                // Some LM servers never send this event — handled as a fallback below.
                case "response.mcp_call.arguments.done":
                    if (pendingTools.TryGetValue(evt.OutputIndex, out var toolName))
                    {
                        Emit(AgentEventKind.ToolCall, toolName: toolName, args: evt.Arguments);
                        emittedToolCalls.Add(evt.OutputIndex);
                    }
                    break;

                case "response.output_item.done" when evt.Item?.Type is "mcp_call":
                    var tn = evt.Item.Tool ?? evt.Item.Name ?? "";
                    var ta = evt.Item.Arguments ?? "";
                    var to = UnwrapMcpOutput(evt.Item.Output);
                    // Emit ToolCall here if arguments.done never fired for this index.
                    if (!emittedToolCalls.Remove(evt.OutputIndex))
                        Emit(AgentEventKind.ToolCall, toolName: tn, args: ta);
                    Emit(AgentEventKind.ToolResult, toolName: tn, text: to);
                    invocations.Add(new() { Name = tn, Arguments = ta, Result = to });
                    pendingTools.Remove(evt.OutputIndex);
                    break;

                case "response.output_item.done" when evt.Item?.Type is "function_call":
                    var fn = evt.Item.Name ?? ""; var fa = evt.Item.Arguments ?? "";
                    Emit(AgentEventKind.ToolCall, toolName: fn, args: fa);
                    invocations.Add(new() { Name = fn, Arguments = fa, Result = "" });
                    break;

                case "response.completed":
                    _lastResponseId = evt.Response?.Id ?? evt.Response?.ResponseId;
                    usage = evt.Response?.Usage;
                    break;
            }
        }

        var fullText = textBuilder.ToString();
        Options.OnEvent?.Invoke(new()
        {
            Kind = AgentEventKind.Answer, Text = fullText,
            InputTokens  = usage?.InputTokens,
            OutputTokens = usage?.OutputTokens,
            TotalTokens  = usage?.TotalTokens,
        });
        return new() { Text = fullText, ToolInvocations = invocations, Usage = usage };
    }

    private AgentResponse ParseOutput(ResponseResponse response)
    {
        var invocations = new List<ToolInvocation>();
        var text = new List<string>();

        foreach (var item in response.Output)
        {
            switch (item.Type)
            {
                case "message":
                    var parts = item.Content?.Where(c => c.Type == "output_text").Select(c => c.Text!).Where(t => t is not null).ToList() ?? [];
                    if (parts.Count > 0)
                    {
                        var chunk = string.Join("", parts);
                        text.Add(chunk);
                        Emit(AgentEventKind.Reasoning, text: chunk);
                    }
                    break;
                case "mcp_call":
                    var tool = item.Tool ?? item.Name ?? ""; var a = item.Arguments ?? ""; var o = UnwrapMcpOutput(item.Output);
                    Emit(AgentEventKind.ToolCall, toolName: tool, args: a);
                    Emit(AgentEventKind.ToolResult, toolName: tool, text: o);
                    invocations.Add(new() { Name = tool, Arguments = a, Result = o });
                    break;
                case "function_call":
                    var fn = item.Name ?? ""; var fa = item.Arguments ?? "";
                    Emit(AgentEventKind.ToolCall, toolName: fn, args: fa);
                    invocations.Add(new() { Name = fn, Arguments = fa, Result = "" });
                    break;
            }
        }
        var ft = string.Join("", text);
        Emit(AgentEventKind.Answer, text: ft);
        return new() { Text = ft, ToolInvocations = invocations, Usage = response.Usage };
    }

    private void RecordResponse(string input, AgentResponse response)
    {
        if (Context is null) return;
        Context.RecordUserInput(input);
        foreach (var inv in response.ToolInvocations)
        {
            Context.RecordToolCall(inv.Name, inv.Arguments);
            Context.RecordToolResult(inv.Name, inv.Result);
        }
        Context.RecordAssistantResponse(response.Text);
        if (response.Usage is not null)
            Context.UpdateTokenUsage(response.Usage.InputTokens, response.Usage.OutputTokens, response.Usage.TotalTokens);
    }

    private void Emit(AgentEventKind kind, string? text = null, string? toolName = null, string? args = null)
        => Options.OnEvent?.Invoke(new() { Kind = kind, Text = text, ToolName = toolName, Arguments = args });

    /// <summary>
    /// MCP tool output arrives as a JSON content array: [{"type":"text","text":"..."}].
    /// Extract and join all text parts; fall back to the raw string when parsing fails.
    /// </summary>
    private static string UnwrapMcpOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        var trimmed = output.TrimStart();
        if (trimmed[0] is not '[' and not '{') return output;
        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var parts = root.EnumerateArray()
                    .Where(el => el.TryGetProperty("type", out var t) && t.GetString() == "text"
                              && el.TryGetProperty("text", out _))
                    .Select(el => el.GetProperty("text").GetString() ?? "");
                var joined = string.Join("\n", parts);
                return joined.Length > 0 ? joined : output;
            }
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type) && type.GetString() == "text"
                && root.TryGetProperty("text", out var text))
                return text.GetString() ?? output;
        }
        catch { }
        return output;
    }

    public async ValueTask DisposeAsync()
    { foreach (var ts in Tools.ToolSets) if (ts is IDisposableToolSet d) await d.DisposeAsync(); }
}
