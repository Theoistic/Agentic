using System.IO;

namespace Agentic.Runtime.Mantle;

/// <summary>
/// Base type for structured multimodal content attached to a chat message.
/// </summary>
public abstract record ContentPart;

/// <summary>
/// Plain text content within a message.
/// </summary>
public sealed record TextPart(string Text) : ContentPart;

/// <summary>
/// Image content stored as base64 so it can be replayed across turns.
/// </summary>
public sealed record ImagePart(string? Base64 = null) : ContentPart
{
    /// <summary>
    /// Creates an image part from a file on disk.
    /// </summary>
    public static ImagePart FromFile(string path) => new(Convert.ToBase64String(File.ReadAllBytes(path)));

    /// <summary>
    /// Creates an image part from raw bytes.
    /// </summary>
    public static ImagePart FromBytes(byte[] data) => new(Convert.ToBase64String(data));
}

/// <summary>
/// Placeholder for future video support in multimodal messages.
/// </summary>
public sealed record VideoPart() : ContentPart;

/// <summary>
/// Token usage reported for a single inference round.
/// </summary>
public sealed record InferenceUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens
)
{
    /// <summary>
    /// Creates usage values and derives the total token count.
    /// </summary>
    public InferenceUsage(int PromptTokens, int CompletionTokens)
        : this(PromptTokens, CompletionTokens, PromptTokens + CompletionTokens)
    {
    }
}

/// <summary>
/// Token usage shape used by response-style API objects.
/// </summary>
public sealed record ResponseUsage(
    int InputTokens,
    int OutputTokens,
    int TotalTokens
)
{
    /// <summary>
    /// Converts runtime inference usage into response API usage fields.
    /// </summary>
    public static ResponseUsage FromInference(InferenceUsage usage) => new(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens);
}

/// <summary>
/// Controls how image context is retained across turns.
/// </summary>
public enum ImageRetentionPolicy
{
    KeepAllImages,
    KeepLatestImage
}

/// <summary>
/// Lifecycle states for a response-style API operation.
/// </summary>
public enum ResponseStatus
{
    Queued,
    InProgress,
    Completed,
    Failed
}

/// <summary>
/// High-level aggressiveness for the active compaction strategy.
/// </summary>
public enum ConversationCompactionLevel
{
    Light,
    Balanced,
    Aggressive
}

/// <summary>
/// Strategy used to reduce conversation history before prompt rendering.
/// </summary>
public enum ContextCompactionStrategy
{
    FifoSlidingWindow,
    PinnedSystemFifo,
    MiddleOutElision,
    RollingSummarization,
    HeuristicPruning,
    VectorAugmentedRecall
}

/// <summary>
/// Generates stable prefixed identifiers for runtime session artifacts.
/// </summary>
public static class SessionIds
{
    /// <summary>
    /// Creates a new identifier using the provided prefix.
    /// </summary>
    public static string Create(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
}

/// <summary>
/// Function tool metadata exposed to a response-style API surface.
/// </summary>
public sealed record ResponseToolDefinition(
    string Type,
    string Name,
    object? Parameters = null,
    string? Description = null
);

/// <summary>
/// Request payload for a response-style API operation.
/// </summary>
public sealed record ResponseRequest(
    string Model,
    IReadOnlyList<ResponseItem> Input,
    string? Instructions = null,
    IReadOnlyList<ResponseToolDefinition>? Tools = null,
    string? PreviousResponseId = null,
    float? Temperature = null,
    int? MaxOutputTokens = null,
    bool Stream = false
);

/// <summary>
/// Base type for response-style input and output items.
/// </summary>
public abstract record ResponseItem(string Id, string Type);

/// <summary>
/// Text payload for response-style message items.
/// </summary>
public sealed record ResponseTextContent(
    string Type,
    string Text
);

/// <summary>
/// Message item used by the response-style API.
/// </summary>
public sealed record ResponseMessageItem(
    string Id,
    string Role,
    IReadOnlyList<ResponseTextContent> Content,
    string? Reasoning = null
) : ResponseItem(Id, "message");

/// <summary>
/// Tool call item emitted by the model in a response-style API.
/// </summary>
public sealed record ResponseFunctionCallItem(
    string Id,
    string CallId,
    string Name,
    string Arguments
) : ResponseItem(Id, "function_call");

/// <summary>
/// Tool output item supplied in response to a prior function call.
/// </summary>
public sealed record ResponseFunctionCallOutputItem(
    string Id,
    string CallId,
    string Output
) : ResponseItem(Id, "function_call_output");

/// <summary>
/// Response-style API result containing generated items and usage.
/// </summary>
public sealed record ResponseObject(
    string Id,
    long CreatedAt,
    ResponseStatus Status,
    string Model,
    IReadOnlyList<ResponseItem> Output,
    ResponseUsage Usage,
    string Object = "response",
    string? PreviousResponseId = null
);

/// <summary>
/// Structured tool invocation captured from model output.
/// </summary>
public sealed record ToolCall(
    string Name,
    IReadOnlyDictionary<string, object?> Arguments,
    string CallId
)
{
    /// <summary>
    /// Creates a tool call with an auto-generated call identifier.
    /// </summary>
    public ToolCall(string Name, IReadOnlyDictionary<string, object?> Arguments)
        : this(Name, Arguments, SessionIds.Create("call"))
    {
    }
}

/// <summary>
/// Canonical conversation message stored in session history.
/// </summary>
public sealed record ChatMessage(
    string Role,
    string? Content = null,
    IReadOnlyList<ContentPart>? Parts = null,
    string? ReasoningContent = null,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? RawContent = null,
    InferenceUsage? Usage = null,
    string? ToolCallId = null
);

/// <summary>
/// KV cache quantization types exposed by llama.cpp for cache K/V tensors.
/// </summary>
public enum KvCacheQuantization
{
    F16,
    Q4_0,
    Q4_1,
    Q5_0,
    Q5_1,
    Q8_0
}

/// <summary>
/// Configuration used to create an <see cref="LmSession"/>.
/// </summary>
public sealed record LmSessionOptions
{
    /// <summary>
    /// Directory containing the llama.cpp backend binaries to load.
    /// </summary>
    public required string BackendDirectory { get; init; }

    /// <summary>
    /// Full path to the GGUF model file.
    /// </summary>
    public required string ModelPath { get; init; }

    /// <summary>
    /// Tool registry available to the session for model tool calls.
    /// </summary>
    public required ToolRegistry ToolRegistry { get; init; }

    /// <summary>
    /// Conversation compaction policy applied before prompt rendering.
    /// </summary>
    public required ConversationCompactionOptions Compaction { get; init; }

    /// <summary>
    /// Per-inference sampling and generation behavior.
    /// </summary>
    public InferenceOptions? Inference { get; init; }

    /// <summary>
    /// Optional pluggable conversation compactor implementation.
    /// </summary>
    public IConversationCompactor? ConversationCompactor { get; init; }

    /// <summary>
    /// Optional tool execution engine used to fulfill both local and remote tools.
    /// When omitted, the session uses <see cref="DefaultToolExecutionEngine"/>.
    /// </summary>
    public IToolExecutionEngine? ToolExecutionEngine { get; init; }

    /// <summary>
    /// Optional logger used by the session and native backend.
    /// </summary>
    public ILogger? Logger { get; init; }

    /// <summary>
    /// Total token capacity of the native llama context created for the session.
    /// </summary>
    public int ContextTokens { get; init; } = 8192;

    /// <summary>
    /// Reserved reset-oriented context setting kept for future policy control.
    /// The session currently rebuilds native contexts using <see cref="ContextTokens"/> to keep capacity aligned.
    /// </summary>
    public int ResetContextTokens { get; init; } = 2048;

    /// <summary>
    /// Evaluation batch size used when decoding prompt chunks into the model context.
    /// </summary>
    public int BatchTokens { get; init; } = 1024;

    /// <summary>
    /// Micro-batch size used internally by llama.cpp when evaluating larger batches.
    /// </summary>
    public int MicroBatchTokens { get; init; } = 1024;

    /// <summary>
    /// CPU thread pool size used by llama.cpp token evaluation.
    /// </summary>
    public int? Threads { get; init; }

    /// <summary>
    /// Maximum number of tool-calling rounds allowed within a single turn.
    /// </summary>
    public int MaxToolRounds { get; init; } = 10;

    /// <summary>
    /// Optional explicit path to the multimodal projector file.
    /// </summary>
    public string? MmprojPath { get; init; }

    /// <summary>
    /// Policy controlling how image parts are retained across turns.
    /// </summary>
    public ImageRetentionPolicy ImageRetentionPolicy { get; init; } = ImageRetentionPolicy.KeepAllImages;

    /// <summary>
    /// Enables GPU acceleration for the vision pipeline when available.
    /// </summary>
    public bool UseGpuForVision { get; init; } = true;

    /// <summary>
    /// Thread count used by the vision subsystem.
    /// </summary>
    public int VisionThreads { get; init; } = 0;

    /// <summary>
    /// Lower bound for multimodal image token budgeting.
    /// </summary>
    public int VisionImageMinTokens { get; init; } = 1024;

    /// <summary>
    /// Upper bound for multimodal image token budgeting.
    /// </summary>
    public int VisionImageMaxTokens { get; init; } = 1024;

    /// <summary>
    /// Enables unified KV cache behavior at context creation time.
    /// </summary>
    public bool UnifiedKvCache { get; init; } = false;

    /// <summary>
    /// Optional RoPE frequency base override used when creating the native context.
    /// </summary>
    public float? RopeFrequencyBase { get; init; }

    /// <summary>
    /// Optional RoPE frequency scale override used when creating the native context.
    /// </summary>
    public float? RopeFrequencyScale { get; init; }

    /// <summary>
    /// Requests KV cache related work to be offloaded to GPU when supported.
    /// </summary>
    public bool OffloadKvCacheToGpu { get; init; } = true;

    /// <summary>
    /// Enables mmap-based GGUF loading.
    /// </summary>
    public bool UseMmap { get; init; } = true;

    /// <summary>
    /// Requests locked model memory pages when the platform allows it.
    /// </summary>
    public bool UseMlock { get; init; } = false;

    /// <summary>
    /// Enables additional tensor validation while loading the model.
    /// </summary>
    public bool CheckTensors { get; init; } = false;

    /// <summary>
    /// Enables flash attention when supported by the backend and hardware.
    /// </summary>
    public bool FlashAttention { get; init; } = false;

    /// <summary>
    /// Optional KV cache quantization type for key tensors.
    /// </summary>
    public KvCacheQuantization? KvCacheTypeK { get; init; }

    /// <summary>
    /// Optional KV cache quantization type for value tensors.
    /// </summary>
    public KvCacheQuantization? KvCacheTypeV { get; init; }
}

/// <summary>
/// Sampling and generation settings applied to an individual inference run.
/// </summary>
public sealed record InferenceOptions
{
    /// <summary>
    /// Maximum number of tokens the model is allowed to emit for a single assistant turn.
    /// Larger values allow longer answers, but also increase latency and the chance of wandering output.
    /// </summary>
    public int MaxOutputTokens { get; init; } = 512;

    /// <summary>
    /// Primary randomness control for token sampling.
    /// Lower values make output more deterministic; higher values increase creativity and risk.
    /// A value less than or equal to zero falls back to greedy selection in the runtime.
    /// </summary>
    public float Temperature { get; init; } = 1.0f;

    /// <summary>
    /// Nucleus sampling threshold.
    /// The sampler keeps only the smallest probability mass whose cumulative total reaches this value.
    /// Lower values narrow the candidate set; higher values allow more diverse continuations.
    /// </summary>
    public float TopP { get; init; } = 0.95f;

    /// <summary>
    /// Hard cap on how many highest-ranked next-token candidates remain after sorting logits.
    /// Useful for constraining sampling even when <see cref="TopP"/> is permissive.
    /// </summary>
    public int TopK { get; init; } = 20;

    /// <summary>
    /// Penalty applied once a token has appeared at least once in the generated text.
    /// This encourages the model to introduce new concepts instead of repeating existing ones.
    /// </summary>
    public float PresencePenalty { get; init; } = 1.5f;

    /// <summary>
    /// Penalty applied proportionally to how often a token has already appeared.
    /// This is stronger than presence-only control and helps suppress loops and repetitive phrasing.
    /// </summary>
    public float FrequencyPenalty { get; init; } = 1.0f;

    /// <summary>
    /// Optional deterministic seed for sampling.
    /// When set, repeated runs with the same prompt and settings become more reproducible.
    /// </summary>
    public int? Seed { get; init; }

    /// <summary>
    /// Enables parsing and streaming of model reasoning blocks such as <c>&lt;think&gt;</c> content.
    /// Disable this when you want only visible answer text and no reasoning surface area.
    /// </summary>
    public bool EnableThinking { get; init; } = true;

    /// <summary>
    /// Requests that the chat template include explicit vision identifiers when supported.
    /// This is template/model specific and is mainly useful for multimodal prompt formats.
    /// </summary>
    public bool AddVisionId { get; init; } = false;

    /// <summary>
    /// Tool schema objects injected into the template context.
    /// These describe callable functions the model may invoke during generation.
    /// </summary>
    public IReadOnlyList<object?>? Tools { get; init; }
}

/// <summary>
/// Options used by conversation compaction before rendering a prompt.
/// </summary>
public sealed record ConversationCompactionOptions(
    int MaxInputTokens,
    int ReservedForGeneration = 512,
    ContextCompactionStrategy Strategy = ContextCompactionStrategy.PinnedSystemFifo,
    ConversationCompactionLevel Level = ConversationCompactionLevel.Balanced,
    bool AlwaysKeepSystem = true,
    int HotTrailMessages = 4
)
{
    /// <summary>
    /// Remaining prompt budget after reserving generation tokens.
    /// </summary>
    public int TokenBudget => MaxInputTokens - ReservedForGeneration;
}
