using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  Agent
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Multi-turn LLM agent with streaming, MCP tool orchestration, and workflow execution.
/// </summary>
public sealed class Agent : IAsyncDisposable
{
    private readonly ILLMBackend _lm;
    private string? _lastResponseId;

    /// <summary>The tool registry used by this agent. Pre-populated tools are sent to MCP servers.</summary>
    public ToolRegistry Tools { get; }
    /// <summary>Configuration options supplied at construction time.</summary>
    public AgentOptions Options { get; }

    /// <summary>Initialises a new agent with its own internal <see cref="ToolRegistry"/>.</summary>
    /// <param name="lm">The LM client used for all API calls.</param>
    /// <param name="options">Agent options; a default instance is used when <c>null</c>.</param>
    public Agent(ILLMBackend lm, AgentOptions? options = null) { _lm = lm; Tools = new(); Options = options ?? new(); }
    /// <summary>Initialises a new agent with a shared <see cref="ToolRegistry"/>.</summary>
    /// <param name="lm">The LM client used for all API calls.</param>
    /// <param name="tools">A shared tool registry (e.g. injected via DI).</param>
    /// <param name="options">Agent options; a default instance is used when <c>null</c>.</param>
    public Agent(ILLMBackend lm, ToolRegistry tools, AgentOptions? options = null) { _lm = lm; Tools = tools; Options = options ?? new(); }

    /// <summary>Registers a tool set and returns this agent for fluent chaining.</summary>
    /// <typeparam name="T">The tool set type.</typeparam>
    /// <param name="toolSet">The tool set instance to register.</param>
    /// <param name="replaceExisting">When <c>true</c>, any prior registration of the same type is replaced.</param>
    public Agent RegisterTools<T>(T toolSet, bool replaceExisting = false) where T : IAgentToolSet
    { Tools.Register(toolSet, replaceExisting); return this; }

    /// <summary>Clears the response-chain ID, starting a fresh conversation.</summary>
    public void ResetConversation() { _lastResponseId = null; }

    /// <summary>Single-shot
    /// <param name="input">The user message to send.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="reasoning">Per-call reasoning effort override. Overrides <see cref="AgentOptions.Reasoning"/> when set.</param>
    /// <param name="inference">Per-call inference override. Overrides <see cref="AgentOptions.Inference"/> when set.</param>
    /// <param name="model">Per-call model override. Accepts a named alias from <see cref="LMConfig.Models"/> or a literal model ID; <c>null</c> falls back to <see cref="AgentOptions.Model"/> then <see cref="LMConfig.ModelName"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> RunAsync(
        string input, string? mcpServerUrl = null, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ReasoningEffort? reasoning = null, InferenceConfig? inference = null,
        string? model = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: input);
        var effectiveModel     = model ?? Options.Model;
        var effectiveInference = inference ?? Options.Inference;
        var tools              = BuildToolDefinitions(mcpServerUrl, serverLabel, allowedTools, mcpHeaders);
        EmitRequestContext(Options.SystemPrompt, tools);
        var resp = await _lm.RespondAsync(input,
            instructions: Options.SystemPrompt, inference: effectiveInference,
            tools: tools,
            reasoning: reasoning ?? Options.Reasoning, model: effectiveModel, ct: ct);
        return ParseOutput(resp);
    }

    /// <summary>Single-shot /v1/responses with text and images. Does not maintain conversation history.</summary>
    /// <param name="text">The user message text.</param>
    /// <param name="images">Image URLs or base64 data URLs.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="reasoning">Per-call reasoning effort override. Overrides <see cref="AgentOptions.Reasoning"/> when set.</param>
    /// <param name="inference">Per-call inference override. Overrides <see cref="AgentOptions.Inference"/> when set.</param>
    /// <param name="model">Per-call model override. Accepts a named alias from <see cref="LMConfig.Models"/> or a literal model ID; <c>null</c> falls back to <see cref="AgentOptions.Model"/> then <see cref="LMConfig.ModelName"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> RunAsync(
        string text, IEnumerable<string> images, string? mcpServerUrl = null, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ReasoningEffort? reasoning = null, InferenceConfig? inference = null,
        string? model = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: text);
        var effectiveModel     = model ?? Options.Model;
        var effectiveInference = inference ?? Options.Inference;
        var tools              = BuildToolDefinitions(mcpServerUrl, serverLabel, allowedTools, mcpHeaders);
        EmitRequestContext(Options.SystemPrompt, tools);
        var resp = await _lm.RespondAsync([ResponseInput.User(text, images)],
            instructions: Options.SystemPrompt, inference: effectiveInference,
            tools: tools,
            reasoning: reasoning ?? Options.Reasoning, model: effectiveModel, ct: ct);
        return ParseOutput(resp);
    }

    /// <summary>Multi-turn /v1/responses with <c>previous_response_id</c> chaining. Maintains conversation history across calls.</summary>
    /// <param name="input">The user message for this turn.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="reasoning">Per-call reasoning effort override. Overrides <see cref="AgentOptions.Reasoning"/> when set.</param>
    /// <param name="inference">Per-call inference override. Overrides <see cref="AgentOptions.Inference"/> when set.</param>
    /// <param name="model">Per-call model override. Accepts a named alias from <see cref="LMConfig.Models"/> or a literal model ID; <c>null</c> falls back to <see cref="AgentOptions.Model"/> then <see cref="LMConfig.ModelName"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> ChatAsync(
        string input, string? mcpServerUrl = null, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ReasoningEffort? reasoning = null, InferenceConfig? inference = null,
        string? model = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: input);
        var tools              = BuildToolDefinitions(mcpServerUrl, serverLabel, allowedTools, mcpHeaders);
        var effectiveReasoning = reasoning ?? Options.Reasoning;
        var effectiveModel     = model ?? Options.Model;
        var effectiveInference = inference ?? Options.Inference;
        EmitRequestContext(Options.SystemPrompt, tools);
        var resp = await _lm.RespondAsync(input, instructions: Options.SystemPrompt,
            previousResponseId: _lastResponseId, inference: effectiveInference,
            tools: tools, reasoning: effectiveReasoning, model: effectiveModel, ct: ct);
        _lastResponseId = resp.ResponseId;
        return ParseOutput(resp);
    }

    /// <summary>Multi-turn /v1/responses with text and images.
    /// <param name="text">The user message text.</param>
    /// <param name="images">Image URLs or base64 data URLs.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="reasoning">Per-call reasoning effort override. Overrides <see cref="AgentOptions.Reasoning"/> when set.</param>
    /// <param name="inference">Per-call inference override. Overrides <see cref="AgentOptions.Inference"/> when set.</param>
    /// <param name="model">Per-call model override. Accepts a named alias from <see cref="LMConfig.Models"/> or a literal model ID; <c>null</c> falls back to <see cref="AgentOptions.Model"/> then <see cref="LMConfig.ModelName"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> ChatAsync(
        string text, IEnumerable<string> images, string? mcpServerUrl = null, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ReasoningEffort? reasoning = null, InferenceConfig? inference = null,
        string? model = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: text);
        var tools              = BuildToolDefinitions(mcpServerUrl, serverLabel, allowedTools, mcpHeaders);
        var effectiveReasoning = reasoning ?? Options.Reasoning;
        var effectiveModel     = model ?? Options.Model;
        var effectiveInference = inference ?? Options.Inference;
        var userInput          = ResponseInput.User(text, images);
        EmitRequestContext(Options.SystemPrompt, tools);
        var resp = await _lm.RespondAsync([userInput], instructions: Options.SystemPrompt,
            previousResponseId: _lastResponseId, inference: effectiveInference,
            tools: tools, reasoning: effectiveReasoning, model: effectiveModel, ct: ct);
        _lastResponseId = resp.ResponseId;
        return ParseOutput(resp);
    }

    /// <summary>Single-shot streaming
    /// <param name="input">The user message to send.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="reasoning">Per-call reasoning effort override. Overrides <see cref="AgentOptions.Reasoning"/> when set.</param>
    /// <param name="inference">Per-call inference override. Overrides <see cref="AgentOptions.Inference"/> when set.</param>
    /// <param name="model">Per-call model override. Accepts a named alias from <see cref="LMConfig.Models"/> or a literal model ID; <c>null</c> falls back to <see cref="AgentOptions.Model"/> then <see cref="LMConfig.ModelName"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> RunStreamAsync(
        string input, string? mcpServerUrl = null, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ReasoningEffort? reasoning = null, InferenceConfig? inference = null,
        string? model = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: input);
        var effectiveModel     = model ?? Options.Model;
        var effectiveInference = inference ?? Options.Inference;
        var tools              = BuildToolDefinitions(mcpServerUrl, serverLabel, allowedTools, mcpHeaders);
        EmitRequestContext(Options.SystemPrompt, tools);
        return await ConsumeStreamAsync(_lm.RespondStreamingAsync(input,
            instructions: Options.SystemPrompt, inference: effectiveInference,
            tools: tools,
            reasoning: reasoning ?? Options.Reasoning, model: effectiveModel, ct: ct));
    }

    /// <summary>Single-shot streaming /v1/responses with text and images, with real-time events. Does not maintain conversation history.</summary>
    /// <param name="text">The user message text.</param>
    /// <param name="images">Image URLs or base64 data URLs.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="reasoning">Per-call reasoning effort override. Overrides <see cref="AgentOptions.Reasoning"/> when set.</param>
    /// <param name="inference">Per-call inference override. Overrides <see cref="AgentOptions.Inference"/> when set.</param>
    /// <param name="model">Per-call model override. Accepts a named alias from <see cref="LMConfig.Models"/> or a literal model ID; <c>null</c> falls back to <see cref="AgentOptions.Model"/> then <see cref="LMConfig.ModelName"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> RunStreamAsync(
        string text, IEnumerable<string> images, string? mcpServerUrl = null, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ReasoningEffort? reasoning = null, InferenceConfig? inference = null,
        string? model = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: text);
        var effectiveModel     = model ?? Options.Model;
        var effectiveInference = inference ?? Options.Inference;
        var tools              = BuildToolDefinitions(mcpServerUrl, serverLabel, allowedTools, mcpHeaders);
        EmitRequestContext(Options.SystemPrompt, tools);
        return await ConsumeStreamAsync(_lm.RespondStreamingAsync([ResponseInput.User(text, images)],
            instructions: Options.SystemPrompt, inference: effectiveInference,
            tools: tools,
            reasoning: reasoning ?? Options.Reasoning, model: effectiveModel, ct: ct));
    }

    /// <summary>Multi-turn streaming /v1/responses with <c>previous_response_id</c> chaining and real-time events.</summary>
    /// <param name="input">The user message for this turn.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="reasoning">Per-call reasoning effort override. Overrides <see cref="AgentOptions.Reasoning"/> when set.</param>
    /// <param name="inference">Per-call inference override. Overrides <see cref="AgentOptions.Inference"/> when set.</param>
    /// <param name="model">Per-call model override. Accepts a named alias from <see cref="LMConfig.Models"/> or a literal model ID; <c>null</c> falls back to <see cref="AgentOptions.Model"/> then <see cref="LMConfig.ModelName"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> ChatStreamAsync(
        string input, string? mcpServerUrl = null, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ReasoningEffort? reasoning = null, InferenceConfig? inference = null,
        string? model = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: input);
        var tools              = BuildToolDefinitions(mcpServerUrl, serverLabel, allowedTools, mcpHeaders);
        var effectiveReasoning = reasoning ?? Options.Reasoning;
        var effectiveModel     = model ?? Options.Model;
        var effectiveInference = inference ?? Options.Inference;
        EmitRequestContext(Options.SystemPrompt, tools);
        return await ConsumeStreamAsync(_lm.RespondStreamingAsync(input,
            instructions: Options.SystemPrompt, previousResponseId: _lastResponseId,
            inference: effectiveInference, tools: tools, reasoning: effectiveReasoning,
            model: effectiveModel, ct: ct));
    }

    /// <summary>Multi-turn streaming /v1/responses with text and images
    /// <param name="text">The user message text.</param>
    /// <param name="images">Image URLs or base64 data URLs.</param>
    /// <param name="mcpServerUrl">URL of the MCP server that supplies tools.</param>
    /// <param name="serverLabel">Optional label for the MCP server; defaults to <c>"agentic"</c>.</param>
    /// <param name="allowedTools">Optional allow-list of tool names; <c>null</c> means all tools.</param>
    /// <param name="mcpHeaders">Optional HTTP headers forwarded to the MCP server.</param>
    /// <param name="reasoning">Per-call reasoning effort override. Overrides <see cref="AgentOptions.Reasoning"/> when set.</param>
    /// <param name="inference">Per-call inference override. Overrides <see cref="AgentOptions.Inference"/> when set.</param>
    /// <param name="model">Per-call model override. Accepts a named alias from <see cref="LMConfig.Models"/> or a literal model ID; <c>null</c> falls back to <see cref="AgentOptions.Model"/> then <see cref="LMConfig.ModelName"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AgentResponse> ChatStreamAsync(
        string text, IEnumerable<string> images, string? mcpServerUrl = null, string? serverLabel = null,
        List<string>? allowedTools = null, Dictionary<string, string>? mcpHeaders = null,
        ReasoningEffort? reasoning = null, InferenceConfig? inference = null,
        string? model = null, CancellationToken ct = default)
    {
        Emit(AgentEventKind.UserInput, text: text);
        var tools              = BuildToolDefinitions(mcpServerUrl, serverLabel, allowedTools, mcpHeaders);
        var effectiveReasoning = reasoning ?? Options.Reasoning;
        var effectiveModel     = model ?? Options.Model;
        var effectiveInference = inference ?? Options.Inference;
        var userInput          = ResponseInput.User(text, images);
        EmitRequestContext(Options.SystemPrompt, tools);
        return await ConsumeStreamAsync(_lm.RespondStreamingAsync([userInput],
            instructions: Options.SystemPrompt, previousResponseId: _lastResponseId,
            inference: effectiveInference, tools: tools, reasoning: effectiveReasoning,
            model: effectiveModel, ct: ct));
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
    /// <param name="reasoning">Per-call reasoning effort override. Overrides <see cref="AgentOptions.Reasoning"/> when set.</param>
    /// <param name="inference">Per-call inference override. Overrides <see cref="AgentOptions.Inference"/> when set.</param>
    /// <param name="model">Per-call model override. Accepts a named alias from <see cref="LMConfig.Models"/> or a literal model ID; <c>null</c> falls back to <see cref="AgentOptions.Model"/> then <see cref="LMConfig.ModelName"/>.</param>
    /// <param name="maxRounds">Maximum number of model turns before the workflow is aborted.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<WorkflowResult> RunWorkflowAsync(
        Workflow workflow, string input, string? mcpServerUrl = null,
        string? serverLabel = null, List<string>? allowedTools = null,
        Dictionary<string, string>? mcpHeaders = null,
        ReasoningEffort? reasoning = null, InferenceConfig? inference = null,
        string? model = null, int maxRounds = 10, CancellationToken ct = default)
    {
        workflow.Reset();
        Emit(AgentEventKind.UserInput, text: input);

        var allInvocations     = new List<ToolInvocation>();
        var allText            = new StringBuilder();
        var systemPrompt       = BuildWorkflowPrompt(workflow, Options.SystemPrompt);
        var mcpTools           = BuildToolDefinitions(mcpServerUrl, serverLabel, allowedTools, mcpHeaders);
        var currentInput       = input;
        var effectiveReasoning = reasoning ?? Options.Reasoning;
        var effectiveModel     = model ?? Options.Model;
        var effectiveInference = inference ?? Options.Inference;
        EmitRequestContext(systemPrompt, mcpTools);

        for (int round = 0; round < maxRounds; round++)
        {
            var response = await ConsumeStreamAsync(
                _lm.RespondStreamingAsync(currentInput,
                    instructions: systemPrompt, previousResponseId: _lastResponseId,
                    inference: effectiveInference, tools: mcpTools,
                    reasoning: effectiveReasoning, model: effectiveModel, ct: ct));

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

    private List<ToolDefinition>? BuildToolDefinitions(
        string? mcpServerUrl,
        string? serverLabel,
        List<string>? allowedTools,
        Dictionary<string, string>? mcpHeaders)
    {
        var tools = Tools.GetToolDefinitions().ToList();
        if (!string.IsNullOrWhiteSpace(mcpServerUrl))
            tools.Add(ToolDefinition.Mcp(serverLabel ?? "agentic", mcpServerUrl, allowedTools, mcpHeaders));
        return tools.Count > 0 ? tools : null;
    }

    // ── Stream consumption ────────────────────────────────────────────────

    private async Task<AgentResponse> ConsumeStreamAsync(IAsyncEnumerable<StreamEvent> events)
    {
        var textBuilder = new StringBuilder();
        var invocations = new List<ToolInvocation>();
        var pendingMcpTools       = new Dictionary<int, string>();
        var emittedMcpToolCalls   = new HashSet<int>();
        var pendingFunctionCalls  = new Dictionary<string, (string Name, string Arguments)>(StringComparer.Ordinal);
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
                    pendingMcpTools[evt.OutputIndex] = evt.Item.Tool ?? evt.Item.Name ?? "";
                    break;

                // Some LM servers never send this event — handled as a fallback below.
                case "response.mcp_call.arguments.done":
                    if (pendingMcpTools.TryGetValue(evt.OutputIndex, out var toolName))
                    {
                        Emit(AgentEventKind.ToolCall, toolName: toolName, args: evt.Arguments);
                        emittedMcpToolCalls.Add(evt.OutputIndex);
                    }
                    break;

                case "response.output_item.done" when evt.Item?.Type is "mcp_call":
                    var tn = evt.Item.Tool ?? evt.Item.Name ?? "";
                    var ta = evt.Item.Arguments ?? "";
                    var to = UnwrapMcpOutput(evt.Item.Output);
                    // Emit ToolCall here if arguments.done never fired for this index.
                    if (!emittedMcpToolCalls.Remove(evt.OutputIndex))
                        Emit(AgentEventKind.ToolCall, toolName: tn, args: ta);
                    Emit(AgentEventKind.ToolResult, toolName: tn, text: to);
                    invocations.Add(new() { Name = tn, Arguments = ta, Result = to });
                    pendingMcpTools.Remove(evt.OutputIndex);
                    break;

                case "response.output_item.done" when evt.Item?.Type is "function_call":
                    var fn = evt.Item.Name ?? ""; var fa = evt.Item.Arguments ?? "";
                    Emit(AgentEventKind.ToolCall, toolName: fn, args: fa);
                    if (!string.IsNullOrEmpty(evt.Item.CallId))
                        pendingFunctionCalls[evt.Item.CallId] = (fn, fa);
                    else
                        invocations.Add(new() { Name = fn, Arguments = fa, Result = "" });
                    break;

                case "response.output_item.done" when evt.Item?.Type is "function_call_output":
                    CompleteFunctionCall(evt.Item, pendingFunctionCalls, invocations);
                    break;

                case "response.completed":
                    _lastResponseId = evt.Response?.Id ?? evt.Response?.ResponseId;
                    usage = evt.Response?.Usage;
                    if (evt.Response?.Output is { Count: > 0 })
                        CompleteFunctionCalls(evt.Response.Output, pendingFunctionCalls, invocations);
                    break;
            }
        }

        foreach (var pending in pendingFunctionCalls.Values)
            invocations.Add(new() { Name = pending.Name, Arguments = pending.Arguments, Result = "" });

        var fullText = textBuilder.ToString();
        EmitCore(new()
        {
            Kind         = AgentEventKind.Answer, Text = fullText,
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
        var pendingFunctionCalls = new Dictionary<string, (string Name, string Arguments)>(StringComparer.Ordinal);
        var unresolvedFunctionCalls = new List<(string Name, string Arguments)>();

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
                    if (!string.IsNullOrEmpty(item.CallId))
                        pendingFunctionCalls[item.CallId] = (fn, fa);
                    else
                        unresolvedFunctionCalls.Add((fn, fa));
                    break;
                case "function_call_output":
                    CompleteFunctionCall(item, pendingFunctionCalls, invocations);
                    break;
            }
        }

        foreach (var pending in unresolvedFunctionCalls)
            invocations.Add(new() { Name = pending.Name, Arguments = pending.Arguments, Result = "" });
        foreach (var pending in pendingFunctionCalls.Values)
            invocations.Add(new() { Name = pending.Name, Arguments = pending.Arguments, Result = "" });

        var ft = string.Join("", text);
        Emit(AgentEventKind.Answer, text: ft);
        return new() { Text = ft, ToolInvocations = invocations, Usage = response.Usage };
    }

    private void Emit(AgentEventKind kind, string? text = null, string? toolName = null, string? args = null)
        => EmitCore(new() { Kind = kind, Text = text, ToolName = toolName, Arguments = args });

    private void EmitCore(AgentEvent e)
    {
        Options.OnEvent?.Invoke(e);
        LogEvent(e);
    }

    private void CompleteFunctionCalls(
        IEnumerable<ResponseOutputItem> items,
        Dictionary<string, (string Name, string Arguments)> pendingFunctionCalls,
        List<ToolInvocation> invocations)
    {
        foreach (var item in items.Where(item => item.Type == "function_call_output"))
            CompleteFunctionCall(item, pendingFunctionCalls, invocations);
    }

    private void CompleteFunctionCall(
        ResponseOutputItem item,
        Dictionary<string, (string Name, string Arguments)> pendingFunctionCalls,
        List<ToolInvocation> invocations)
    {
        var result = item.Output ?? "";
        if (!string.IsNullOrEmpty(item.CallId) && pendingFunctionCalls.Remove(item.CallId, out var pending))
        {
            Emit(AgentEventKind.ToolResult, toolName: pending.Name, text: result);
            invocations.Add(new() { Name = pending.Name, Arguments = pending.Arguments, Result = result });
            return;
        }

        if (!string.IsNullOrEmpty(item.CallId))
            return;

        Emit(AgentEventKind.ToolResult, toolName: item.CallId ?? string.Empty, text: result);
    }

    private void EmitRequestContext(string? instructions, IReadOnlyList<ToolDefinition>? tools)
    {
        if (!string.IsNullOrEmpty(instructions))
            EmitCore(new() { Kind = AgentEventKind.SystemPrompt, Text = instructions });
        foreach (var t in tools?.Where(t => string.Equals(t.Type, "mcp", StringComparison.OrdinalIgnoreCase)) ?? [])
            EmitCore(new()
            {
                Kind         = AgentEventKind.ToolDeclaration,
                ServerLabel  = t.ServerLabel,
                ServerUrl    = t.ServerUrl,
                AllowedTools = t.AllowedTools,
                Text         = t.AllowedTools is { Count: > 0 } ? string.Join(", ", t.AllowedTools) : "all tools",
            });
    }

    private void LogEvent(AgentEvent e)
    {
        if (Options.Logger is null) return;
        switch (e.Kind)
        {
            case AgentEventKind.UserInput:
                Options.Logger.LogInformation("USER: {Text}", e.Text);
                break;
            case AgentEventKind.SystemPrompt:
                Options.Logger.LogDebug("SYSTEM_PROMPT:\n{Text}", e.Text);
                break;
            case AgentEventKind.ToolDeclaration:
                Options.Logger.LogDebug("TOOL_SERVER: {Label} -> {Url}  allowed=[{AllowedTools}]",
                    e.ServerLabel, e.ServerUrl,
                    e.AllowedTools is { Count: > 0 } ? string.Join(", ", e.AllowedTools) : "all");
                break;
            case AgentEventKind.ToolCall:
                Options.Logger.LogInformation("TOOL_CALL: {Tool}\n{Args}", e.ToolName, e.Arguments);
                break;
            case AgentEventKind.ToolResult:
                Options.Logger.LogInformation("TOOL_RESULT: {Tool}\n{Text}", e.ToolName, e.Text);
                break;
            case AgentEventKind.Reasoning:
                Options.Logger.LogDebug("THINKING:\n{Text}", e.Text);
                break;
            case AgentEventKind.Answer:
                Options.Logger.LogInformation("ANSWER ({InputTokens}/{OutputTokens} tokens):\n{Text}",
                    e.InputTokens, e.OutputTokens, e.Text);
                break;
            case AgentEventKind.StepCompleted:
                Options.Logger.LogInformation("STEP_COMPLETED: {Step}", e.Text);
                break;
            case AgentEventKind.WorkflowCompleted:
                Options.Logger.LogInformation("WORKFLOW_COMPLETED: {Workflow}", e.Text);
                break;
            case AgentEventKind.TextDelta:
                Options.Logger.LogTrace("DELTA: {Text}", e.Text);
                break;
        }
    }

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
