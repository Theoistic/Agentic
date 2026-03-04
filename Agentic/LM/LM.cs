using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  Configuration
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Configuration for an <see cref="LM"/> client instance.</summary>
public class LMConfig
{
    /// <summary>Model identifier sent in every request (e.g. <c>"gpt-4o"</c>).</summary>
    public string ModelName { get; set; } = "liquid/lfm2.5-1.2b";
    /// <summary>Base URL of the OpenAI-compatible API server (e.g. <c>"http://localhost:1234"</c>).</summary>
    public string Endpoint { get; set; } = "http://localhost:5454";
    /// <summary>Optional Bearer token sent in the <c>Authorization</c> header.</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>Model identifier used for embedding requests. Required when calling <see cref="LM.EmbedAsync"/> or <see cref="LM.EmbedBatchAsync"/>.</summary>
    public string? EmbeddingModel { get; set; }
    /// <summary>
    /// Default thinking configuration applied to every <c>/v1/responses</c> call.
    /// Can be overridden per-call or per-agent via <see cref="AgentOptions.Thinking"/>.
    /// <c>null</c> = omit the flag entirely (server default).
    /// </summary>
    public ThinkingConfig? Thinking { get; set; }
    /// <summary>
    /// Named model aliases. Map a short key (e.g. <c>"advanced"</c>, <c>"ocr"</c>) to the full
    /// model identifier sent to the server. Resolved by <see cref="LM.ResolveModel"/>.
    /// </summary>
    public Dictionary<string, string> Models { get; set; } = [];
}

// ═══════════════════════════════════════════════════════════════════════════
//  LM client
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Thrown when an <see cref="LM"/> API call fails with an HTTP error or network problem.</summary>
/// <param name="message">Human-readable error description.</param>
/// <param name="statusCode">HTTP status code (0 when the server was unreachable).</param>
/// <param name="body">Raw response body, if available.</param>
public sealed class LMException(string message, int statusCode, string? body) : Exception(message)
{
    /// <summary>HTTP status code returned by the server (0 when the server was unreachable or timed out).</summary>
    public int StatusCode { get; } = statusCode;
    /// <summary>Raw HTTP response body, or <c>null</c> when none was available.</summary>
    public string? ResponseBody { get; } = body;
}

/// <summary>
/// OpenAI-compatible REST client supporting <c>/v1/responses</c> (streaming + non-streaming)
/// and <c>/v1/embeddings</c>.
/// </summary>
public sealed class LM : IDisposable
{
    private readonly HttpClient _http;
    private readonly LMConfig _config;
    private readonly bool _ownsClient;

    private static readonly JsonSerializerOptions s_json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Initialises a new LM client.</summary>
    /// <param name="config">Connection and model configuration.</param>
    /// <param name="httpClient">Optional shared <see cref="HttpClient"/>; when <c>null</c> a new instance is created and owned by this class.</param>
    public LM(LMConfig config, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress ??= new Uri(_config.Endpoint.TrimEnd('/'));
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new("Bearer", _config.ApiKey);
    }

    /// <summary>Sends a single-turn request to <c>/v1/responses</c> and returns the full response.</summary>
    /// <param name="input">User message text.</param>
    /// <param name="instructions">Optional system/instruction text.</param>
    /// <param name="previousResponseId">ID of the previous response for multi-turn chaining.</param>
    /// <param name="temperature">Sampling temperature.</param>
    /// <param name="tools">Tool definitions (e.g. MCP servers) available to the model.</param>
    /// <param name="reasoning">Optional reasoning configuration.</param>
    /// <param name="thinking">Qwen thinking override; <c>null</c> falls back to <see cref="LMConfig.Thinking"/>.</param>
    /// <param name="model">Model override. Accepts a named alias from <see cref="LMConfig.Models"/> or a literal model ID; <c>null</c> falls back to <see cref="LMConfig.ModelName"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ResponseResponse> RespondAsync(
        string input, string? instructions = null, string? previousResponseId = null,
        double temperature = 0, List<ToolDefinition>? tools = null,
        ReasoningConfig? reasoning = null, ThinkingConfig? thinking = null,
        string? model = null, CancellationToken ct = default)
    {
        var req = new ResponseRequest
        {
            Model = ResolveModel(model), Input = input, Instructions = instructions,
            PreviousResponseId = previousResponseId, Temperature = temperature,
            Tools = tools, Reasoning = reasoning,
        };
        ApplyThinking(req, thinking ?? _config.Thinking);
        return await PostAsync<ResponseRequest, ResponseResponse>("/v1/responses", req, ct);
    }

    /// <summary>Sends a multi-turn conversation history to <c>/v1/responses</c> and returns the full response.</summary>
    /// <param name="input">Ordered list of conversation turns to replay.</param>
    /// <param name="instructions">Optional system/instruction text.</param>
    /// <param name="previousResponseId">ID of the previous response for multi-turn chaining.</param>
    /// <param name="temperature">Sampling temperature.</param>
    /// <param name="tools">Tool definitions available to the model.</param>
    /// <param name="reasoning">Optional reasoning configuration.</param>
    /// <param name="thinking">Qwen thinking override; <c>null</c> falls back to <see cref="LMConfig.Thinking"/>.</param>
    /// <param name="model">Model override. Accepts a named alias from <see cref="LMConfig.Models"/> or a literal model ID; <c>null</c> falls back to <see cref="LMConfig.ModelName"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ResponseResponse> RespondAsync(
        IEnumerable<ResponseInput> input, string? instructions = null, string? previousResponseId = null,
        double temperature = 0, List<ToolDefinition>? tools = null,
        ReasoningConfig? reasoning = null, ThinkingConfig? thinking = null,
        string? model = null, CancellationToken ct = default)
    {
        var req = new ResponseRequest
        {
            Model = ResolveModel(model), Input = input.ToList(), Instructions = instructions,
            PreviousResponseId = previousResponseId, Temperature = temperature,
            Tools = tools, Reasoning = reasoning,
        };
        ApplyThinking(req, thinking ?? _config.Thinking);
        return await PostAsync<ResponseRequest, ResponseResponse>("/v1/responses", req, ct);
    }

    /// <summary>Streaming /v1/responses — yields SSE events as they arrive.</summary>
    /// <param name="input">User message text.</param>
    /// <param name="instructions">Optional system/instruction text.</param>
    /// <param name="previousResponseId">ID of the previous response for multi-turn chaining.</param>
    /// <param name="temperature">Sampling temperature.</param>
    /// <param name="tools">Tool definitions available to the model.</param>
    /// <param name="reasoning">Optional reasoning configuration.</param>
    /// <param name="thinking">Qwen thinking override; <c>null</c> falls back to <see cref="LMConfig.Thinking"/>.</param>
    /// <param name="model">Model override. Accepts a named alias from <see cref="LMConfig.Models"/> or a literal model ID; <c>null</c> falls back to <see cref="LMConfig.ModelName"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async IAsyncEnumerable<StreamEvent> RespondStreamingAsync(
        string input, string? instructions = null, string? previousResponseId = null,
        double temperature = 0, List<ToolDefinition>? tools = null,
        ReasoningConfig? reasoning = null, ThinkingConfig? thinking = null,
        string? model = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new ResponseRequest
        {
            Model = ResolveModel(model), Input = input, Instructions = instructions,
            PreviousResponseId = previousResponseId, Temperature = temperature,
            Tools = tools, Reasoning = reasoning, Stream = true,
        };
        ApplyThinking(request, thinking ?? _config.Thinking);

        await foreach (var ev in StreamRequestAsync(request, ct)) yield return ev;
    }

    /// <summary>Streaming /v1/responses with a full conversation history as input.</summary>
    /// <param name="input">Ordered list of conversation turns to replay.</param>
    /// <param name="instructions">Optional system/instruction text.</param>
    /// <param name="previousResponseId">ID of the previous response for multi-turn chaining.</param>
    /// <param name="temperature">Sampling temperature.</param>
    /// <param name="tools">Tool definitions available to the model.</param>
    /// <param name="reasoning">Optional reasoning configuration.</param>
    /// <param name="thinking">Qwen thinking override; <c>null</c> falls back to <see cref="LMConfig.Thinking"/>.</param>
    /// <param name="model">Model override. Accepts a named alias from <see cref="LMConfig.Models"/> or a literal model ID; <c>null</c> falls back to <see cref="LMConfig.ModelName"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async IAsyncEnumerable<StreamEvent> RespondStreamingAsync(
        IEnumerable<ResponseInput> input, string? instructions = null,
        string? previousResponseId = null,
        double temperature = 0, List<ToolDefinition>? tools = null,
        ReasoningConfig? reasoning = null, ThinkingConfig? thinking = null,
        string? model = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new ResponseRequest
        {
            Model = ResolveModel(model), Input = input.ToList(), Instructions = instructions,
            PreviousResponseId = previousResponseId,
            Temperature = temperature, Tools = tools, Reasoning = reasoning, Stream = true,
        };
        ApplyThinking(request, thinking ?? _config.Thinking);

        await foreach (var ev in StreamRequestAsync(request, ct)) yield return ev;
    }

    private async IAsyncEnumerable<StreamEvent> StreamRequestAsync(
        ResponseRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/responses");
        msg.Content = JsonContent.Create(request, options: s_json);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LMException("LM streaming request timed out.", 0, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            throw new LMException($"LM server unreachable at {_config.Endpoint}: {ex.Message}", 0, ex.Message);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                throw new LMException($"LM streaming failed ({(int)resp.StatusCode}): {err}", (int)resp.StatusCode, err);
            }

            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (IOException)
                {
                    break;  // connection closed (abrupt or graceful) — treat as end of stream
                }
                if (line is null) break;

                if (line.Length == 0 || line[0] is ':' or 'e') continue;   // blank, comment, event:
                if (!line.StartsWith("data:")) continue;
                var json = line["data:".Length..].TrimStart();
                if (json is "" or "[DONE]") continue;

                var evt = JsonSerializer.Deserialize<StreamEvent>(json, s_json);
                if (evt is not null) yield return evt;
            }
        }
    }

    // ── Embeddings ───────────────────────────────────────────────────────

    /// <summary>Generate an embedding vector for a single text input.</summary>
    public async Task<float[]> EmbedAsync(string input, CancellationToken ct = default)
    {
        var model = _config.EmbeddingModel
            ?? throw new InvalidOperationException("EmbeddingModel is not configured in LMConfig.");
        var resp = await PostAsync<EmbeddingRequest, EmbeddingResponse>("/v1/embeddings", new()
        {
            Model = model, Input = input,
        }, ct);
        return resp.Data.FirstOrDefault()?.Embedding ?? [];
    }

    /// <summary>Generate embedding vectors for multiple text inputs in a single batch.</summary>
    public async Task<List<float[]>> EmbedBatchAsync(IEnumerable<string> inputs, CancellationToken ct = default)
    {
        var model = _config.EmbeddingModel
            ?? throw new InvalidOperationException("EmbeddingModel is not configured in LMConfig.");
        var resp = await PostAsync<EmbeddingRequest, EmbeddingResponse>("/v1/embeddings", new()
        {
            Model = model, Input = inputs.ToList(),
        }, ct);
        return resp.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToList();
    }

    private async Task<TResp> PostAsync<TReq, TResp>(string path, TReq body, CancellationToken ct)
    {
        HttpResponseMessage r;
        try
        {
            r = await _http.PostAsJsonAsync(path, body, s_json, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LMException($"LM request timed out ({path}).", 0, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            throw new LMException($"LM server unreachable at {_config.Endpoint} ({path}): {ex.Message}", 0, ex.Message);
        }

        using (r)
        {
            if (!r.IsSuccessStatusCode)
            {
                var err = await r.Content.ReadAsStringAsync(ct);
                throw new LMException($"LM {path} failed ({(int)r.StatusCode}): {err}", (int)r.StatusCode, err);
            }
            return await r.Content.ReadFromJsonAsync<TResp>(s_json, ct)
                ?? throw new LMException($"LM returned null for {path}.", (int)r.StatusCode, null);
        }
    }

    /// <summary>
    /// Checks whether the LM server is reachable by requesting its model list.
    /// Returns true if it responds with any HTTP success status.
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync("/v1/models", ct);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static void ApplyThinking(ResponseRequest request, ThinkingConfig? thinking)
    {
        if (thinking is null) return;
        request.EnableThinking = thinking.Enabled;
    }

    /// <summary>
    /// Resolves a model alias or literal model ID to the actual model name sent to the server.
    /// <list type="bullet">
    /// <item><c>null</c> → <see cref="LMConfig.ModelName"/> (the default)</item>
    /// <item>key found in <see cref="LMConfig.Models"/> → mapped value</item>
    /// <item>otherwise → the string is used as a literal model ID</item>
    /// </list>
    /// </summary>
    public string ResolveModel(string? alias)
    {
        if (alias is null) return _config.ModelName;
        if (_config.Models.TryGetValue(alias, out var resolved)) return resolved;
        return alias;
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
