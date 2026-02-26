using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  Configuration
// ═══════════════════════════════════════════════════════════════════════════

public class LMConfig
{
    public string ModelName { get; set; } = "liquid/lfm2.5-1.2b";
    public string Endpoint { get; set; } = "http://localhost:5454";
    public string ApiKey { get; set; } = "";
    public string? EmbeddingModel { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════
//  LM client
// ═══════════════════════════════════════════════════════════════════════════

public sealed class LMException(string message, int statusCode, string? body) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? ResponseBody { get; } = body;
}

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

    public async Task<ResponseResponse> RespondAsync(
        string input, string? instructions = null, string? previousResponseId = null,
        double temperature = 0, List<ToolDefinition>? tools = null,
        ReasoningConfig? reasoning = null, CancellationToken ct = default)
    {
        return await PostAsync<ResponseRequest, ResponseResponse>("/v1/responses", new()
        {
            Model = _config.ModelName, Input = input, Instructions = instructions,
            PreviousResponseId = previousResponseId, Temperature = temperature,
            Tools = tools, Reasoning = reasoning,
        }, ct);
    }

    public async Task<ResponseResponse> RespondAsync(
        IEnumerable<ResponseInput> input, string? instructions = null, string? previousResponseId = null,
        double temperature = 0, List<ToolDefinition>? tools = null,
        ReasoningConfig? reasoning = null, CancellationToken ct = default)
    {
        return await PostAsync<ResponseRequest, ResponseResponse>("/v1/responses", new()
        {
            Model = _config.ModelName, Input = input.ToList(), Instructions = instructions,
            PreviousResponseId = previousResponseId, Temperature = temperature,
            Tools = tools, Reasoning = reasoning,
        }, ct);
    }

    /// <summary>Streaming /v1/responses — yields SSE events as they arrive.</summary>
    public async IAsyncEnumerable<StreamEvent> RespondStreamingAsync(
        string input, string? instructions = null, string? previousResponseId = null,
        double temperature = 0, List<ToolDefinition>? tools = null,
        ReasoningConfig? reasoning = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new ResponseRequest
        {
            Model = _config.ModelName, Input = input, Instructions = instructions,
            PreviousResponseId = previousResponseId, Temperature = temperature,
            Tools = tools, Reasoning = reasoning, Stream = true,
        };

        await foreach (var ev in StreamRequestAsync(request, ct)) yield return ev;
    }

    /// <summary>Streaming /v1/responses with a full conversation history as input.</summary>
    public async IAsyncEnumerable<StreamEvent> RespondStreamingAsync(
        IEnumerable<ResponseInput> input, string? instructions = null,
        double temperature = 0, List<ToolDefinition>? tools = null,
        ReasoningConfig? reasoning = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new ResponseRequest
        {
            Model = _config.ModelName, Input = input.ToList(), Instructions = instructions,
            Temperature = temperature, Tools = tools, Reasoning = reasoning, Stream = true,
        };

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

    /// <summary>Describe an image using /v1/chat/completions multimodal (works on all VLM servers).</summary>
    public async Task<string> DescribeImageAsync(
        string prompt, string imageUrl, int maxTokens = 1024,
        double temperature = 0, CancellationToken ct = default)
    {
        var req = new VisionRequest
        {
            Model = _config.ModelName,
            MaxTokens = maxTokens,
            Temperature = temperature,
            Messages =
            [
                new()
                {
                    Content = [new VisionTextPart(prompt), new VisionImagePart(imageUrl)],
                }
            ],
        };
        var resp = await PostAsync<VisionRequest, VisionResponse>("/v1/chat/completions", req, ct);
        return resp.Choices.FirstOrDefault()?.Message.Content ?? "";
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

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
