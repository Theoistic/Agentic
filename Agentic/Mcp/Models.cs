using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentic.Mcp;

// ═══════════════════════════════════════════════════════════════════════════
//  JSON-RPC 2.0
// ═══════════════════════════════════════════════════════════════════════════

public sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Id { get; set; }
    [JsonPropertyName("method")]  public string Method { get; set; } = "";
    [JsonPropertyName("params"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }

    [JsonIgnore] public bool IsNotification => !Id.HasValue;
}

public sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")]      public JsonElement? Id { get; set; }
    [JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; set; }
    [JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }

    public static JsonRpcResponse Success(JsonElement? id, object result) => new()
    { Id = id, Result = Agentic.ToolSchema.SerializeToElement(result) };

    public static JsonRpcResponse Fail(JsonElement? id, int code, string message) => new()
    { Id = id, Error = new() { Code = code, Message = message } };
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]    public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; set; }

    public const int ParseError     = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams  = -32602;
    public const int InternalError  = -32603;
}

// ═══════════════════════════════════════════════════════════════════════════
//  MCP protocol models
// ═══════════════════════════════════════════════════════════════════════════

public sealed class McpImplementation
{
    [JsonPropertyName("name")]    public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

public sealed class McpServerCapabilities
{
    [JsonPropertyName("tools"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpToolsCapability? Tools { get; set; }
    [JsonPropertyName("resources"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpResourcesCapability? Resources { get; set; }
    [JsonPropertyName("prompts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpPromptsCapability? Prompts { get; set; }
    [JsonPropertyName("logging"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Logging { get; set; }
}

public sealed class McpToolsCapability     { [JsonPropertyName("listChanged")] public bool ListChanged { get; set; } }
public sealed class McpResourcesCapability { [JsonPropertyName("subscribe")] public bool Subscribe { get; set; } [JsonPropertyName("listChanged")] public bool ListChanged { get; set; } }
public sealed class McpPromptsCapability   { [JsonPropertyName("listChanged")] public bool ListChanged { get; set; } }

public sealed class InitializeParams
{
    [JsonPropertyName("protocolVersion")] public string ProtocolVersion { get; set; } = "";
    [JsonPropertyName("capabilities")]    public JsonElement? Capabilities { get; set; }
    [JsonPropertyName("clientInfo")]      public McpImplementation? ClientInfo { get; set; }
}

public sealed class InitializeResult
{
    [JsonPropertyName("protocolVersion")] public string ProtocolVersion { get; set; } = "";
    [JsonPropertyName("capabilities")]    public McpServerCapabilities Capabilities { get; set; } = new();
    [JsonPropertyName("serverInfo")]      public McpImplementation ServerInfo { get; set; } = new();
    [JsonPropertyName("instructions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }
}

public sealed class McpToolInfo
{
    [JsonPropertyName("name")]        public string Name { get; set; } = "";
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("inputSchema")] public JsonElement InputSchema { get; set; }
}

public sealed class ToolsListResult  { [JsonPropertyName("tools")]  public List<McpToolInfo> Tools { get; set; } = []; }
public sealed class ToolCallParams
{
    [JsonPropertyName("name")]      public string Name { get; set; } = "";
    [JsonPropertyName("arguments"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Arguments { get; set; }
}

public sealed class McpContent
{
    [JsonPropertyName("type")]     public string Type { get; set; } = "text";
    [JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }
    [JsonPropertyName("mimeType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }
    [JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }
    [JsonPropertyName("resource"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpResourceContent? Resource { get; set; }
}

public sealed class ToolCallResult
{
    [JsonPropertyName("content")] public List<McpContent> Content { get; set; } = [];
    [JsonPropertyName("isError"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsError { get; set; }
}

// ── Resources models ─────────────────────────────────────────────────────

public sealed class McpResourceInfo
{
    [JsonPropertyName("uri")]         public string Uri { get; set; } = "";
    [JsonPropertyName("name")]        public string Name { get; set; } = "";
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("mimeType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }
}

public sealed class McpResourceTemplate
{
    [JsonPropertyName("uriTemplate")] public string UriTemplate { get; set; } = "";
    [JsonPropertyName("name")]        public string Name { get; set; } = "";
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("mimeType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }
}

public sealed class ResourcesListResult          { [JsonPropertyName("resources")]         public List<McpResourceInfo> Resources { get; set; } = []; }
public sealed class ResourceTemplatesListResult  { [JsonPropertyName("resourceTemplates")] public List<McpResourceTemplate> ResourceTemplates { get; set; } = []; }
public sealed class ResourceReadParams           { [JsonPropertyName("uri")]               public string Uri { get; set; } = ""; }

public sealed class McpResourceContent
{
    [JsonPropertyName("uri")]      public string Uri { get; set; } = "";
    [JsonPropertyName("mimeType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }
    [JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }
    [JsonPropertyName("blob"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Blob { get; set; }
}

public sealed class ResourceReadResult { [JsonPropertyName("contents")] public List<McpResourceContent> Contents { get; set; } = []; }

// ── Prompts models ───────────────────────────────────────────────────────

public sealed class McpPromptInfo
{
    [JsonPropertyName("name")]        public string Name { get; set; } = "";
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("arguments"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<McpPromptArgument>? Arguments { get; set; }
}

public sealed class McpPromptArgument
{
    [JsonPropertyName("name")]        public string Name { get; set; } = "";
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("required"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Required { get; set; }
}

public sealed class PromptsListResult { [JsonPropertyName("prompts")] public List<McpPromptInfo> Prompts { get; set; } = []; }

public sealed class PromptGetParams
{
    [JsonPropertyName("name")]      public string Name { get; set; } = "";
    [JsonPropertyName("arguments"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Arguments { get; set; }
}

public sealed class McpPromptMessage
{
    [JsonPropertyName("role")]    public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public McpContent Content { get; set; } = new();
}

public sealed class PromptGetResult
{
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("messages")] public List<McpPromptMessage> Messages { get; set; } = [];
}

// ── Completion models ────────────────────────────────────────────────────

public sealed class CompletionCompleteParams
{
    [JsonPropertyName("ref")]      public CompletionRef Ref { get; set; } = new();
    [JsonPropertyName("argument")] public CompletionArgument Argument { get; set; } = new();
}

public sealed class CompletionRef
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("uri"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uri { get; set; }
    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
}

public sealed class CompletionArgument
{
    [JsonPropertyName("name")]  public string Name { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
}

public sealed class CompletionResult
{
    [JsonPropertyName("completion")] public CompletionValues Completion { get; set; } = new();
}

public sealed class CompletionValues
{
    [JsonPropertyName("values")]  public List<string> Values { get; set; } = [];
    [JsonPropertyName("total"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Total { get; set; }
    [JsonPropertyName("hasMore"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasMore { get; set; }
}

// ── Logging models ───────────────────────────────────────────────────────

public sealed class LoggingSetLevelParams
{
    [JsonPropertyName("level")] public string Level { get; set; } = "info";
}
