namespace Agentic;

/// <summary>
/// Abstraction over an LLM backend.
/// Implementations may target remote OpenAI-compatible APIs or native local runtimes.
/// </summary>
public interface ILLMBackend
{
    Task<ResponseResponse> RespondAsync(
        string input, string? instructions = null, string? previousResponseId = null,
        InferenceConfig? inference = null, List<ToolDefinition>? tools = null,
        ReasoningEffort? reasoning = null,
        string? model = null, CancellationToken ct = default);

    Task<ResponseResponse> RespondAsync(
        IEnumerable<ResponseInput> input, string? instructions = null, string? previousResponseId = null,
        InferenceConfig? inference = null, List<ToolDefinition>? tools = null,
        ReasoningEffort? reasoning = null,
        string? model = null, CancellationToken ct = default);

    IAsyncEnumerable<StreamEvent> RespondStreamingAsync(
        string input, string? instructions = null, string? previousResponseId = null,
        InferenceConfig? inference = null, List<ToolDefinition>? tools = null,
        ReasoningEffort? reasoning = null,
        string? model = null,
        CancellationToken ct = default);

    IAsyncEnumerable<StreamEvent> RespondStreamingAsync(
        IEnumerable<ResponseInput> input, string? instructions = null,
        string? previousResponseId = null,
        InferenceConfig? inference = null, List<ToolDefinition>? tools = null,
        ReasoningEffort? reasoning = null,
        string? model = null,
        CancellationToken ct = default);

    Task<float[]> EmbedAsync(string input, CancellationToken ct = default);

    Task<List<float[]>> EmbedBatchAsync(IEnumerable<string> inputs, CancellationToken ct = default);

    Task<bool> PingAsync(CancellationToken ct = default);
}
