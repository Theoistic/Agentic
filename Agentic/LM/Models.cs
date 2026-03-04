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

// ═══════════════════════════════════════════════════════════════════════════
//  Multimodal input content parts (/v1/responses input_image support)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Discriminated content part for a multimodal <c>/v1/responses</c> input message.
/// Concrete types: <see cref="InputTextContent"/> and <see cref="InputImageContent"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(InputTextContent),  "input_text")]
[JsonDerivedType(typeof(InputImageContent), "input_image")]
public abstract class ResponseInputContent { }

/// <summary>Text content part for a multimodal input message.</summary>
public sealed class InputTextContent(string text) : ResponseInputContent
{
    /// <summary>The text value of this content part.</summary>
    [JsonPropertyName("text")] public string Text { get; set; } = text;
}

/// <summary>
/// Image content part for a multimodal input message.
/// Supply a URL or a <c>data:image/…;base64,…</c> data URL via <see cref="ImageUrl"/>.
/// </summary>
public sealed class InputImageContent : ResponseInputContent
{
    /// <summary>A URL or base64 data URL (<c>data:image/jpeg;base64,…</c>).</summary>
    [JsonPropertyName("image_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; set; }

    /// <summary>Creates an image part from a URL or base64 data URL.</summary>
    public static InputImageContent FromUrl(string url) => new() { ImageUrl = url };

    /// <summary>
    /// Reads a local file and encodes it as a base64 data URL.
    /// <paramref name="mimeType"/> is inferred from the file extension when not supplied.
    /// </summary>
    public static InputImageContent FromFile(string filePath, string? mimeType = null)
    {
        var bytes = File.ReadAllBytes(filePath);
        var b64   = Convert.ToBase64String(bytes);
        mimeType ??= InferMimeType(filePath);
        return new() { ImageUrl = $"data:{mimeType};base64,{b64}" };
    }

    private static string InferMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png"            => "image/png",
        ".gif"            => "image/gif",
        ".webp"           => "image/webp",
        _                 => "image/jpeg",
    };
}

/// <summary>A single conversation turn used as input to <c>/v1/responses</c>.</summary>
public sealed class ResponseInput
{
    /// <summary>The speaker role: <c>"user"</c> or <c>"assistant"</c>.</summary>
    [JsonPropertyName("role")]    public string Role { get; set; } = "user";

    /// <summary>
    /// The content of this turn. Either a plain <see cref="string"/> for text-only messages,
    /// or a <see cref="List{T}"/> of <see cref="ResponseInputContent"/> for multimodal messages.
    /// </summary>
    [JsonPropertyName("content")] public object Content { get; set; } = "";

    /// <summary>Builds a text-only user input.</summary>
    public static ResponseInput User(string text) => new() { Role = "user", Content = text };

    /// <summary>
    /// Builds a multimodal user input with text and images.
    /// Each image string may be a URL or a <c>data:image/…;base64,…</c> data URL.
    /// </summary>
    public static ResponseInput User(string text, IEnumerable<string> images)
    {
        var parts = new List<ResponseInputContent> { new InputTextContent(text) };
        foreach (var img in images)
            parts.Add(InputImageContent.FromUrl(img));
        return new() { Role = "user", Content = parts };
    }
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
