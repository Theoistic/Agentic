using System.Text.Json.Serialization;

namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  /v1/responses models
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Describes a tool source available to the model, typically an MCP server endpoint.</summary>
public sealed class ToolDefinition
{
    /// <summary>The tool type discriminator. Defaults to <c>"mcp"</c>.</summary>
    [JsonPropertyName("type")]          public string Type { get; set; } = "mcp";
    /// <summary>A short label identifying this MCP server to the model.</summary>
    [JsonPropertyName("server_label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerLabel { get; set; }
    /// <summary>The base URL of the MCP server (e.g. <c>http://localhost:5100/mcp</c>).</summary>
    [JsonPropertyName("server_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerUrl { get; set; }
    /// <summary>Optional allow-list of tool names exposed to the model. <c>null</c> means all tools.</summary>
    [JsonPropertyName("allowed_tools"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowedTools { get; set; }
    /// <summary>Optional HTTP headers forwarded to the MCP server on every request.</summary>
    [JsonPropertyName("headers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>Creates an MCP-type tool definition.</summary>
    /// <param name="label">Short label identifying the server.</param>
    /// <param name="url">Base URL of the MCP server.</param>
    /// <param name="allowed">Optional tool allow-list.</param>
    /// <param name="headers">Optional HTTP headers.</param>
    public static ToolDefinition Mcp(string label, string url, List<string>? allowed = null, Dictionary<string, string>? headers = null) =>
        new() { ServerLabel = label, ServerUrl = url, AllowedTools = allowed, Headers = headers };
}

/// <summary>Request body sent to the <c>/v1/responses</c> endpoint.</summary>
public sealed class ResponseRequest
{
    [JsonPropertyName("model")]       public string Model { get; set; } = "";
    [JsonPropertyName("input")]       public object Input { get; set; } = "";
    [JsonPropertyName("instructions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }
    [JsonPropertyName("temperature"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Temperature { get; set; }
    [JsonPropertyName("previous_response_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousResponseId { get; set; }
    [JsonPropertyName("tools"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolDefinition>? Tools { get; set; }
    [JsonPropertyName("reasoning"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReasoningConfig? Reasoning { get; set; }
    /// <summary>Qwen thinking toggle: sent as <c>enable_thinking</c> in the request body.</summary>
    [JsonPropertyName("enable_thinking"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableThinking { get; set; }
    [JsonPropertyName("stream"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Stream { get; set; }
}

/// <summary>Controls the reasoning effort the model applies before generating a response.</summary>
public sealed class ReasoningConfig
{
    /// <summary>Effort level: <c>"low"</c>, <c>"medium"</c> (default), or <c>"high"</c>.</summary>
    [JsonPropertyName("effort")] public string Effort { get; set; } = "medium";
}

/// <summary>Controls Qwen-style chain-of-thought thinking for a single request or globally via <see cref="LMConfig"/>.</summary>
public sealed class ThinkingConfig
{
    /// <summary>Whether to enable (<c>true</c>) or disable (<c>false</c>) thinking.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>A typed content block within a <see cref="ResponseOutputItem"/> (e.g. <c>output_text</c>).</summary>
public sealed class ResponseOutputContent
{
    /// <summary>Content type discriminator (e.g. <c>"output_text"</c>).</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    /// <summary>Text value when <see cref="Type"/> is <c>"output_text"</c>.</summary>
    [JsonPropertyName("text")] public string? Text { get; set; }
}

/// <summary>A single output item in a <see cref="ResponseResponse"/> (message, tool call, tool result, etc.).</summary>
public sealed class ResponseOutputItem
{
    [JsonPropertyName("type")]      public string Type { get; set; } = "";
    [JsonPropertyName("id")]        public string? Id { get; set; }
    [JsonPropertyName("role")]      public string? Role { get; set; }
    [JsonPropertyName("content")]   public List<ResponseOutputContent>? Content { get; set; }
    [JsonPropertyName("name")]      public string? Name { get; set; }
    [JsonPropertyName("call_id")]   public string? CallId { get; set; }
    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    [JsonPropertyName("tool")]      public string? Tool { get; set; }
    [JsonPropertyName("output")]    public string? Output { get; set; }
}

/// <summary>Token usage counters returned with a completed response.</summary>
public sealed class ResponseUsage
{
    /// <summary>Number of tokens in the input (prompt + context).</summary>
    [JsonPropertyName("input_tokens")]  public int InputTokens { get; set; }
    /// <summary>Number of tokens generated by the model.</summary>
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
    /// <summary>Total tokens consumed by the request (<see cref="InputTokens"/> + <see cref="OutputTokens"/>).</summary>
    [JsonPropertyName("total_tokens")]  public int TotalTokens { get; set; }
}

/// <summary>Top-level response object returned by the <c>/v1/responses</c> endpoint.</summary>
public sealed class ResponseResponse
{
    [JsonPropertyName("id")]          public string Id { get; set; } = "";
    [JsonPropertyName("object")]      public string Object { get; set; } = "";
    [JsonPropertyName("status")]      public string? Status { get; set; }
    [JsonPropertyName("output")]      public List<ResponseOutputItem> Output { get; set; } = [];
    [JsonPropertyName("response_id")] public string? ResponseId { get; set; }
    [JsonPropertyName("usage")]       public ResponseUsage? Usage { get; set; }
}

/// <summary>A single Server-Sent Event (SSE) emitted during a streaming <c>/v1/responses</c> request.</summary>
public sealed class StreamEvent
{
    [JsonPropertyName("type")]          public string Type { get; set; } = "";
    [JsonPropertyName("output_index")]  public int OutputIndex { get; set; }
    [JsonPropertyName("content_index")] public int ContentIndex { get; set; }
    [JsonPropertyName("delta")]         public string? Delta { get; set; }
    [JsonPropertyName("text")]          public string? Text { get; set; }
    [JsonPropertyName("arguments")]     public string? Arguments { get; set; }
    [JsonPropertyName("item")]          public ResponseOutputItem? Item { get; set; }
    [JsonPropertyName("part")]          public ResponseOutputContent? Part { get; set; }
    [JsonPropertyName("response")]      public ResponseResponse? Response { get; set; }
}

/// <summary>A single conversation turn used as input to <c>/v1/responses</c>.</summary>
public sealed class ResponseInput
{
    /// <summary>The speaker role: <c>"user"</c> or <c>"assistant"</c>.</summary>
    [JsonPropertyName("role")]    public string Role { get; set; } = "user";
    /// <summary>The text content of this conversation turn.</summary>
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

// ═══════════════════════════════════════════════════════════════════════════
//  Vision (chat completions multimodal — universally supported by VLM servers)
// ═══════════════════════════════════════════════════════════════════════════

internal sealed class VisionRequest
{
    [JsonPropertyName("model")]      public string Model { get; set; } = "";
    [JsonPropertyName("messages")]   public List<VisionMessage> Messages { get; set; } = [];
    [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; } = 1024;
    [JsonPropertyName("temperature"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Temperature { get; set; }
}

internal sealed class VisionMessage
{
    [JsonPropertyName("role")]    public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public List<VisionContentPart> Content { get; set; } = [];
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(VisionTextPart), "text")]
[JsonDerivedType(typeof(VisionImagePart), "image_url")]
internal abstract class VisionContentPart { }

internal sealed class VisionTextPart(string text) : VisionContentPart
{
    [JsonPropertyName("text")] public string Text { get; set; } = text;
}

internal sealed class VisionImagePart(string url) : VisionContentPart
{
    [JsonPropertyName("image_url")] public VisionImageUrl ImageUrl { get; set; } = new(url);
}

internal sealed class VisionImageUrl(string url)
{
    [JsonPropertyName("url")] public string Url { get; set; } = url;
}

internal sealed class VisionResponse
{
    [JsonPropertyName("choices")] public List<VisionChoice> Choices { get; set; } = [];
}

internal sealed class VisionChoice
{
    [JsonPropertyName("message")] public VisionChoiceMessage Message { get; set; } = new();
}

internal sealed class VisionChoiceMessage
{
    [JsonPropertyName("content")] public string? Content { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════
//  /v1/embeddings models
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Request body for the <c>/v1/embeddings</c> endpoint.</summary>
public sealed class EmbeddingRequest
{
    /// <summary>Embedding model identifier.</summary>
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    /// <summary>A single string or list of strings to embed.</summary>
    [JsonPropertyName("input")] public object Input { get; set; } = "";
    /// <summary>Optional encoding format (e.g. <c>"float"</c> or <c>"base64"</c>).</summary>
    [JsonPropertyName("encoding_format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncodingFormat { get; set; }
}

/// <summary>Response from the <c>/v1/embeddings</c> endpoint.</summary>
public sealed class EmbeddingResponse
{
    [JsonPropertyName("object")] public string Object { get; set; } = "";
    [JsonPropertyName("model")]  public string Model { get; set; } = "";
    [JsonPropertyName("data")]   public List<EmbeddingData> Data { get; set; } = [];
    [JsonPropertyName("usage")]  public EmbeddingUsage? Usage { get; set; }
}

/// <summary>A single embedding result for one input text.</summary>
public sealed class EmbeddingData
{
    /// <summary>Zero-based position of this item in the original input list.</summary>
    [JsonPropertyName("index")]     public int Index { get; set; }
    /// <summary>The raw embedding vector.</summary>
    [JsonPropertyName("embedding")] public float[] Embedding { get; set; } = [];
}

/// <summary>Token usage counters for an embeddings request.</summary>
public sealed class EmbeddingUsage
{
    /// <summary>Number of tokens in the input text(s).</summary>
    [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
    /// <summary>Total tokens consumed by the request.</summary>
    [JsonPropertyName("total_tokens")]  public int TotalTokens { get; set; }
}
