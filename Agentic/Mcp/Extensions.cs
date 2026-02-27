using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentic.Mcp;

// ═══════════════════════════════════════════════════════════════════════════
//  DI + Pipeline extensions
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Extension methods for registering and mapping the Agentic MCP server in an ASP.NET Core application.</summary>
public static class McpServerExtensions
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Registers the MCP server services: <see cref="McpServerOptions"/>, <see cref="Agentic.ToolRegistry"/>,
    /// <see cref="ResourceRegistry"/>, <see cref="PromptRegistry"/>, and <see cref="McpRequestHandler"/>.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">Optional callback to customise <see cref="McpServerOptions"/>.</param>
    public static IServiceCollection AddAgenticMcp(this IServiceCollection services, Action<McpServerOptions>? configure = null)
    {
        var options = new McpServerOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<Agentic.ToolRegistry>();
        services.AddSingleton<ResourceRegistry>();
        services.AddSingleton<PromptRegistry>();
        services.AddSingleton<McpRequestHandler>();
        return services;
    }

    /// <summary>
    /// Maps the MCP server endpoints (POST, GET SSE, DELETE) under <paramref name="pattern"/>.
    /// Call after <see cref="AddAgenticMcp"/>.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">Route prefix; defaults to <c>"/mcp"</c>.</param>
    public static IEndpointRouteBuilder MapMcpServer(this IEndpointRouteBuilder endpoints, string pattern = "/mcp")
    {
        var g = endpoints.MapGroup(pattern);

        g.AddEndpointFilter(async (ctx, next) =>
        {
            var options = ctx.HttpContext.RequestServices.GetRequiredService<McpServerOptions>();

            // ── Origin check (browser / CORS boundary) ────────────────────
            if (options.AllowedOrigins is { Count: > 0 })
            {
                var origin = ctx.HttpContext.Request.Headers.Origin.ToString();
                if (!string.IsNullOrEmpty(origin)
                    && !options.AllowedOrigins.Contains("*")
                    && !options.AllowedOrigins.Contains(origin))
                {
                    ctx.HttpContext.Response.StatusCode = 403;
                    return Results.StatusCode(403);
                }
            }

            // ── API key check (agent identity) ────────────────────────────
            if (options.ApiKey is not null)
            {
                var auth = ctx.HttpContext.Request.Headers.Authorization.ToString();
                var headerValid = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                                  && auth["Bearer ".Length..] == options.ApiKey;
                var queryValid  = ctx.HttpContext.Request.Query["key"].ToString() == options.ApiKey;
                if (!headerValid && !queryValid)
                {
                    ctx.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
                    ctx.HttpContext.Response.StatusCode = 401;
                    return Results.StatusCode(401);
                }
            }

            return await next(ctx);
        });

        g.MapPost("/", HandlePost).AllowAnonymous();
        g.MapGet("/", HandleGet).AllowAnonymous();
        g.MapDelete("/", HandleDelete).AllowAnonymous();
        return endpoints;
    }

    /// <summary>Returns the full MCP server URL for the running application (useful for wiring up agents in the same process).</summary>
    /// <param name="app">The running web application.</param>
    /// <param name="path">The MCP route path; defaults to <c>"/mcp"</c>.</param>
    public static string GetMcpUrl(this WebApplication app, string path = "/mcp")
    {
        var addr = app.Urls.FirstOrDefault(u => u.StartsWith("http://"))
                ?? app.Urls.FirstOrDefault() ?? "http://localhost:5000";
        return $"{addr.TrimEnd('/')}{path}";
    }

    private static async Task HandlePost(HttpContext ctx, McpRequestHandler handler)
    {
        var ct = ctx.RequestAborted;
        handler.LogRequest(ctx.Connection.RemoteIpAddress?.ToString() ?? "?", "POST");

        string body;
        using (var reader = new StreamReader(ctx.Request.Body))
            body = await reader.ReadToEndAsync(ct);

        if (string.IsNullOrWhiteSpace(body)) { ctx.Response.StatusCode = 400; return; }

        // JSON-RPC 2.0 batch support: body may be a single object or an array
        if (body.TrimStart().StartsWith('['))
        {
            List<JsonRpcRequest>? requests;
            try { requests = JsonSerializer.Deserialize<List<JsonRpcRequest>>(body, s_json); }
            catch (JsonException)
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsJsonAsync(
                    JsonRpcResponse.Fail(null, JsonRpcError.ParseError, "Invalid JSON"), s_json);
                return;
            }

            if (requests is null || requests.Count == 0) { ctx.Response.StatusCode = 400; return; }

            var responses = new List<JsonRpcResponse>();
            foreach (var req in requests)
            {
                var resp = await handler.HandleAsync(req, ct);
                if (resp is not null) responses.Add(resp);
            }

            if (responses.Count == 0) { ctx.Response.StatusCode = 202; return; }
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(responses, s_json);
            return;
        }

        JsonRpcRequest? request;
        try { request = JsonSerializer.Deserialize<JsonRpcRequest>(body, s_json); }
        catch (JsonException)
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(
                JsonRpcResponse.Fail(null, JsonRpcError.ParseError, "Invalid JSON"), s_json);
            return;
        }

        if (request is null) { ctx.Response.StatusCode = 400; return; }

        var response = await handler.HandleAsync(request, ct);
        if (response is null) { ctx.Response.StatusCode = 202; return; }

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(response, s_json);
    }

    private static async Task HandleGet(HttpContext ctx, McpRequestHandler handler)
    {
        var remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        handler.LogRequest(remote, "SSE open");
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Connection = "keep-alive";
        await ctx.Response.Body.FlushAsync();
        try { await Task.Delay(Timeout.Infinite, ctx.RequestAborted); }
        catch (OperationCanceledException) { handler.LogRequest(remote, "SSE closed"); }
    }

    private static Task HandleDelete(HttpContext ctx)
    {
        ctx.Response.StatusCode = 200;
        return Task.CompletedTask;
    }
}
