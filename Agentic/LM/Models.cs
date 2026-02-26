using System.Text.Json.Serialization;

namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  /v1/responses models
// ═══════════════════════════════════════════════════════════════════════════

public sealed class ToolDefinition
{
    [JsonPropertyName("type")]          public string Type { get; set; } = "mcp";
    [JsonPropertyName("server_label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerLabel { get; set; }
    [JsonPropertyName("server_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerUrl { get; set; }
    [JsonPropertyName("allowed_tools"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AllowedTools { get; set; }
    [JsonPropertyName("headers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Headers { get; set; }

    public static ToolDefinition Mcp(string label, string url, List<string>? allowed = null, Dictionary<string, string>? headers = null) =>
        new() { ServerLabel = label, ServerUrl = url, AllowedTools = allowed, Headers = headers };
}

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
    [JsonPropertyName("stream"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Stream { get; set; }
}

public sealed class ReasoningConfig
{
    [JsonPropertyName("effort")] public string Effort { get; set; } = "medium";
}

public sealed class ResponseOutputContent
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("text")] public string? Text { get; set; }
}

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

public sealed class ResponseUsage
{
    [JsonPropertyName("input_tokens")]  public int InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
    [JsonPropertyName("total_tokens")]  public int TotalTokens { get; set; }
}

public sealed class ResponseResponse
{
    [JsonPropertyName("id")]          public string Id { get; set; } = "";
    [JsonPropertyName("object")]      public string Object { get; set; } = "";
    [JsonPropertyName("status")]      public string? Status { get; set; }
    [JsonPropertyName("output")]      public List<ResponseOutputItem> Output { get; set; } = [];
    [JsonPropertyName("response_id")] public string? ResponseId { get; set; }
    [JsonPropertyName("usage")]       public ResponseUsage? Usage { get; set; }
}

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

public sealed class ResponseInput
{
    [JsonPropertyName("role")]    public string Role { get; set; } = "user";
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

public sealed class EmbeddingRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("input")] public object Input { get; set; } = "";
    [JsonPropertyName("encoding_format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncodingFormat { get; set; }
}

public sealed class EmbeddingResponse
{
    [JsonPropertyName("object")] public string Object { get; set; } = "";
    [JsonPropertyName("model")]  public string Model { get; set; } = "";
    [JsonPropertyName("data")]   public List<EmbeddingData> Data { get; set; } = [];
    [JsonPropertyName("usage")]  public EmbeddingUsage? Usage { get; set; }
}

public sealed class EmbeddingData
{
    [JsonPropertyName("index")]     public int Index { get; set; }
    [JsonPropertyName("embedding")] public float[] Embedding { get; set; } = [];
}

public sealed class EmbeddingUsage
{
    [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
    [JsonPropertyName("total_tokens")]  public int TotalTokens { get; set; }
}
