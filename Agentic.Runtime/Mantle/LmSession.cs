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
/// </summary>
public sealed class LmSession : IAsyncDisposable, IDisposable
{
    private sealed record ParsedAssistantOutput(
        string Content,
        string ReasoningContent,
        bool HasReasoning);

    private readonly LmSessionOptions _options;
    private readonly List<ChatMessage> _history = [];
    private readonly List<int> _cachedTokens = [];
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Random _random;
    private readonly Dictionary<string, IReadOnlyList<ChatMessage>> _responseHistories = new(StringComparer.Ordinal);

    private readonly string _template;
    private readonly string? _bosToken;
    private readonly string _imageToken;
    private readonly InferenceOptions _inference;
    private readonly ConversationCompactionOptions _compaction;
    private readonly IConversationCompactor _conversationCompactor;
    private readonly IToolExecutionEngine _toolExecutionEngine;

    private Llama.Model _model;
    private Llama.Context _context;
    private Llama.Vision.Context _visionContext;
    private bool _cacheContainsVision;
    private bool _disposed;

    public bool VisionEnabled => !_visionContext.IsNull;
    public string? VisionDisabledReason { get; }
    public IReadOnlyList<ChatMessage> History => _history;
    public ResponseObject? LastResponse { get; private set; }

    /// <summary>
    /// Raised whenever the session prepares a prompt for model execution.
    /// </summary>
    public event EventHandler<SessionDebugView>? DebugViewCreated;

    private LmSession(
        LmSessionOptions options,
        Llama.Model model,
        Llama.Context context,
        Llama.Vision.Context visionContext,
        string template,
        string? bosToken,
        string? visionDisabledReason)
    {
        _options = options;
        _model = model;
        _context = context;
        _visionContext = visionContext;
        _template = template;
        _bosToken = bosToken;
        _imageToken = InferImageToken(template);
        _inference = options.Inference ?? new InferenceOptions();
        _compaction = options.Compaction;
        _conversationCompactor = options.ConversationCompactor ?? new TokenWindowConversationCompactor();
        _toolExecutionEngine = options.ToolExecutionEngine ?? new DefaultToolExecutionEngine();
        _random = _inference.Seed is int seed ? new Random(seed) : Random.Shared;
        VisionDisabledReason = visionDisabledReason;
    }

    /// <summary>
    /// Creates and initializes a session from the provided options.
    /// </summary>
    public static Task<LmSession> CreateAsync(LmSessionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!path.Contains(options.BackendDirectory))
            Environment.SetEnvironmentVariable("PATH", $"{path};{options.BackendDirectory}");

        return Task.Run(() =>
        {
            Llama.Init(options.BackendDirectory, options.Logger);
            var inference = options.Inference ?? new InferenceOptions();

            // Automatically resolve template and tokens from GGUF metadata
            var metadata = GgufReader.ReadMetadata(options.ModelPath);
            string template = GgufReader.GetString(metadata, "tokenizer.chat_template")
                ?? throw new InvalidOperationException("No chat template found in GGUF metadata.");
            string? bosToken = GgufReader.ResolveTokenById(metadata, "tokenizer.ggml.bos_token_id");

            var model = Llama.LoadModel(
                options.ModelPath,
                useMmap: options.UseMmap,
                useMlock: options.UseMlock,
                checkTensors: options.CheckTensors);
            var context = Llama.CreateContext(
                model,
                nCtx: options.ContextTokens,
                nBatch: options.BatchTokens,
                nUbatch: options.MicroBatchTokens,
                nThreads: options.Threads,
                embeddings: true,
                unifiedKvCache: options.UnifiedKvCache,
                ropeFreqBase: options.RopeFrequencyBase,
                ropeFreqScale: options.RopeFrequencyScale,
                offloadKvCacheToGpu: options.OffloadKvCacheToGpu,
                flashAttention: options.FlashAttention,
                kvCacheTypeK: options.KvCacheTypeK,
                kvCacheTypeV: options.KvCacheTypeV);

            Llama.Vision.Context visionCtx = default;
            string? visionDisabledReason = null;
            string mmprojPath = ResolveMmprojPath(options.ModelPath, options.MmprojPath);

            if (!string.IsNullOrEmpty(mmprojPath))
            {
                try
                {
                    visionCtx = Llama.Vision.Load(
                        model,
                        mmprojPath,
                        useGpu: options.UseGpuForVision,
                        nThreads: options.VisionThreads > 0 ? options.VisionThreads : Environment.ProcessorCount,
                        mediaMarker: Llama.Vision.DefaultMarker,
                        warmup: false,
                        imageMinTokens: options.VisionImageMinTokens,
                        imageMaxTokens: options.VisionImageMaxTokens);
                }
                catch (Exception ex)
                {
                    visionDisabledReason = ex.Message;
                }
            }

            return new LmSession(options, model, context, visionCtx, template, bosToken, visionDisabledReason);
        }, ct);
    }

    /// <summary>
    /// Clears session history and cached context state.
    /// </summary>
    public void Reset()
    {
        ThrowIfDisposed();
        ResetCore(clearResponses: true);
    }

    private void ResetCore(bool clearResponses)
    {
        _history.Clear();
        _cachedTokens.Clear();
        if (clearResponses)
        {
            _responseHistories.Clear();
            LastResponse = null;
        }
        _cacheContainsVision = false;
        _context = ResetContext(_model, _context, _options.ContextTokens, _options.BatchTokens, _options.MicroBatchTokens);
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
        await _syncLock.WaitAsync(ct);

        try
        {
            var responseHistory = BuildResponseHistory(request);
            if (responseHistory.Count == 0)
                throw new InvalidOperationException("Response request did not produce any renderable history.");

            int historyPrefixCount = Math.Max(0, responseHistory.Count - 1);

            ResetCore(clearResponses: false);
            _history.AddRange(responseHistory.Take(historyPrefixCount));

            await foreach (var _ in GenerateCoreAsync(responseHistory[^1], ct).ConfigureAwait(false))
            {
            }

            return FinalizeResponse(request.Model, request.PreviousResponseId, historyPrefixCount);
        }
        finally
        {
            _syncLock.Release();
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
        await _syncLock.WaitAsync(ct);

        try
        {
            var responseHistory = BuildResponseHistory(request);
            if (responseHistory.Count == 0)
                throw new InvalidOperationException("Response request did not produce any renderable history.");

            int historyPrefixCount = Math.Max(0, responseHistory.Count - 1);

            ResetCore(clearResponses: false);
            _history.AddRange(responseHistory.Take(historyPrefixCount));

            await foreach (var chunk in GenerateCoreAsync(responseHistory[^1], ct).ConfigureAwait(false))
                yield return chunk;

            FinalizeResponse(request.Model, request.PreviousResponseId, historyPrefixCount);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Generates an embedding vector as <see cref="float"/> values.
    /// </summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        ThrowIfDisposed();
        await _syncLock.WaitAsync(ct);

        try
        {
            ResetCacheInternal();
            int[] tokens = Llama.Tokenize(_model, text);
            await Task.Run(() => DecodePromptWithCacheContinuation(tokens), ct);
            return await Task.Run(() => Llama.GetEmbeddings(_context, _model), ct);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Generates an embedding vector as <see cref="double"/> values.
    /// </summary>
    public async Task<double[]> EmbedAsDoubleAsync(string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        ThrowIfDisposed();
        await _syncLock.WaitAsync(ct);

        try
        {
            ResetCacheInternal();
            int[] tokens = Llama.Tokenize(_model, text);
            await Task.Run(() => DecodePromptWithCacheContinuation(tokens), ct);
            return await Task.Run(() => Llama.GetEmbeddingsAsDouble(_context, _model), ct);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Streams assistant output for a user message.
    /// </summary>
    public async IAsyncEnumerable<ChatResponseChunk> GenerateAsync(
        ChatMessage message,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _syncLock.WaitAsync(ct);

        try
        {
            await foreach (var chunk in GenerateCoreAsync(message, ct).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async IAsyncEnumerable<ChatResponseChunk> GenerateCoreAsync(
        ChatMessage message,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _history.Add(message);

        for (int round = 0; round < _options.MaxToolRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            var fitted = CompactHistory(_history);
            var promptMessages = ApplyImageRetentionPolicy(fitted, _options.ImageRetentionPolicy);
            string prompt = RenderPrompt(promptMessages);
            int[] promptTokens = Llama.Tokenize(_model, prompt);

            DebugViewCreated?.Invoke(this, new SessionDebugView(
                History: [.. _history],
                PromptMessages: [.. promptMessages],
                RenderedPrompt: prompt,
                Tools: _inference.Tools,
                PromptTokens: promptTokens.Length));

            await EnsurePromptEncodedAsync(prompt, promptTokens, promptMessages, ct);

            var output = new StringBuilder();
            List<ToolCall>? toolCalls = null;
            int completionTokens = 0;
            int emittedContentLength = 0;
            int emittedReasoningLength = 0;

            for (int i = 0; i < _options.ContextTokens; i++)
            {
                ct.ThrowIfCancellationRequested();

                int token = Llama.SampleGreedy(_context, _model);

                if (Llama.IsEndOfGeneration(_model, token))
                    break;

                string piece = Llama.TokenToString(_model, token);
                output.Append(piece);
                completionTokens++;

                var parsedOutput = ParseAssistantOutput(output.ToString(), _inference.EnableThinking);
                string visibleContent = GetStreamingVisibleContent(parsedOutput.Content);
                string? contentDelta = GetDelta(visibleContent, ref emittedContentLength);
                string? reasoningDelta = GetDelta(parsedOutput.ReasoningContent, ref emittedReasoningLength);

                if (contentDelta is not null || reasoningDelta is not null)
                    yield return new ChatResponseChunk(Text: contentDelta, ReasoningText: reasoningDelta);

                if (MiniJinjaChatTemplate.TryParseToolCalls(output.ToString(), out toolCalls))
                    break;

                await Task.Run(() =>
                {
                    using var batch = Llama.CreateBatch([token]);
                    int rc = Llama.Decode(_context, batch);
                    if (rc != 0) throw new InvalidOperationException($"llama_decode failed: {rc}");
                    _cachedTokens.Add(token);
                }, ct);
            }

            string outputText = output.ToString();
            var usage = new InferenceUsage(promptTokens.Length, completionTokens);

            if (toolCalls is { Count: > 0 } || MiniJinjaChatTemplate.TryParseToolCalls(outputText, out toolCalls))
            {
                yield return new ChatResponseChunk(ToolCalls: toolCalls);

                var assistantMessage = CreateAssistantMessage(outputText, _inference.EnableThinking, toolCalls, usage);

                _history.Clear();
                _history.AddRange(fitted);
                _history.Add(assistantMessage);
                yield return new ChatResponseChunk(Usage: usage);

                foreach (var call in toolCalls!)
                {
                    string result = await ExecuteToolAsync(call, ct);
                    _history.Add(new ChatMessage("tool", result, ToolCallId: call.CallId));
                }
                continue;
            }

            _history.Clear();
            _history.AddRange(fitted);
            _history.Add(CreateAssistantMessage(outputText, _inference.EnableThinking, usage: usage));
            yield return new ChatResponseChunk(Usage: usage);
            yield break;
        }
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
                history.Add(new ChatMessage(
                    message.Role,
                    Content: string.Concat(message.Content.Select(c => c.Text)),
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

    private async Task EnsurePromptEncodedAsync(string prompt, int[] promptTokens, IReadOnlyList<ChatMessage> fitted, CancellationToken ct)
    {
        List<string> imageBase64s = ExtractImageBase64s(fitted);
        bool hasImages = VisionEnabled
            && imageBase64s.Count > 0
            && !string.IsNullOrEmpty(_imageToken)
            && prompt.Contains(_imageToken, StringComparison.Ordinal);

        if (hasImages)
        {
            ResetCacheInternal();
            string mtmdPrompt = prompt.Replace(_imageToken, Llama.Vision.DefaultMarker, StringComparison.Ordinal);
            int imageMarkerCount = CountOccurrences(mtmdPrompt, Llama.Vision.DefaultMarker);

            if (imageMarkerCount != imageBase64s.Count)
                throw new InvalidOperationException($"Prompt expects {imageMarkerCount} image(s), but {imageBase64s.Count} image payload(s) were collected from the fitted history.");

            await Task.Run(() =>
            {
                int nPast = 0;
                Llama.Vision.EvalPromptWithBase64Images(
                    _visionContext, _context, mtmdPrompt, imageBase64s,
                    ref nPast, nBatch: _options.BatchTokens);
            }, ct);

            _cacheContainsVision = true;
        }
        else
        {
            await Task.Run(() => DecodePromptWithCacheContinuation(promptTokens), ct);
        }
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

    private async Task<string> ExecuteToolAsync(ToolCall call, CancellationToken ct)
    {
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

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
            return 0;

        int count = 0;
        int index = 0;

        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private void DecodePromptWithCacheContinuation(int[] promptTokens)
    {
        if (_cacheContainsVision) ResetCacheInternal();

        int prefixLength = GetCommonPrefixLength(promptTokens, _cachedTokens);

        if (prefixLength < _cachedTokens.Count)
        {
            if (!Llama.TryRemoveCacheRange(_context, 0, prefixLength))
            {
                ResetCacheInternal();
                prefixLength = 0;
            }
            else
            {
                _cachedTokens.RemoveRange(prefixLength, _cachedTokens.Count - prefixLength);
            }
        }

        if (prefixLength < promptTokens.Length)
        {
            int[] suffix = promptTokens[prefixLength..];
            for (int offset = 0; offset < suffix.Length; offset += _options.BatchTokens)
            {
                int count = Math.Min(_options.BatchTokens, suffix.Length - offset);
                int[] chunk = new int[count];
                Array.Copy(suffix, offset, chunk, 0, count);

                using var batch = Llama.CreateBatch(chunk);
                if (Llama.Decode(_context, batch) != 0) throw new InvalidOperationException("Decode failed.");
            }
            _cachedTokens.AddRange(suffix);
        }
    }

    private void ResetCacheInternal()
    {
        _context = ResetContext(_model, _context, _options.ContextTokens, _options.BatchTokens, _options.MicroBatchTokens);
        _cachedTokens.Clear();
        _cacheContainsVision = false;
    }

    private static Llama.Context ResetContext(Llama.Model model, Llama.Context context, int nCtx, int nBatch, int nUbatch)
    {
        Llama.FreeContext(context);
        return Llama.CreateContext(model, nCtx, nBatch, nUbatch, embeddings: true);
    }

    private static int GetCommonPrefixLength(int[] prompt, List<int> cache)
    {
        int limit = Math.Min(prompt.Length, cache.Count);
        int i = 0;
        while (i < limit && prompt[i] == cache[i]) i++;
        return i;
    }

    private IReadOnlyList<ChatMessage> CompactHistory(IReadOnlyList<ChatMessage> messages)
        => _conversationCompactor.Compact(messages, new ConversationCompactionContext(_compaction, CountTokens, HasRenderableUserQuery));

    private int CountTokens(IReadOnlyList<ChatMessage> messages)
        => Llama.Tokenize(_model, RenderPrompt(messages)).Length;

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

    private string RenderPrompt(IReadOnlyList<ChatMessage> messages)
    {
        ValidateMessages(messages);
        var ctx = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["messages"] = messages.Select(BuildTemplateMessage).Cast<object?>().ToList(),
            ["add_generation_prompt"] = true,
            ["enable_thinking"] = _inference.EnableThinking,
            ["add_vision_id"] = _inference.AddVisionId,
            ["tools"] = _inference.Tools
        };
        if (!string.IsNullOrEmpty(_bosToken)) ctx["bos_token"] = _bosToken;

        return MiniJinjaChatTemplate.Render(_template, ctx);
    }

    private static void ValidateMessages(IReadOnlyList<ChatMessage> messages)
    {
        for (int i = 1; i < messages.Count; i++)
            if (messages[i].Role == "system") throw new InvalidOperationException("System message must be first.");
    }

    private static Dictionary<string, object?> BuildTemplateMessage(ChatMessage message)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal) { ["role"] = message.Role };
        object? content = BuildTemplateContent(message);
        if (content is not null) result["content"] = content;
        if (!string.IsNullOrWhiteSpace(message.ToolCallId)) result["call_id"] = message.ToolCallId;
        if (!string.IsNullOrWhiteSpace(message.ReasoningContent)) result["reasoning_content"] = message.ReasoningContent;
        if (message.ToolCalls is { Count: > 0 })
            result["tool_calls"] = message.ToolCalls.Select(BuildTemplateToolCall).Cast<object?>().ToList();
        return result;
    }

    private static object? BuildTemplateContent(ChatMessage message)
    {
        if (message.Parts is not { Count: > 0 }) return message.Content;
        return message.Parts.Select(p => p switch
        {
            TextPart t => new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "text", ["text"] = t.Text },
            ImagePart => new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "image", ["image"] = true },
            _ => (object?)null
        }).Where(x => x != null).ToList();
    }

    private static Dictionary<string, object?> BuildTemplateToolCall(ToolCall toolCall)
    {
        object? args = ConvertTemplateValue(toolCall.Arguments);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = toolCall.CallId,
            ["call_id"] = toolCall.CallId,
            ["name"] = toolCall.Name,
            ["arguments"] = args,
            ["function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = toolCall.Name,
                ["arguments"] = args,
                ["id"] = toolCall.CallId,
                ["call_id"] = toolCall.CallId
            }
        };
    }

    private static object? ConvertTemplateValue(object? value)
    {
        if (value is null) return null;
        if (value is System.Text.Json.JsonElement element) return element.Clone();
        if (value is IDictionary<string, object?> dict)
        {
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kv in dict) d[kv.Key] = ConvertTemplateValue(kv.Value);
            return d;
        }
        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            return enumerable.Cast<object>().Select(ConvertTemplateValue).ToList();
        }
        return value;
    }

    private static string ResolveMmprojPath(string modelPath, string? mmprojPath)
    {
        if (!string.IsNullOrWhiteSpace(mmprojPath)) return mmprojPath;
        return Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(modelPath)) ?? ".", "*.gguf")
            .FirstOrDefault(f => Path.GetFileName(f).Contains("mmproj", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    }

    private static string InferImageToken(string template)
    {
        const string qwenVisionToken = "<|vision_start|><|image_pad|><|vision_end|>";
        return template.Contains(qwenVisionToken, StringComparison.Ordinal) ? qwenVisionToken : string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _syncLock.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                if (!_visionContext.IsNull) Llama.Vision.Free(_visionContext);
                if (!_context.IsNull) Llama.FreeContext(_context);
                if (!_model.IsNull) Llama.FreeModel(_model);
                Llama.Shutdown();
            });
            _disposed = true;
        }
        finally { _syncLock.Release(); _syncLock.Dispose(); }
    }

    /// <summary>
    /// Disposes the session synchronously.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}