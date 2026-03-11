using Agentic.Runtime.Core;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Agentic.Runtime.Mantle;

/// <summary>
/// One streamed chunk of assistant output.
/// </summary>
public sealed record ChatResponseChunk(
    string? Text = null,
    string? ReasoningText = null,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    InferenceUsage? Usage = null
);

/// <summary>
/// Captures the prompt-state view seen by the session for debugging.
/// </summary>
public sealed record SessionDebugView(
    IReadOnlyList<ChatMessage> History,
    IReadOnlyList<ChatMessage> PromptMessages,
    string RenderedPrompt,
    IReadOnlyList<object?>? Tools,
    int PromptTokens
);

/// <summary>
/// Maintains conversation state, prompt compaction, tool execution, and generation.
/// Delegates all native inference to a shared <see cref="LmEngine"/>.
/// </summary>
public sealed class LmSession : IAsyncDisposable, IDisposable
{
    private sealed record ParsedAssistantOutput(
        string Content,
        string ReasoningContent,
        bool HasReasoning);

    private sealed record ResponseExecutionContext(
        ResponseRequest Request,
        IReadOnlyDictionary<string, RemoteToolBinding> RemoteTools);

    private readonly LmSessionOptions _options;
    private readonly LmEngine _engine;
    private readonly List<ChatMessage> _history = [];
    private readonly Dictionary<string, IReadOnlyList<ChatMessage>> _responseHistories = new(StringComparer.Ordinal);

    private readonly ResponseRequest _defaultRequest;
    private readonly ConversationCompactionOptions _compaction;
    private readonly IConversationCompactor _conversationCompactor;
    private readonly IToolExecutionEngine _toolExecutionEngine;

    private bool _disposed;

    // Tracks which session is active on the current async call chain so that
    // nested tool calls (e.g. ScanPdfPage → lm.RespondAsync) can detect
    // re-entrancy and save/restore history instead of corrupting the outer call.
    private static readonly AsyncLocal<LmSession?> s_reentrancyOwner = new();

    public bool VisionEnabled => _engine.VisionEnabled;
    public string? VisionDisabledReason => _engine.VisionDisabledReason;
    public IReadOnlyList<ChatMessage> History => _history;
    public ResponseObject? LastResponse { get; private set; }

    /// <summary>
    /// Gets the underlying inference engine used by this session.
    /// </summary>
    public LmEngine Engine => _engine;

    /// <summary>
    /// Raised whenever the session prepares a prompt for model execution.
    /// </summary>
    public event EventHandler<SessionDebugView>? DebugViewCreated;

    private LmSession(LmSessionOptions options, LmEngine engine)
    {
        _options = options;
        _engine = engine;
        _defaultRequest = options.DefaultRequest ?? new ResponseRequest();
        _compaction = options.Compaction;
        _conversationCompactor = options.ConversationCompactor ?? new TokenWindowConversationCompactor();
        _toolExecutionEngine = options.ToolExecutionEngine ?? new DefaultToolExecutionEngine([new McpRemoteToolExecutor()]);
    }

    /// <summary>
    /// Creates and initializes a session from the provided options.
    /// </summary>
    public static async Task<LmSession> CreateAsync(LmSessionOptions options, CancellationToken ct = default)
    {
        var engine = await LmEngine.CreateAsync(options, ct);
        return new LmSession(options, engine);
    }

    /// <summary>
    /// Creates a session that shares an existing engine instance.
    /// </summary>
    public static LmSession Create(LmSessionOptions options, LmEngine engine)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(engine);
        return new LmSession(options, engine);
    }

    /// <summary>
    /// Clears session history and response state.
    /// </summary>
    public void Reset()
    {
        ThrowIfDisposed();
        ResetCore(clearResponses: true);
    }

    private void ResetCore(bool clearResponses)
    {
        _history.Clear();
        if (clearResponses)
        {
            _responseHistories.Clear();
            LastResponse = null;
        }
    }

    /// <summary>
    /// Sends a single message and returns the final assistant message.
    /// </summary>
    public async Task<ChatMessage> SendAsync(ChatMessage message, CancellationToken ct = default)
    {
        await foreach (var _ in GenerateAsync(message, ct).ConfigureAwait(false))
        {
        }

        for (int i = _history.Count - 1; i >= 0; i--)
            if (_history[i].Role == "assistant")
                return _history[i];

        throw new InvalidOperationException("No assistant message was generated.");
    }

    /// <summary>
    /// Handles a response-style request and returns a response-style object.
    /// </summary>
    public async Task<ResponseObject> CreateResponseAsync(ResponseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Input is not { Count: > 0 })
            throw new InvalidOperationException("Response requests must contain at least one input item.");

        ThrowIfDisposed();

        bool isReentrant = s_reentrancyOwner.Value == this;
        List<ChatMessage>? savedHistory = null;

        if (isReentrant)
            savedHistory = SaveHistorySnapshot();
        else
            s_reentrancyOwner.Value = this;

        try
        {
            var responseHistory = BuildResponseHistory(request);
            if (responseHistory.Count == 0)
                throw new InvalidOperationException("Response request did not produce any renderable history.");

            int historyPrefixCount = Math.Max(0, responseHistory.Count - 1);

            ResetCore(clearResponses: false);
            _history.AddRange(responseHistory.Take(historyPrefixCount));

            var executionContext = await CreateResponseExecutionContextAsync(request, ct).ConfigureAwait(false);

            await foreach (var _ in GenerateCoreAsync(responseHistory[^1], executionContext, ct).ConfigureAwait(false))
            {
            }

            return FinalizeResponse(request.Model, request.PreviousResponseId, historyPrefixCount);
        }
        finally
        {
            if (isReentrant)
                RestoreAfterReentrantCall(savedHistory!);
            else
                s_reentrancyOwner.Value = null;
        }
    }

    /// <summary>
    /// Streams a response-style request while preserving response history state.
    /// </summary>
    public async IAsyncEnumerable<ChatResponseChunk> GenerateResponseAsync(
        ResponseRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Input is not { Count: > 0 })
            throw new InvalidOperationException("Response requests must contain at least one input item.");

        ThrowIfDisposed();

        bool isReentrant = s_reentrancyOwner.Value == this;
        List<ChatMessage>? savedHistory = null;

        if (isReentrant)
            savedHistory = SaveHistorySnapshot();
        else
            s_reentrancyOwner.Value = this;

        try
        {
            var responseHistory = BuildResponseHistory(request);
            if (responseHistory.Count == 0)
                throw new InvalidOperationException("Response request did not produce any renderable history.");

            int historyPrefixCount = Math.Max(0, responseHistory.Count - 1);

            ResetCore(clearResponses: false);
            _history.AddRange(responseHistory.Take(historyPrefixCount));

            var executionContext = await CreateResponseExecutionContextAsync(request, ct).ConfigureAwait(false);

            await foreach (var chunk in GenerateCoreAsync(responseHistory[^1], executionContext, ct).ConfigureAwait(false))
                yield return chunk;

            FinalizeResponse(request.Model, request.PreviousResponseId, historyPrefixCount);
        }
        finally
        {
            if (isReentrant)
                RestoreAfterReentrantCall(savedHistory!);
            else
                s_reentrancyOwner.Value = null;
        }
    }

    /// <summary>
    /// Generates an embedding vector as <see cref="float"/> values.
    /// </summary>
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _engine.EmbedAsync(text, ct);
    }

    /// <summary>
    /// Generates an embedding vector as <see cref="double"/> values.
    /// </summary>
    public Task<double[]> EmbedAsDoubleAsync(string text, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _engine.EmbedAsDoubleAsync(text, ct);
    }

    /// <summary>
    /// Streams assistant output for a user message.
    /// </summary>
    public async IAsyncEnumerable<ChatResponseChunk> GenerateAsync(
        ChatMessage message,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();

        bool isReentrant = s_reentrancyOwner.Value == this;
        List<ChatMessage>? savedHistory = null;

        if (isReentrant)
            savedHistory = SaveHistorySnapshot();
        else
            s_reentrancyOwner.Value = this;

        try
        {
            await foreach (var chunk in GenerateCoreAsync(message, new ResponseExecutionContext(_defaultRequest, new Dictionary<string, RemoteToolBinding>(StringComparer.Ordinal)), ct).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            if (isReentrant)
                RestoreAfterReentrantCall(savedHistory!);
            else
                s_reentrancyOwner.Value = null;
        }
    }

    private async IAsyncEnumerable<ChatResponseChunk> GenerateCoreAsync(
        ChatMessage message,
        ResponseExecutionContext executionContext,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _history.Add(message);
        var request = executionContext.Request;

        for (int round = 0; round < _options.MaxToolRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            var fitted = CompactHistory(_history);
            var promptMessages = ApplyImageRetentionPolicy(fitted, _options.ImageRetentionPolicy);
            string prompt = _engine.RenderPrompt(promptMessages, request);
            int[] promptTokens = _engine.Tokenize(prompt);

            DebugViewCreated?.Invoke(this, new SessionDebugView(
                History: [.. _history],
                PromptMessages: [.. promptMessages],
                RenderedPrompt: prompt,
                Tools: request.Tools?.Select(LmEngine.BuildPromptTool).Cast<object?>().ToList(),
                PromptTokens: promptTokens.Length));

            List<string> imageBase64s = ExtractImageBase64s(promptMessages);
            await _engine.EncodePromptAsync(prompt, promptTokens, imageBase64s, ct);

            var output = new StringBuilder();
            List<ToolCall>? toolCalls = null;
            int completionTokens = 0;
            int emittedContentLength = 0;
            int emittedReasoningLength = 0;
            int maxOutputTokens = request.MaxOutputTokens.GetValueOrDefault() > 0 ? request.MaxOutputTokens.GetValueOrDefault() : _options.ContextTokens;

            await foreach (var token in _engine.GenerateTokensAsync(request, maxOutputTokens, ct).ConfigureAwait(false))
            {
                output.Append(token.Text);
                completionTokens++;

                var parsedOutput = ParseAssistantOutput(output.ToString(), request.EnableThinking ?? true);
                string visibleContent = GetStreamingVisibleContent(parsedOutput.Content);
                string? contentDelta = GetDelta(visibleContent, ref emittedContentLength);
                string? reasoningDelta = GetDelta(parsedOutput.ReasoningContent, ref emittedReasoningLength);

                if (contentDelta is not null || reasoningDelta is not null)
                    yield return new ChatResponseChunk(Text: contentDelta, ReasoningText: reasoningDelta);

                if (MiniJinjaChatTemplate.TryParseToolCalls(output.ToString(), out toolCalls))
                    break;
            }

            string outputText = output.ToString();
            var usage = new InferenceUsage(promptTokens.Length, completionTokens);

            if (toolCalls is { Count: > 0 } || MiniJinjaChatTemplate.TryParseToolCalls(outputText, out toolCalls))
            {
                yield return new ChatResponseChunk(ToolCalls: toolCalls);

                var assistantMessage = CreateAssistantMessage(outputText, request.EnableThinking ?? true, toolCalls, usage);

                _history.Clear();
                _history.AddRange(fitted);
                _history.Add(assistantMessage);
                yield return new ChatResponseChunk(Usage: usage);

                // Tool execution happens with the engine lock released, so nested
                // calls (e.g. tools that invoke the engine for embeddings or sub-generation)
                // can proceed without deadlocking.
                foreach (var call in toolCalls!)
                {
                    string result = await ExecuteToolAsync(call, executionContext.RemoteTools, ct);
                    _history.Add(new ChatMessage("tool", result, ToolCallId: call.CallId));
                }
                continue;
            }

            _history.Clear();
            _history.AddRange(fitted);
            _history.Add(CreateAssistantMessage(outputText, request.EnableThinking ?? true, usage: usage));
            yield return new ChatResponseChunk(Usage: usage);
            yield break;
        }
    }

    private async Task<ResponseExecutionContext> CreateResponseExecutionContextAsync(ResponseRequest request, CancellationToken ct)
    {
        var remoteTools = new Dictionary<string, RemoteToolBinding>(StringComparer.Ordinal);
        List<ResponseToolDefinition> promptTools = request.Tools is { Count: > 0 }
            ? []
            : [.. _defaultRequest.Tools ?? []];

        if (request.Tools is { Count: > 0 })
        {
            foreach (var tool in request.Tools)
            {
                if (string.Equals(tool.Type, "mcp", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var remoteTool in await McpHttpClient.ListToolsAsync(tool, ct).ConfigureAwait(false))
                    {
                        promptTools.Add(new ResponseToolDefinition(
                            Type: "function",
                            Name: remoteTool.Name,
                            Parameters: remoteTool.InputSchema,
                            Description: remoteTool.Description));
                        remoteTools[remoteTool.Name] = remoteTool.Binding;
                    }

                    continue;
                }

                promptTools.Add(new ResponseToolDefinition(
                    Type: "function",
                    Name: tool.Name,
                    Parameters: tool.Parameters ?? new { type = "object" },
                    Description: tool.Description));
            }
        }

        return new ResponseExecutionContext(MergeRequestDefaults(request, promptTools), remoteTools);
    }

    private ResponseRequest MergeRequestDefaults(ResponseRequest request, IReadOnlyList<ResponseToolDefinition> promptTools)
    {
        bool enableThinking = request.EnableThinking
            ?? (request.ReasoningEffort is { Length: > 0 } reasoningEffort
                ? !string.Equals(reasoningEffort, "none", StringComparison.OrdinalIgnoreCase)
                : _defaultRequest.EnableThinking ?? true);

        return request with
        {
            Temperature = request.Temperature ?? _defaultRequest.Temperature,
            TopP = request.TopP ?? _defaultRequest.TopP,
            TopK = request.TopK ?? _defaultRequest.TopK,
            PresencePenalty = request.PresencePenalty ?? _defaultRequest.PresencePenalty,
            FrequencyPenalty = request.FrequencyPenalty ?? _defaultRequest.FrequencyPenalty,
            RepetitionPenalty = request.RepetitionPenalty ?? _defaultRequest.RepetitionPenalty,
            MaxOutputTokens = request.MaxOutputTokens ?? _defaultRequest.MaxOutputTokens,
            EnableThinking = enableThinking,
            Tools = promptTools.Count > 0 ? [.. promptTools] : null,
            AddVisionId = request.AddVisionId || _defaultRequest.AddVisionId,
            Seed = request.Seed ?? _defaultRequest.Seed
        };
    }

    private static ChatMessage CreateAssistantMessage(string rawText, bool enableThinking, List<ToolCall>? toolCalls = null, InferenceUsage? usage = null)
    {
        var parsedOutput = ParseAssistantOutput(rawText, enableThinking);
        string content = toolCalls is { Count: > 0 }
            ? MiniJinjaChatTemplate.StripToolCallMarkup(parsedOutput.Content)
            : parsedOutput.Content;

        return new ChatMessage(
            "assistant",
            string.IsNullOrWhiteSpace(content) ? null : content.Trim(),
            ReasoningContent: parsedOutput.HasReasoning ? parsedOutput.ReasoningContent.Trim() : null,
            ToolCalls: toolCalls,
            RawContent: rawText,
            Usage: usage);
    }

    private List<ChatMessage> BuildResponseHistory(ResponseRequest request)
    {
        List<ChatMessage> history = request.PreviousResponseId is { Length: > 0 } previousResponseId
            ? [.. GetResponseHistory(previousResponseId)]
            : [];

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            var systemMessage = new ChatMessage("system", request.Instructions);
            if (history.Count > 0 && history[0].Role == "system")
                history[0] = systemMessage;
            else
                history.Insert(0, systemMessage);
        }

        foreach (var item in request.Input)
            AddResponseItem(history, item);

        return history;
    }

    private IReadOnlyList<ChatMessage> GetResponseHistory(string responseId)
    {
        if (!_responseHistories.TryGetValue(responseId, out var history))
            throw new InvalidOperationException($"Unknown previous_response_id '{responseId}'.");

        return [.. history];
    }

    private static void AddResponseItem(List<ChatMessage> history, ResponseItem item)
    {
        switch (item)
        {
            case ResponseMessageItem message:
                var (content, parts) = ParseMessageContent(message.Content);
                history.Add(new ChatMessage(
                    message.Role,
                    Content: content,
                    Parts: parts,
                    ReasoningContent: message.Reasoning));
                break;

            case ResponseFunctionCallItem functionCall:
                history.Add(new ChatMessage(
                    "assistant",
                    ToolCalls: [new ToolCall(functionCall.Name, ParseToolArguments(functionCall.Arguments), functionCall.CallId)]));
                break;

            case ResponseFunctionCallOutputItem functionOutput:
                history.Add(new ChatMessage("tool", functionOutput.Output, ToolCallId: functionOutput.CallId));
                break;

            default:
                throw new InvalidOperationException($"Unsupported response item type '{item.Type}'.");
        }
    }

    private static IReadOnlyList<ResponseItem> CreateResponseItems(IReadOnlyList<ChatMessage> messages)
    {
        var items = new List<ResponseItem>();

        foreach (var message in messages)
        {
            if (message.Role == "assistant")
            {
                if (message.ToolCalls is { Count: > 0 })
                {
                    foreach (var toolCall in message.ToolCalls)
                    {
                        items.Add(new ResponseFunctionCallItem(
                            Id: SessionIds.Create("item"),
                            CallId: toolCall.CallId,
                            Name: toolCall.Name,
                            Arguments: JsonSerializer.Serialize(toolCall.Arguments)));
                    }
                }

                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    items.Add(new ResponseMessageItem(
                        Id: SessionIds.Create("item"),
                        Role: "assistant",
                        Content: [new ResponseTextContent("output_text", message.Content)],
                        Reasoning: message.ReasoningContent));
                }

                continue;
            }

            if (message.Role == "tool" && !string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                items.Add(new ResponseFunctionCallOutputItem(
                    Id: SessionIds.Create("item"),
                    CallId: message.ToolCallId,
                    Output: message.Content ?? string.Empty));
            }
        }

        return items;
    }

    private static (string? Content, IReadOnlyList<ContentPart>? Parts) ParseMessageContent(IReadOnlyList<ResponseTextContent> content)
    {
        if (content.Count == 0)
            return (null, null);

        var text = new StringBuilder();
        var parts = new List<ContentPart>(content.Count);

        foreach (var part in content)
        {
            switch (part.Type)
            {
                case "input_image":
                    if (!string.IsNullOrWhiteSpace(part.Text))
                        parts.Add(new ImagePart(part.Text));
                    break;

                default:
                    if (!string.IsNullOrEmpty(part.Text))
                    {
                        text.Append(part.Text);
                        parts.Add(new TextPart(part.Text));
                    }
                    break;
            }
        }

        return (text.Length > 0 ? text.ToString() : null, parts.Count > 0 ? parts : null);
    }

    private static ResponseUsage SumUsage(IReadOnlyList<ChatMessage> messages)
    {
        int promptTokens = 0;
        int completionTokens = 0;

        foreach (var usage in messages.Select(m => m.Usage).Where(u => u is not null))
        {
            promptTokens += usage!.PromptTokens;
            completionTokens += usage.CompletionTokens;
        }

        return new ResponseUsage(promptTokens, completionTokens, promptTokens + completionTokens);
    }

    private ResponseObject FinalizeResponse(string model, string? previousResponseId, int historyPrefixCount)
    {
        string responseId = SessionIds.Create("resp");
        var newMessages = _history.Skip(historyPrefixCount).ToList();
        var usage = SumUsage(newMessages);
        var response = new ResponseObject(
            Id: responseId,
            CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status: ResponseStatus.Completed,
            Model: model,
            Output: CreateResponseItems(newMessages),
            Usage: usage,
            PreviousResponseId: previousResponseId);

        _responseHistories[responseId] = [.. _history];
        LastResponse = response;
        return response;
    }

    private static IReadOnlyDictionary<string, object?> ParseToolArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ParseJsonObject(document.RootElement)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["input"] = arguments };
        }
    }

    private static Dictionary<string, object?> ParseJsonObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
            result[property.Name] = ParseJsonValue(property.Value);

        return result;
    }

    private static object? ParseJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ParseJsonObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ParseJsonValue).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private static IReadOnlyList<ChatMessage> ApplyImageRetentionPolicy(IReadOnlyList<ChatMessage> messages, ImageRetentionPolicy policy)
    {
        if (policy == ImageRetentionPolicy.KeepAllImages)
            return messages;

        int remainingImages = 1;
        var result = new List<ChatMessage>(messages.Count);

        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.Parts is not { Count: > 0 })
            {
                result.Insert(0, message);
                continue;
            }

            var parts = new List<ContentPart>(message.Parts.Count);
            for (int j = message.Parts.Count - 1; j >= 0; j--)
            {
                ContentPart part = message.Parts[j];
                if (part is ImagePart)
                {
                    if (remainingImages > 0)
                    {
                        parts.Insert(0, part);
                        remainingImages--;
                    }

                    continue;
                }

                parts.Insert(0, part);
            }

            result.Insert(0, parts.Count == message.Parts.Count
                ? message
                : message with { Parts = parts.Count > 0 ? parts : null });
        }

        return result;
    }

    private static ParsedAssistantOutput ParseAssistantOutput(string rawText, bool enableThinking)
    {
        const string thinkStart = "<think>";
        const string thinkEnd = "</think>";

        int start = rawText.IndexOf(thinkStart, StringComparison.Ordinal);
        if (start >= 0)
        {
            int reasoningStart = start + thinkStart.Length;
            int end = rawText.IndexOf(thinkEnd, reasoningStart, StringComparison.Ordinal);

            if (end >= 0)
            {
                string content = rawText[..start] + rawText[(end + thinkEnd.Length)..];
                string reasoning = rawText[reasoningStart..end];
                return new ParsedAssistantOutput(content, reasoning, true);
            }

            return new ParsedAssistantOutput(rawText[..start], rawText[reasoningStart..], true);
        }

        if (enableThinking)
        {
            int end = rawText.IndexOf(thinkEnd, StringComparison.Ordinal);
            if (end >= 0)
            {
                string reasoning = rawText[..end];
                string content = rawText[(end + thinkEnd.Length)..];
                return new ParsedAssistantOutput(content, reasoning, true);
            }

            return new ParsedAssistantOutput(string.Empty, rawText, rawText.Length > 0);
        }

        return new ParsedAssistantOutput(rawText, string.Empty, false);
    }

    private static string? GetDelta(string current, ref int emittedLength)
    {
        if (current.Length <= emittedLength)
            return null;

        string delta = current[emittedLength..];
        emittedLength = current.Length;
        return delta.Length == 0 ? null : delta;
    }

    private static string GetStreamingVisibleContent(string content)
    {
        string visible = RemoveStreamingDelimitedBlock(content, "<tool_call>", "</tool_call>");
        visible = RemoveStreamingDelimitedBlock(visible, "<tool_code>", "</tool_code>");
        return TrimTrailingTagPrefix(visible, "<tool_call>", "</tool_call>", "<tool_code>", "</tool_code>");
    }

    private static string RemoveStreamingDelimitedBlock(string text, string startTag, string endTag)
    {
        int searchStart = 0;
        var sb = new StringBuilder(text.Length);

        while (searchStart < text.Length)
        {
            int start = text.IndexOf(startTag, searchStart, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(text, searchStart, text.Length - searchStart);
                break;
            }

            sb.Append(text, searchStart, start - searchStart);

            int end = text.IndexOf(endTag, start + startTag.Length, StringComparison.Ordinal);
            if (end < 0)
                break;

            searchStart = end + endTag.Length;
        }

        return sb.ToString();
    }

    private static string TrimTrailingTagPrefix(string text, params string[] tags)
    {
        int trimLength = 0;

        foreach (string tag in tags)
        {
            int maxPrefixLength = Math.Min(tag.Length - 1, text.Length);
            for (int length = maxPrefixLength; length > trimLength; length--)
            {
                if (tag.AsSpan(0, length).SequenceEqual(text.AsSpan(text.Length - length, length)))
                {
                    trimLength = length;
                    break;
                }
            }
        }

        return trimLength == 0 ? text : text[..^trimLength];
    }

    private async Task<string> ExecuteToolAsync(
        ToolCall call,
        IReadOnlyDictionary<string, RemoteToolBinding> remoteTools,
        CancellationToken ct)
    {
        if (remoteTools.TryGetValue(call.Name, out var remoteTool))
            return await McpHttpClient.CallToolAsync(remoteTool, call.Arguments, ct).ConfigureAwait(false);

        if (!_options.ToolRegistry.TryGet(call.Name, out var tool))
            return $"Error: tool '{call.Name}' is not registered.";

        return await _toolExecutionEngine.ExecuteAsync(new ToolExecutionRequest(call, tool, _options.ToolRegistry), ct);
    }

    private static List<string> ExtractImageBase64s(IReadOnlyList<ChatMessage> messages)
    {
        var images = new List<string>();

        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].Parts is not { Count: > 0 } parts) continue;

            for (int j = 0; j < parts.Count; j++)
            {
                if (parts[j] is ImagePart { Base64: { } base64 })
                    images.Add(base64);
            }
        }

        return images;
    }

    private IReadOnlyList<ChatMessage> CompactHistory(IReadOnlyList<ChatMessage> messages)
        => _conversationCompactor.Compact(messages, new ConversationCompactionContext(_compaction, CountTokens, HasRenderableUserQuery));

    private int CountTokens(IReadOnlyList<ChatMessage> messages)
        => _engine.Tokenize(_engine.RenderPrompt(messages, _defaultRequest)).Length;

    private static bool HasRenderableUserQuery(IReadOnlyList<ChatMessage> messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.Role == "user")
            {
                string content = msg.Content ?? string.Empty;
                return !(content.StartsWith("<tool_response>") && content.EndsWith("</tool_response>"));
            }
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _engine.DisposeAsync();
    }

    /// <summary>
    /// Disposes the session synchronously.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private List<ChatMessage> SaveHistorySnapshot() => [.. _history];

    private void RestoreAfterReentrantCall(List<ChatMessage> savedHistory)
    {
        _history.Clear();
        _history.AddRange(savedHistory);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}