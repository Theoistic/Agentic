namespace Agentic;

/// <summary>
/// Routes LLM calls across multiple named <see cref="ILLMBackend"/> instances.
/// <para>
/// Chat requests (<c>RespondAsync</c>, <c>RespondStreamingAsync</c>) are dispatched to the
/// backend whose registered name matches the <c>model</c> parameter, falling back to the
/// designated default when <c>model</c> is <see langword="null"/>.
/// Embedding requests (<c>EmbedAsync</c>, <c>EmbedBatchAsync</c>) are always dispatched to
/// the designated embedding backend, falling back to the default if none is designated.
/// </para>
/// <para>
/// Backends are registered via the fluent <see cref="Add"/> method. The first non-embedding
/// backend added becomes the default unless <c>isDefault</c> is explicitly set.
/// </para>
/// </summary>
public sealed class BackendRouter : ILLMBackend, IAsyncDisposable, IDisposable
{
    private readonly Dictionary<string, ILLMBackend> _backends = new(StringComparer.OrdinalIgnoreCase);
    private string? _defaultName;
    private string? _embeddingName;

    /// <summary>
    /// Registers <paramref name="backend"/> under <paramref name="name"/> and returns
    /// <c>this</c> so calls can be chained.
    /// </summary>
    /// <param name="name">
    /// The routing key. Pass this as the <c>model</c> argument to <c>RespondAsync</c> /
    /// <c>RespondStreamingAsync</c> to target this backend explicitly.
    /// </param>
    /// <param name="backend">The backend instance to register.</param>
    /// <param name="isDefault">
    /// When <see langword="true"/> this backend becomes the default chat backend.
    /// The first non-embedding backend added is automatically designated default.
    /// </param>
    /// <param name="isEmbedding">
    /// When <see langword="true"/> this backend is used for <c>EmbedAsync</c> /
    /// <c>EmbedBatchAsync</c>.
    /// </param>
    public BackendRouter Add(string name, ILLMBackend backend, bool isDefault = false, bool isEmbedding = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(backend);

        _backends[name] = backend;

        if (isDefault || (_defaultName is null && !isEmbedding))
            _defaultName = name;

        if (isEmbedding)
            _embeddingName = name;

        return this;
    }

    private ILLMBackend ResolveChat(string? model)
    {
        if (model is not null)
        {
            if (_backends.TryGetValue(model, out var named))
                return named;
            throw new InvalidOperationException(
                $"No backend registered for model '{model}'. Registered names: {string.Join(", ", _backends.Keys)}");
        }

        if (_defaultName is not null && _backends.TryGetValue(_defaultName, out var def))
            return def;

        throw new InvalidOperationException(
            "No default chat backend is registered. Call Add(..., isDefault: true) or add at least one non-embedding backend.");
    }

    private ILLMBackend ResolveEmbedding()
    {
        var name = _embeddingName ?? _defaultName;
        if (name is not null && _backends.TryGetValue(name, out var backend))
            return backend;

        throw new InvalidOperationException(
            "No embedding backend is registered. Call Add(..., isEmbedding: true).");
    }

    public Task<ResponseResponse> RespondAsync(
        string input, string? instructions = null, string? previousResponseId = null,
        InferenceConfig? inference = null, List<ToolDefinition>? tools = null,
        ReasoningEffort? reasoning = null,
        string? model = null, CancellationToken ct = default)
        => ResolveChat(model).RespondAsync(input, instructions, previousResponseId, inference, tools, reasoning, model, ct);

    public Task<ResponseResponse> RespondAsync(
        IEnumerable<ResponseInput> input, string? instructions = null, string? previousResponseId = null,
        InferenceConfig? inference = null, List<ToolDefinition>? tools = null,
        ReasoningEffort? reasoning = null,
        string? model = null, CancellationToken ct = default)
        => ResolveChat(model).RespondAsync(input, instructions, previousResponseId, inference, tools, reasoning, model, ct);

    public IAsyncEnumerable<StreamEvent> RespondStreamingAsync(
        string input, string? instructions = null, string? previousResponseId = null,
        InferenceConfig? inference = null, List<ToolDefinition>? tools = null,
        ReasoningEffort? reasoning = null,
        string? model = null,
        CancellationToken ct = default)
        => ResolveChat(model).RespondStreamingAsync(input, instructions, previousResponseId, inference, tools, reasoning, model, ct);

    public IAsyncEnumerable<StreamEvent> RespondStreamingAsync(
        IEnumerable<ResponseInput> input, string? instructions = null,
        string? previousResponseId = null,
        InferenceConfig? inference = null, List<ToolDefinition>? tools = null,
        ReasoningEffort? reasoning = null,
        string? model = null,
        CancellationToken ct = default)
        => ResolveChat(model).RespondStreamingAsync(input, instructions, previousResponseId, inference, tools, reasoning, model, ct);

    public Task<float[]> EmbedAsync(string input, CancellationToken ct = default)
        => ResolveEmbedding().EmbedAsync(input, ct);

    public Task<List<float[]>> EmbedBatchAsync(IEnumerable<string> inputs, CancellationToken ct = default)
        => ResolveEmbedding().EmbedBatchAsync(inputs, ct);

    /// <summary>
    /// Pings all registered backends and returns <see langword="true"/> when all respond successfully.
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        foreach (var backend in _backends.Values)
            if (!await backend.PingAsync(ct))
                return false;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var backend in _backends.Values)
        {
            if (backend is IAsyncDisposable ad)
                await ad.DisposeAsync();
            else if (backend is IDisposable d)
                d.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var backend in _backends.Values)
        {
            if (backend is IDisposable d)
                d.Dispose();
        }
    }
}
