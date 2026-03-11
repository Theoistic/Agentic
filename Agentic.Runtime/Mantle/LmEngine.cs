using Agentic.Runtime.Core;
using System.Runtime.CompilerServices;
using System.Text;

namespace Agentic.Runtime.Mantle;

/// <summary>
/// A single token produced during inference.
/// </summary>
public readonly record struct InferenceToken(int Id, string Text, bool IsEndOfGeneration);

/// <summary>
/// Low-level inference engine that owns native model, context, vision and KV-cache state.
/// All native-context access is serialized by an internal lock so callers do not need to
/// coordinate externally. The lock is held only for the duration of individual inference
/// operations (token generation, embedding), not across multi-round tool loops.
/// </summary>
public sealed class LmEngine : IAsyncDisposable, IDisposable
{
    private readonly LmSessionOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<int> _cachedTokens = [];
    private readonly Random _random;
    private readonly int _nBatch;

    private readonly string? _template;
    private readonly string? _bosToken;
    private readonly string _imageToken;

    private Llama.Model _model;
    private Llama.Context _context;
    private Llama.Vision.Context _visionContext;
    private bool _cacheContainsVision;
    private bool _disposed;

    public bool VisionEnabled => !_visionContext.IsNull;
    public string? VisionDisabledReason { get; }
    public string ImageToken => _imageToken;

    /// <summary>
    /// True when the loaded model has no chat template and can only produce embeddings.
    /// </summary>
    public bool IsEmbeddingOnly => _template is null;

    private LmEngine(
        LmSessionOptions options,
        Llama.Model model,
        Llama.Context context,
        Llama.Vision.Context visionContext,
        string? template,
        string? bosToken,
        string? visionDisabledReason,
        int nBatch,
        Random random)
    {
        _options = options;
        _model = model;
        _context = context;
        _visionContext = visionContext;
        _template = template;
        _bosToken = bosToken;
        _imageToken = InferImageToken(template);
        _nBatch = nBatch;
        _random = random;
        VisionDisabledReason = visionDisabledReason;
    }

    /// <summary>
    /// Creates and initializes an engine from the provided options.
    /// </summary>
    public static Task<LmEngine> CreateAsync(LmSessionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!path.Contains(options.BackendDirectory))
            Environment.SetEnvironmentVariable("PATH", $"{path};{options.BackendDirectory}");

        return Task.Run(() =>
        {
            Llama.Init(options.BackendDirectory, options.Logger);

            var metadata = GgufReader.ReadMetadata(options.ModelPath);
            string? template = GgufReader.GetString(metadata, "tokenizer.chat_template");
            string? bosToken = GgufReader.ResolveTokenById(metadata, "tokenizer.ggml.bos_token_id");

            var model = Llama.LoadModel(
                options.ModelPath,
                useMmap: options.UseMmap,
                useMlock: options.UseMlock,
                checkTensors: options.CheckTensors);

            // Resolve vision projector path before context creation so we can
            // inflate n_batch when vision will be active. Image token chunks
            // are decoded in a single llama_decode call and must fit within n_batch.
            // Embedding-only models (no chat template) never use vision.
            string mmprojPath = template is not null
                ? ResolveMmprojPath(options.ModelPath, options.MmprojPath)
                : string.Empty;
            int nBatch = !string.IsNullOrEmpty(mmprojPath)
                ? Math.Max(options.BatchTokens, options.VisionImageMaxTokens)
                : options.BatchTokens;

            var context = Llama.CreateContext(
                model,
                nCtx: options.ContextTokens,
                nBatch: nBatch,
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

            var defaultRequest = options.DefaultRequest ?? new ResponseRequest();
            var random = defaultRequest.Seed is int seed ? new Random(seed) : Random.Shared;

            return new LmEngine(options, model, context, visionCtx, template, bosToken, visionDisabledReason, nBatch, random);
        }, ct);
    }

    /// <summary>
    /// Tokenizes text using the loaded model vocabulary.
    /// </summary>
    public int[] Tokenize(string text) => Llama.Tokenize(_model, text);

    /// <summary>
    /// Converts a token ID to its string representation.
    /// </summary>
    public string TokenToString(int token) => Llama.TokenToString(_model, token);

    /// <summary>
    /// Checks whether a token signals end of generation.
    /// </summary>
    public bool IsEndOfGeneration(int token) => Llama.IsEndOfGeneration(_model, token);

    /// <summary>
    /// Renders a chat prompt from messages and request settings using the model's Jinja template.
    /// This method is stateless and does not acquire the engine lock.
    /// </summary>
    public string RenderPrompt(IReadOnlyList<ChatMessage> messages, ResponseRequest request)
    {
        if (_template is null)
            throw new InvalidOperationException("Cannot render a chat prompt: the loaded model has no chat template (embedding-only model).");

        ValidateMessages(messages);
        var ctx = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["messages"] = messages.Select(BuildTemplateMessage).Cast<object?>().ToList(),
            ["add_generation_prompt"] = true,
            ["enable_thinking"] = request.EnableThinking ?? true,
            ["add_vision_id"] = request.AddVisionId,
            ["tools"] = request.Tools?.Select(BuildPromptTool).Cast<object?>().ToList()
        };
        if (!string.IsNullOrEmpty(_bosToken)) ctx["bos_token"] = _bosToken;

        return MiniJinjaChatTemplate.Render(_template, ctx);
    }

    /// <summary>
    /// Encodes a prompt into the native context, using vision pipeline when images are present.
    /// Acquires the engine lock for the duration of the operation.
    /// </summary>
    public async Task EncodePromptAsync(string prompt, int[] promptTokens, List<string> imageBase64s, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
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
                        ref nPast, nBatch: _nBatch);
                }, ct);

                _cacheContainsVision = true;
            }
            else
            {
                await Task.Run(() => DecodePromptWithCacheContinuation(promptTokens), ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Samples a single token from the current context state.
    /// The engine lock must already be held by the caller (e.g. during streaming generation).
    /// </summary>
    public int SampleToken(ResponseRequest request, Dictionary<int, int> tokenCounts)
        => Llama.Sample(_context, _model, request, tokenCounts, _random);

    /// <summary>
    /// Decodes a single token into the context and adds it to the cache.
    /// The engine lock must already be held by the caller.
    /// </summary>
    public void DecodeToken(int token)
    {
        using var batch = Llama.CreateBatch([token]);
        int rc = Llama.Decode(_context, batch);
        if (rc != 0) throw new InvalidOperationException($"llama_decode failed: {rc}");
        _cachedTokens.Add(token);
    }

    /// <summary>
    /// Streams tokens from the model for the current context state.
    /// Acquires the engine lock for the duration of the enumeration.
    /// </summary>
    public async IAsyncEnumerable<InferenceToken> GenerateTokensAsync(
        ResponseRequest request,
        int maxOutputTokens,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var tokenCounts = new Dictionary<int, int>();

            for (int i = 0; i < maxOutputTokens; i++)
            {
                ct.ThrowIfCancellationRequested();

                int token = SampleToken(request, tokenCounts);

                if (IsEndOfGeneration(token))
                    yield break;

                tokenCounts[token] = tokenCounts.TryGetValue(token, out int count) ? count + 1 : 1;
                string piece = TokenToString(token);

                yield return new InferenceToken(token, piece, false);

                DecodeToken(token);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Generates an embedding vector as <see cref="float"/> values.
    /// Acquires the engine lock for the duration of the operation.
    /// </summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        await _lock.WaitAsync(ct);
        try
        {
            ResetCacheInternal();
            int[] tokens = Llama.Tokenize(_model, text);
            await Task.Run(() => DecodePromptWithCacheContinuation(tokens), ct);
            return await Task.Run(() => Llama.GetEmbeddings(_context, _model), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Generates an embedding vector as <see cref="double"/> values.
    /// Acquires the engine lock for the duration of the operation.
    /// </summary>
    public async Task<double[]> EmbedAsDoubleAsync(string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        await _lock.WaitAsync(ct);
        try
        {
            ResetCacheInternal();
            int[] tokens = Llama.Tokenize(_model, text);
            await Task.Run(() => DecodePromptWithCacheContinuation(tokens), ct);
            return await Task.Run(() => Llama.GetEmbeddingsAsDouble(_context, _model), ct);
        }
        finally
        {
            _lock.Release();
        }
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
        _context = ResetContext(_model, _context, _options.ContextTokens, _nBatch, _options.MicroBatchTokens);
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

    private static string ResolveMmprojPath(string modelPath, string? mmprojPath)
    {
        if (!string.IsNullOrWhiteSpace(mmprojPath)) return mmprojPath;
        return Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(modelPath)) ?? ".", "*.gguf")
            .FirstOrDefault(f => Path.GetFileName(f).Contains("mmproj", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    }

    private static string InferImageToken(string? template)
    {
        const string qwenVisionToken = "<|vision_start|><|image_pad|><|vision_end|>";
        return template?.Contains(qwenVisionToken, StringComparison.Ordinal) == true ? qwenVisionToken : string.Empty;
    }

    private static void ValidateMessages(IReadOnlyList<ChatMessage> messages)
    {
        for (int i = 1; i < messages.Count; i++)
            if (messages[i].Role == "system") throw new InvalidOperationException("System message must be first.");
    }

    internal static object BuildPromptTool(ResponseToolDefinition tool) => new
    {
        name = tool.Name,
        description = tool.Description,
        parameters = tool.Parameters ?? new { type = "object" }
    };

    internal static Dictionary<string, object?> BuildTemplateMessage(ChatMessage message)
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _lock.WaitAsync();
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
        finally { _lock.Release(); _lock.Dispose(); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
