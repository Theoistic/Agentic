using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentic.Mcp;

// ═══════════════════════════════════════════════════════════════════════════
//  Options
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Configuration for the Agentic MCP server registered via <see cref="McpServerExtensions.AddAgenticMcp"/>.</summary>
public sealed class McpServerOptions
{
    /// <summary>Name reported to MCP clients during the <c>initialize</c> handshake.</summary>
    public string ServerName { get; set; } = "Agentic";
    /// <summary>Version string reported to MCP clients during the <c>initialize</c> handshake.</summary>
    public string ServerVersion { get; set; } = "1.0.0";
    /// <summary>MCP protocol version advertised to clients.</summary>
    public string ProtocolVersion { get; set; } = "2025-03-26";
    /// <summary>Optional instruction text returned in the <c>initialize</c> response.</summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// When set, every request must supply this value as a Bearer token in the
    /// <c>Authorization</c> header. <c>null</c> = no key required (open server).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// When set, requests that carry an <c>Origin</c> header are only accepted if
    /// the origin appears in this list. Use <c>"*"</c> as an entry to allow all
    /// browser origins while still blocking unlisted ones.
    /// Requests without an <c>Origin</c> header (CLI tools, agents) are unaffected.
    /// </summary>
    public List<string>? AllowedOrigins { get; set; }

    /// <summary>
    /// Maximum time allowed for a single tool call. Defaults to 55 seconds so the
    /// server can respond with a clean error before LM Studio's 60-second timeout
    /// drops the SSE connection. Set to <see cref="Timeout.InfiniteTimeSpan"/> to
    /// disable.
    /// </summary>
    public TimeSpan ToolCallTimeout { get; set; } = TimeSpan.FromSeconds(55);
}

// ═══════════════════════════════════════════════════════════════════════════
//  Request handler
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Handles incoming JSON-RPC 2.0 requests from MCP clients, dispatching to tools,
/// resources, prompts, and protocol methods (<c>initialize</c>, <c>ping</c>, etc.).
/// </summary>
public sealed class McpRequestHandler
{
    private readonly Agentic.ToolRegistry _tools;
    private readonly ResourceRegistry _resources;
    private readonly PromptRegistry _prompts;
    private readonly McpServerOptions _options;
    private readonly ILogger<McpRequestHandler> _logger;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Initialises the handler with all required services (resolved from DI).</summary>
    public McpRequestHandler(Agentic.ToolRegistry tools, ResourceRegistry resources, PromptRegistry prompts,
        McpServerOptions options, ILogger<McpRequestHandler> logger)
    {
        _tools = tools;
        _resources = resources;
        _prompts = prompts;
        _options = options;
        _logger = logger;
    }

    /// <summary>Logs a raw transport-level event (caller IP + verb) at Information level.</summary>
    public void LogRequest(string remoteIp, string verb) =>
        _logger.LogInformation("MCP {Verb} from {Remote}", verb, remoteIp);

    /// <summary>
    /// Dispatches a single JSON-RPC request to the appropriate MCP handler.
    /// Returns <c>null</c> for notifications (requests without an <c>id</c>) or when the client disconnects.
    /// </summary>
    public async Task<JsonRpcResponse?> HandleAsync(JsonRpcRequest request, ToolContext? toolContext = null, CancellationToken ct = default)
    {
        _logger.LogDebug("MCP <-- {Method} (id={Id})", request.Method, request.Id);

        if (request.IsNotification) return null;

        try
        {
            return request.Method switch
            {
                "initialize"     => HandleInitialize(request),
                "ping"           => JsonRpcResponse.Success(request.Id, new { }),
                "tools/list"     => HandleToolsList(request),
                "tools/call"     => await HandleToolsCall(request, toolContext, ct),
                "resources/list"           => HandleResourcesList(request),
                "resources/read"           => await HandleResourcesRead(request),
                "resources/templates/list" => HandleResourcesTemplatesList(request),
                "prompts/list"             => HandlePromptsList(request),
                "prompts/get"              => HandlePromptsGet(request),
                "completion/complete"      => HandleCompletionComplete(request),
                "logging/setLevel"         => HandleLoggingSetLevel(request),
                _ => JsonRpcResponse.Fail(request.Id, JsonRpcError.MethodNotFound, $"Method not found: {request.Method}"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("MCP client disconnected during {Method}", request.Method);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP handler error for {Method}", request.Method);
            return JsonRpcResponse.Fail(request.Id, JsonRpcError.InternalError, ex.Message);
        }
    }

    private JsonRpcResponse HandleInitialize(JsonRpcRequest request)
    {
        var cp = request.Params.HasValue
            ? request.Params.Value.Deserialize<InitializeParams>(s_json)
            : null;

        _logger.LogInformation("MCP initialize from {Client} v{Ver}",
            cp?.ClientInfo?.Name ?? "unknown", cp?.ClientInfo?.Version ?? "?");

        return JsonRpcResponse.Success(request.Id, new InitializeResult
        {
            ProtocolVersion = _options.ProtocolVersion,
            Capabilities = new()
            {
                Tools = new(),
                Resources = _resources.HasAny ? new() : null,
                Prompts = _prompts.HasAny ? new() : null,
                Logging = new { },
            },
            ServerInfo = new() { Name = _options.ServerName, Version = _options.ServerVersion },
            Instructions = _options.Instructions,
        });
    }

    private JsonRpcResponse HandleToolsList(JsonRpcRequest request)
    {
        var defaultSchema = Agentic.ToolSchema.Parse("""{"type":"object"}""");
        var tools = _tools.GetAllDescriptors().Select(d => new McpToolInfo
        {
            Name = d.Name,
            Description = d.Description,
            InputSchema = d.ParameterSchema ?? defaultSchema,
        }).ToList();

        _logger.LogDebug("MCP tools/list --> {Count} tools", tools.Count);
        return JsonRpcResponse.Success(request.Id, new ToolsListResult { Tools = tools });
    }

    private async Task<JsonRpcResponse> HandleToolsCall(JsonRpcRequest request, ToolContext? toolContext, CancellationToken ct = default)
    {
        var cp = request.Params.HasValue
            ? request.Params.Value.Deserialize<ToolCallParams>(s_json)
            : null;

        if (cp is null || string.IsNullOrEmpty(cp.Name))
            return JsonRpcResponse.Fail(request.Id, JsonRpcError.InvalidParams, "Missing required parameter: name");

        _logger.LogInformation("MCP tools/call --> {Tool}", cp.Name);

        using var timeoutCts = _options.ToolCallTimeout == Timeout.InfiniteTimeSpan
            ? null
            : new CancellationTokenSource(_options.ToolCallTimeout);
        using var linkedCts = timeoutCts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            var result = await _tools.InvokeAsync(cp.Name, cp.Arguments, toolContext, linkedCts.Token);
            return JsonRpcResponse.Success(request.Id, new ToolCallResult
            { Content = [new McpContent { Text = result }] });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogWarning("MCP client disconnected while tool '{Tool}' was running", cp.Name);
            throw;  // let HandleAsync's catch log and return null
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            var seconds = (int)_options.ToolCallTimeout.TotalSeconds;
            _logger.LogWarning("MCP tool '{Tool}' timed out after {Seconds}s", cp.Name, seconds);
            return JsonRpcResponse.Success(request.Id, new ToolCallResult
            {
                Content = [new McpContent { Text = $"Tool '{cp.Name}' timed out after {seconds}s. Try processing fewer pages at a time." }],
                IsError = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP tool '{Tool}' failed", cp.Name);
            return JsonRpcResponse.Success(request.Id, new ToolCallResult
            { Content = [new McpContent { Text = $"Error: {ex.Message}" }], IsError = true });
        }
    }

    // ── Resources ─────────────────────────────────────────────────────────

    private JsonRpcResponse HandleResourcesList(JsonRpcRequest request)
    {
        var result = new ResourcesListResult { Resources = _resources.ListResources() };
        _logger.LogDebug("MCP resources/list --> {Count} resources", result.Resources.Count);
        return JsonRpcResponse.Success(request.Id, result);
    }

    private JsonRpcResponse HandleResourcesTemplatesList(JsonRpcRequest request)
    {
        var result = new ResourceTemplatesListResult { ResourceTemplates = _resources.ListTemplates() };
        _logger.LogDebug("MCP resources/templates/list --> {Count} templates", result.ResourceTemplates.Count);
        return JsonRpcResponse.Success(request.Id, result);
    }

    private async Task<JsonRpcResponse> HandleResourcesRead(JsonRpcRequest request)
    {
        var cp = request.Params.HasValue
            ? request.Params.Value.Deserialize<ResourceReadParams>(s_json)
            : null;

        if (cp is null || string.IsNullOrEmpty(cp.Uri))
            return JsonRpcResponse.Fail(request.Id, JsonRpcError.InvalidParams, "Missing required parameter: uri");

        _logger.LogInformation("MCP resources/read --> {Uri}", cp.Uri);

        try
        {
            var result = await _resources.ReadAsync(cp.Uri);
            return JsonRpcResponse.Success(request.Id, result);
        }
        catch (KeyNotFoundException)
        {
            return JsonRpcResponse.Fail(request.Id, JsonRpcError.InvalidParams, $"Resource not found: {cp.Uri}");
        }
    }

    // ── Prompts ───────────────────────────────────────────────────────────

    private JsonRpcResponse HandlePromptsList(JsonRpcRequest request)
    {
        var result = new PromptsListResult { Prompts = _prompts.ListPrompts() };
        _logger.LogDebug("MCP prompts/list --> {Count} prompts", result.Prompts.Count);
        return JsonRpcResponse.Success(request.Id, result);
    }

    private JsonRpcResponse HandlePromptsGet(JsonRpcRequest request)
    {
        var cp = request.Params.HasValue
            ? request.Params.Value.Deserialize<PromptGetParams>(s_json)
            : null;

        if (cp is null || string.IsNullOrEmpty(cp.Name))
            return JsonRpcResponse.Fail(request.Id, JsonRpcError.InvalidParams, "Missing required parameter: name");

        _logger.LogInformation("MCP prompts/get --> {Prompt}", cp.Name);

        try
        {
            var result = _prompts.Get(cp.Name, cp.Arguments);
            return JsonRpcResponse.Success(request.Id, result);
        }
        catch (KeyNotFoundException)
        {
            return JsonRpcResponse.Fail(request.Id, JsonRpcError.InvalidParams, $"Prompt not found: {cp.Name}");
        }
    }

    // ── Completion ────────────────────────────────────────────────────────

    private JsonRpcResponse HandleCompletionComplete(JsonRpcRequest request)
    {
        var cp = request.Params.HasValue
            ? request.Params.Value.Deserialize<CompletionCompleteParams>(s_json)
            : null;

        if (cp is null)
            return JsonRpcResponse.Fail(request.Id, JsonRpcError.InvalidParams, "Missing params");

        _logger.LogDebug("MCP completion/complete for {Type} arg={Name} value={Value}",
            cp.Ref.Type, cp.Argument.Name, cp.Argument.Value);

        return JsonRpcResponse.Success(request.Id, new CompletionResult());
    }

    // ── Logging ──────────────────────────────────────────────────────────

    private JsonRpcResponse HandleLoggingSetLevel(JsonRpcRequest request)
    {
        var cp = request.Params.HasValue
            ? request.Params.Value.Deserialize<LoggingSetLevelParams>(s_json)
            : null;

        _logger.LogInformation("MCP logging level set to {Level}", cp?.Level ?? "info");
        return JsonRpcResponse.Success(request.Id, new { });
    }
}
