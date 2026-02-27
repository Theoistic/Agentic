using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentic.Mcp;

// ═══════════════════════════════════════════════════════════════════════════
//  JSON-RPC 2.0
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>A JSON-RPC 2.0 request or notification object.</summary>
public sealed class JsonRpcRequest
{
    /// <summary>JSON-RPC protocol version. Always <c>"2.0"</c>.</summary>
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    /// <summary>Request identifier. Absent for notifications.</summary>
    [JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Id { get; set; }
    /// <summary>The method name to invoke.</summary>
    [JsonPropertyName("method")]  public string Method { get; set; } = "";
    /// <summary>Method parameters, if any.</summary>
    [JsonPropertyName("params"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }

    /// <summary><c>true</c> when this message has no <c>id</c> and should not receive a response.</summary>
    [JsonIgnore] public bool IsNotification => !Id.HasValue;
}

/// <summary>A JSON-RPC 2.0 response object returned for every non-notification request.</summary>
public sealed class JsonRpcResponse
{
    /// <summary>JSON-RPC protocol version. Always <c>"2.0"</c>.</summary>
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    /// <summary>Echoed request identifier.</summary>
    [JsonPropertyName("id")]      public JsonElement? Id { get; set; }
    /// <summary>Successful result payload. Mutually exclusive with <see cref="Error"/>.</summary>
    [JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; set; }
    /// <summary>Error payload. Mutually exclusive with <see cref="Result"/>.</summary>
    [JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }

    /// <summary>Creates a successful response with the given result object.</summary>
    public static JsonRpcResponse Success(JsonElement? id, object result) => new()
    { Id = id, Result = Agentic.ToolSchema.SerializeToElement(result) };

    /// <summary>Creates an error response with the given error code and message.</summary>
    public static JsonRpcResponse Fail(JsonElement? id, int code, string message) => new()
    { Id = id, Error = new() { Code = code, Message = message } };
}

/// <summary>The <c>error</c> object within a JSON-RPC 2.0 error response.</summary>
public sealed class JsonRpcError
{
    /// <summary>Numeric error code (see <see cref="ParseError"/> and related constants).</summary>
    [JsonPropertyName("code")]    public int Code { get; set; }
    /// <summary>Short human-readable error message.</summary>
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    /// <summary>Optional additional error data.</summary>
    [JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; set; }

    /// <summary>Error code for JSON parse failures.</summary>
    public const int ParseError     = -32700;
    /// <summary>Error code for invalid JSON-RPC request objects.</summary>
    public const int InvalidRequest = -32600;
    /// <summary>Error code when the requested method does not exist.</summary>
    public const int MethodNotFound = -32601;
    /// <summary>Error code for invalid or missing method parameters.</summary>
    public const int InvalidParams  = -32602;
    /// <summary>Error code for unexpected server-side errors.</summary>
    public const int InternalError  = -32603;
}

// ═══════════════════════════════════════════════════════════════════════════
//  MCP protocol models
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Identifies an MCP client or server by name and version.</summary>
public sealed class McpImplementation
{
    /// <summary>Human-readable name of the implementation.</summary>
    [JsonPropertyName("name")]    public string Name { get; set; } = "";
    /// <summary>Version string of the implementation.</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

/// <summary>Describes the optional capabilities supported by an MCP server.</summary>
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

/// <summary>Capability descriptor for the <c>tools</c> feature.</summary>
public sealed class McpToolsCapability     { [JsonPropertyName("listChanged")] public bool ListChanged { get; set; } }
/// <summary>Capability descriptor for the <c>resources</c> feature.</summary>
public sealed class McpResourcesCapability { [JsonPropertyName("subscribe")] public bool Subscribe { get; set; } [JsonPropertyName("listChanged")] public bool ListChanged { get; set; } }
/// <summary>Capability descriptor for the <c>prompts</c> feature.</summary>
public sealed class McpPromptsCapability   { [JsonPropertyName("listChanged")] public bool ListChanged { get; set; } }

/// <summary>Parameters sent by the client during the <c>initialize</c> handshake.</summary>
public sealed class InitializeParams
{
    [JsonPropertyName("protocolVersion")] public string ProtocolVersion { get; set; } = "";
    [JsonPropertyName("capabilities")]    public JsonElement? Capabilities { get; set; }
    [JsonPropertyName("clientInfo")]      public McpImplementation? ClientInfo { get; set; }
}

/// <summary>Response returned by the server for an <c>initialize</c> request.</summary>
public sealed class InitializeResult
{
    [JsonPropertyName("protocolVersion")] public string ProtocolVersion { get; set; } = "";
    [JsonPropertyName("capabilities")]    public McpServerCapabilities Capabilities { get; set; } = new();
    [JsonPropertyName("serverInfo")]      public McpImplementation ServerInfo { get; set; } = new();
    [JsonPropertyName("instructions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }
}

/// <summary>Metadata describing a single tool exposed by an MCP server.</summary>
public sealed class McpToolInfo
{
    /// <summary>Unique tool name (snake_case).</summary>
    [JsonPropertyName("name")]        public string Name { get; set; } = "";
    /// <summary>Human-readable description of what the tool does.</summary>
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    /// <summary>JSON Schema object describing the tool's input parameters.</summary>
    [JsonPropertyName("inputSchema")] public JsonElement InputSchema { get; set; }
}

/// <summary>Result for a <c>tools/list</c> request.</summary>
public sealed class ToolsListResult  { [JsonPropertyName("tools")]  public List<McpToolInfo> Tools { get; set; } = []; }
/// <summary>Parameters for a <c>tools/call</c> request.</summary>
public sealed class ToolCallParams
{
    [JsonPropertyName("name")]      public string Name { get; set; } = "";
    [JsonPropertyName("arguments"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Arguments { get; set; }
}

/// <summary>A typed content block within an MCP tool call result or prompt message.</summary>
public sealed class McpContent
{
    /// <summary>Content type: <c>"text"</c>, <c>"image"</c>, or <c>"resource"</c>.</summary>
    [JsonPropertyName("type")]     public string Type { get; set; } = "text";
    /// <summary>Text value when <see cref="Type"/> is <c>"text"</c>.</summary>
    [JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }
    /// <summary>MIME type (used with binary content).</summary>
    [JsonPropertyName("mimeType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }
    /// <summary>Base-64 encoded binary data when <see cref="Type"/> is <c>"image"</c>.</summary>
    [JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }
    /// <summary>Embedded resource content when <see cref="Type"/> is <c>"resource"</c>.</summary>
    [JsonPropertyName("resource"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpResourceContent? Resource { get; set; }
}

/// <summary>Result object returned for a <c>tools/call</c> request.</summary>
public sealed class ToolCallResult
{
    /// <summary>Content blocks produced by the tool.</summary>
    [JsonPropertyName("content")] public List<McpContent> Content { get; set; } = [];
    /// <summary><c>true</c> when the tool call failed; the content will contain an error message.</summary>
    [JsonPropertyName("isError"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsError { get; set; }
}

// ── Resources models ─────────────────────────────────────────────────────

/// <summary>Metadata describing a resource exposed by an MCP server.</summary>
public sealed class McpResourceInfo
{
    [JsonPropertyName("uri")]         public string Uri { get; set; } = "";
    [JsonPropertyName("name")]        public string Name { get; set; } = "";
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("mimeType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }
}

/// <summary>A URI template that clients can use to construct resource addresses.</summary>
public sealed class McpResourceTemplate
{
    [JsonPropertyName("uriTemplate")] public string UriTemplate { get; set; } = "";
    [JsonPropertyName("name")]        public string Name { get; set; } = "";
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("mimeType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }
}

/// <summary>Result for a <c>resources/list</c> request.</summary>
public sealed class ResourcesListResult          { [JsonPropertyName("resources")]         public List<McpResourceInfo> Resources { get; set; } = []; }
/// <summary>Result for a <c>resources/templates/list</c> request.</summary>
public sealed class ResourceTemplatesListResult  { [JsonPropertyName("resourceTemplates")] public List<McpResourceTemplate> ResourceTemplates { get; set; } = []; }
/// <summary>Parameters for a <c>resources/read</c> request.</summary>
public sealed class ResourceReadParams           { [JsonPropertyName("uri")]               public string Uri { get; set; } = ""; }

/// <summary>The content of a resource as returned by a <c>resources/read</c> request.</summary>
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

/// <summary>Result for a <c>resources/read</c> request.</summary>
public sealed class ResourceReadResult { [JsonPropertyName("contents")] public List<McpResourceContent> Contents { get; set; } = []; }

// ── Prompts models ───────────────────────────────────────────────────────

/// <summary>Metadata describing a reusable prompt template exposed by an MCP server.</summary>
public sealed class McpPromptInfo
{
    [JsonPropertyName("name")]        public string Name { get; set; } = "";
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("arguments"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<McpPromptArgument>? Arguments { get; set; }
}

/// <summary>A named argument accepted by a prompt template.</summary>
public sealed class McpPromptArgument
{
    [JsonPropertyName("name")]        public string Name { get; set; } = "";
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("required"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Required { get; set; }
}

/// <summary>Result for a <c>prompts/list</c> request.</summary>
public sealed class PromptsListResult { [JsonPropertyName("prompts")] public List<McpPromptInfo> Prompts { get; set; } = []; }

/// <summary>Parameters for a <c>prompts/get</c> request.</summary>
public sealed class PromptGetParams
{
    [JsonPropertyName("name")]      public string Name { get; set; } = "";
    [JsonPropertyName("arguments"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Arguments { get; set; }
}

/// <summary>A single message in a prompt template result.</summary>
public sealed class McpPromptMessage
{
    [JsonPropertyName("role")]    public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public McpContent Content { get; set; } = new();
}

/// <summary>Result for a <c>prompts/get</c> request.</summary>
public sealed class PromptGetResult
{
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("messages")] public List<McpPromptMessage> Messages { get; set; } = [];
}

// ── Completion models ────────────────────────────────────────────────────

/// <summary>Parameters for a <c>completion/complete</c> request.</summary>
public sealed class CompletionCompleteParams
{
    [JsonPropertyName("ref")]      public CompletionRef Ref { get; set; } = new();
    [JsonPropertyName("argument")] public CompletionArgument Argument { get; set; } = new();
}

/// <summary>Identifies the target (prompt or resource) for a completion request.</summary>
public sealed class CompletionRef
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("uri"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uri { get; set; }
    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
}

/// <summary>Specifies the argument name and partial value for which completions are requested.</summary>
public sealed class CompletionArgument
{
    [JsonPropertyName("name")]  public string Name { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
}

/// <summary>Result for a <c>completion/complete</c> request.</summary>
public sealed class CompletionResult
{
    [JsonPropertyName("completion")] public CompletionValues Completion { get; set; } = new();
}

/// <summary>The set of completion values returned by the server.</summary>
public sealed class CompletionValues
{
    [JsonPropertyName("values")]  public List<string> Values { get; set; } = [];
    [JsonPropertyName("total"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Total { get; set; }
    [JsonPropertyName("hasMore"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasMore { get; set; }
}

// ── Logging models ───────────────────────────────────────────────────────

/// <summary>Parameters for a <c>logging/setLevel</c> request.</summary>
public sealed class LoggingSetLevelParams
{
    [JsonPropertyName("level")] public string Level { get; set; } = "info";
}
