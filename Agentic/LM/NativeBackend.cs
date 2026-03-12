using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentic.Runtime.Core;
using Mantle = Agentic.Runtime.Mantle;

namespace Agentic;

/// <summary>
/// Backend implementation that runs inference through <see cref="Mantle.LmSession"/>.
/// </summary>
public sealed class NativeBackend : ILLMBackend, IAsyncDisposable, IDisposable
{
    private Mantle.LmSessionOptions _sessionOptions;
    private readonly string _defaultModel;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly HashSet<string> _projectedLocalToolNames = new(StringComparer.Ordinal);

    private readonly LlamaBackend? _autoBackend;
    private readonly string? _autoCudaVersion;
    private readonly string? _autoReleaseTag;
    private readonly string? _autoInstallRoot;
    private readonly IProgress<(string message, double percent)>? _installProgress;

    private Mantle.LmSession? _session;
    private bool _disposed;

    /// <summary>
    /// Creates a native backend that lazily initializes a runtime session on first use.
    /// </summary>
    public NativeBackend(Mantle.LmSessionOptions sessionOptions, string? modelName = null)
    {
        ArgumentNullException.ThrowIfNull(sessionOptions);
        _sessionOptions = sessionOptions;
        _defaultModel = string.IsNullOrWhiteSpace(modelName)
            ? Path.GetFileNameWithoutExtension(sessionOptions.ModelPath) ?? "model"
            : modelName;
    }

    /// <summary>
    /// Creates a native backend that automatically downloads and installs the llama.cpp runtime
    /// from the latest GitHub release when no local installation is found, then lazily initializes
    /// the session on first use.
    /// </summary>
    /// <param name="sessionOptions">Session options without <c>BackendDirectory</c> - it is resolved automatically.</param>
    /// <param name="backend">The accelerator backend to use.</param>
    /// <param name="cudaVersion">Preferred CUDA version, e.g. <c>"12.4"</c>. When <see langword="null"/> the highest available version is chosen.</param>
    /// <param name="releaseTag">Pin to a specific release tag, e.g. <c>"b8269"</c>. When <see langword="null"/> the latest release is used.</param>
    /// <param name="installRoot">Override the default runtime install root directory.</param>
    /// <param name="installProgress">Optional progress reporter for the download and extraction.</param>
    /// <param name="modelName">Optional model name override.</param>
    public NativeBackend(
        Mantle.LmSessionOptions sessionOptions,
        LlamaBackend backend,
        string? cudaVersion = null,
        string? releaseTag = null,
        string? installRoot = null,
        IProgress<(string message, double percent)>? installProgress = null,
        string? modelName = null)
        : this(sessionOptions, modelName)
    {
        _autoBackend = backend;
        _autoCudaVersion = cudaVersion;
        _autoReleaseTag = releaseTag;
        _autoInstallRoot = installRoot;
        _installProgress = installProgress;
    }

    /// <summary>
    /// Gets the resolved backend directory after the session has been initialized,
    /// or <see langword="null"/> if the runtime has not been loaded yet.
    /// </summary>
    public string? BackendDirectory =>
        string.IsNullOrEmpty(_sessionOptions.BackendDirectory) ? null : _sessionOptions.BackendDirectory;

    public Task<ResponseResponse> RespondAsync(
        string input, string? instructions = null, string? previousResponseId = null,
        InferenceConfig? inference = null, List<ToolDefinition>? tools = null,
        ReasoningEffort? reasoning = null,
        string? model = null, CancellationToken ct = default)
        => RespondAsync([ResponseInput.User(input)], instructions, previousResponseId, inference, tools, reasoning, model, ct);

    public async Task<ResponseResponse> RespondAsync(
        IEnumerable<ResponseInput> input, string? instructions = null, string? previousResponseId = null,
        InferenceConfig? inference = null, List<ToolDefinition>? tools = null,
        ReasoningEffort? reasoning = null,
        string? model = null, CancellationToken ct = default)
    {
        using var activity = AgenticTelemetry.StartActivity("lm.native.respond");
        AgenticTelemetry.LmRequests.Add(1, new KeyValuePair<string, object?>("agentic.lm.method", "NativeRespond"));
        var sw = Stopwatch.StartNew();
        try
        {
            activity?.SetTag("gen_ai.system", "native");
            activity?.SetTag("gen_ai.request.model", ResolveModel(model));
            var session = await EnsureSessionAsync(ct);
            var request = CreateRequest(input, instructions, previousResponseId, inference, tools, reasoning, model, stream: false);
            var response = await session.CreateResponseAsync(request, ct);
            var result = MapResponse(response);
            activity?.SetTag("gen_ai.response.id", result.Id);
            return result;
        }
        catch (Exception ex)
        {
            AgenticTelemetry.LmRequestErrors.Add(1, new KeyValuePair<string, object?>("agentic.lm.method", "NativeRespond"));
            AgenticTelemetry.RecordException(activity, ex);
            throw;
        }
        finally
        {
            AgenticTelemetry.LmRequestDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("agentic.lm.method", "NativeRespond"));
        }
    }

    public async IAsyncEnumerable<StreamEvent> RespondStreamingAsync(
        string input, string? instructions = null, string? previousResponseId = null,
        InferenceConfig? inference = null, List<ToolDefinition>? tools = null,
        ReasoningEffort? reasoning = null,
        string? model = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in RespondStreamingAsync([ResponseInput.User(input)], instructions, previousResponseId, inference, tools, reasoning, model, ct))
            yield return evt;
    }

    public async IAsyncEnumerable<StreamEvent> RespondStreamingAsync(
        IEnumerable<ResponseInput> input, string? instructions = null,
        string? previousResponseId = null,
        InferenceConfig? inference = null, List<ToolDefinition>? tools = null,
        ReasoningEffort? reasoning = null,
        string? model = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct);
        var request = CreateRequest(input, instructions, previousResponseId, inference, tools, reasoning, model, stream: true);

        int toolOutputIndex = 0;

        await foreach (var chunk in session.GenerateResponseAsync(request, ct))
        {
            if (!string.IsNullOrEmpty(chunk.Text))
            {
                yield return new StreamEvent
                {
                    Type = "response.output_text.delta",
                    OutputIndex = 0,
                    ContentIndex = 0,
                    Delta = chunk.Text,
                    Text = chunk.Text,
                };
            }

            if (chunk.ToolCalls is { Count: > 0 })
            {
                foreach (var call in chunk.ToolCalls)
                {
                    yield return new StreamEvent
                    {
                        Type = "response.output_item.done",
                        OutputIndex = toolOutputIndex++,
                        Item = new ResponseOutputItem
                        {
                            Type = "function_call",
                            Name = call.Name,
                            CallId = call.CallId,
                            Arguments = JsonSerializer.Serialize(call.Arguments),
                        }
                    };
                }
            }
        }

        if (session.LastResponse is not null)
        {
            yield return new StreamEvent
            {
                Type = "response.completed",
                Response = MapResponse(session.LastResponse),
            };
        }
    }

    public async Task<float[]> EmbedAsync(string input, CancellationToken ct = default)
    {
        using var activity = AgenticTelemetry.StartActivity("lm.native.embed");
        AgenticTelemetry.EmbeddingRequests.Add(1);
        AgenticTelemetry.LmRequests.Add(1, new KeyValuePair<string, object?>("agentic.lm.method", "NativeEmbed"));
        try
        {
            activity?.SetTag("gen_ai.system", "native");
            ArgumentException.ThrowIfNullOrWhiteSpace(input);
            var session = await EnsureSessionAsync(ct);
            var result = await session.EmbedAsync(input, ct);
            activity?.SetTag("agentic.embedding.dimensions", result.Length);
            return result;
        }
        catch (Exception ex)
        {
            AgenticTelemetry.LmRequestErrors.Add(1, new KeyValuePair<string, object?>("agentic.lm.method", "NativeEmbed"));
            AgenticTelemetry.RecordException(activity, ex);
            throw;
        }
    }

    public async Task<List<float[]>> EmbedBatchAsync(IEnumerable<string> inputs, CancellationToken ct = default)
    {
        using var activity = AgenticTelemetry.StartActivity("lm.native.embed_batch");
        AgenticTelemetry.EmbeddingRequests.Add(1);
        AgenticTelemetry.LmRequests.Add(1, new KeyValuePair<string, object?>("agentic.lm.method", "NativeEmbedBatch"));
        try
        {
            activity?.SetTag("gen_ai.system", "native");
            ArgumentNullException.ThrowIfNull(inputs);

            var vectors = new List<float[]>();
            foreach (var input in inputs)
                vectors.Add(await EmbedAsync(input, ct));
            activity?.SetTag("agentic.embedding.count", vectors.Count);
            return vectors;
        }
        catch (Exception ex)
        {
            AgenticTelemetry.LmRequestErrors.Add(1, new KeyValuePair<string, object?>("agentic.lm.method", "NativeEmbedBatch"));
            AgenticTelemetry.RecordException(activity, ex);
            throw;
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureSessionAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        if (_session is not null)
            await _session.DisposeAsync();

        _sessionLock.Dispose();
        _disposed = true;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task<Mantle.LmSession> EnsureSessionAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        if (_session is not null)
            return _session;

        await _sessionLock.WaitAsync(ct);
        try
        {
            if (_session is null)
            {
                if (_autoBackend.HasValue && string.IsNullOrEmpty(_sessionOptions.BackendDirectory))
                {
                    string dir = await LlamaRuntimeInstaller.EnsureInstalledAsync(
                        _autoBackend.Value,
                        cudaVersion: _autoCudaVersion,
                        releaseTag: _autoReleaseTag,
                        installRoot: _autoInstallRoot,
                        progress: _installProgress,
                        ct: ct);

                    _sessionOptions = _sessionOptions with { BackendDirectory = dir };
                }

                _session = await Mantle.LmSession.CreateAsync(_sessionOptions, ct);
            }

            return _session;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private Mantle.ResponseRequest CreateRequest(
        IEnumerable<ResponseInput> input,
        string? instructions,
        string? previousResponseId,
        InferenceConfig? inference,
        List<ToolDefinition>? tools,
        ReasoningEffort? reasoning,
        string? model,
        bool stream)
    {
        ValidateUnsupportedFeatures(input);
        var hasVisionInput = ContainsVisionInput(input);
        SynchronizeLocalTools(tools);

        return new Mantle.ResponseRequest
        {
            Model = ResolveModel(model),
            Input = ConvertInput(input),
            Instructions = instructions,
            Tools = CreateToolDefinitions(tools),
            PreviousResponseId = previousResponseId,
            MaxOutputTokens = null,
            Stream = stream,
            Temperature = inference?.Temperature is double temperature ? (float)temperature : null,
            TopP = inference?.TopP is double topP ? (float)topP : null,
            TopK = inference?.TopK,
            PresencePenalty = inference?.PresencePenalty is double presencePenalty ? (float)presencePenalty : null,
            RepetitionPenalty = inference?.RepetitionPenalty is double repetitionPenalty ? (float)repetitionPenalty : null,
            AddVisionId = hasVisionInput,
            EnableThinking = reasoning switch
            {
                null => null,
                ReasoningEffort.None => false,
                _ => true,
            },
            ReasoningEffort = reasoning switch
            {
                null => null,
                ReasoningEffort.None => "none",
                _ => reasoning.Value.ToString().ToLowerInvariant(),
            }
        };
    }

    private static void ValidateUnsupportedFeatures(IEnumerable<ResponseInput> input)
    {
        foreach (var item in input)
            if (item.Content is IEnumerable<ResponseInputContent> parts)
                foreach (var image in parts.OfType<InputImageContent>())
                    if (string.IsNullOrWhiteSpace(image.ImageUrl))
                        throw new NotSupportedException("NativeBackend image inputs must provide a non-empty image URL or data URL.");
    }

    private static bool ContainsVisionInput(IEnumerable<ResponseInput> input) =>
        input.Any(item => item.Content is IEnumerable<ResponseInputContent> parts && parts.OfType<InputImageContent>().Any());

    private void SynchronizeLocalTools(List<ToolDefinition>? tools)
    {
        var nextLocalNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tool in tools?.Where(tool => string.Equals(tool.Type, "function", StringComparison.OrdinalIgnoreCase)
                                               && !string.IsNullOrWhiteSpace(tool.Name)
                                               && tool.LocalInvoker is not null)
                              ?? [])
        {
            nextLocalNames.Add(tool.Name!);
            _sessionOptions.ToolRegistry.Register(new Mantle.AgentTool(
                tool.Name!,
                tool.Description ?? string.Empty,
                CreateNativeParameters(tool.Parameters),
                args => InvokeLocalTool(tool, args)));
        }

        foreach (var staleTool in _projectedLocalToolNames.Except(nextLocalNames).ToList())
            _sessionOptions.ToolRegistry.Remove(staleTool);

        _projectedLocalToolNames.Clear();
        foreach (var name in nextLocalNames)
            _projectedLocalToolNames.Add(name);
    }

    private static IReadOnlyList<Mantle.ResponseToolDefinition>? CreateToolDefinitions(List<ToolDefinition>? tools)
    {
        if (tools is not { Count: > 0 })
            return null;

        return [.. tools.Select(tool => new Mantle.ResponseToolDefinition(
            Type: tool.Type,
            Name: tool.Name ?? string.Empty,
            Parameters: tool.Parameters ?? ToolSchema.Parse("""{"type":"object"}"""),
            Description: tool.Description,
            ServerLabel: tool.ServerLabel,
            ServerUrl: tool.ServerUrl,
            AllowedTools: tool.AllowedTools,
            Headers: tool.Headers))];
    }

    private static IReadOnlyList<Mantle.ToolParameter> CreateNativeParameters(JsonElement? schema)
    {
        if (schema is not { ValueKind: JsonValueKind.Object } schemaObject)
            return [];

        var required = schemaObject.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array
            ? requiredElement.EnumerateArray().Select(item => item.GetString()).OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        if (!schemaObject.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return [];

        var parameters = new List<Mantle.ToolParameter>();
        foreach (var property in properties.EnumerateObject())
        {
            var definition = property.Value;
            var type = definition.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? "string" : "string";
            var description = definition.TryGetProperty("description", out var descriptionElement) ? descriptionElement.GetString() ?? string.Empty : string.Empty;
            parameters.Add(new Mantle.ToolParameter(property.Name, type, description, required.Contains(property.Name)));
        }
        return parameters;
    }

    private static string InvokeLocalTool(ToolDefinition tool, IReadOnlyDictionary<string, object?> args)
    {
        if (tool.LocalInvoker is null)
            return $"Error: tool '{tool.Name}' does not have a local handler.";

        var arguments = ToolSchema.SerializeToElement(args);
        return tool.LocalInvoker(arguments, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static IReadOnlyList<Mantle.ResponseItem> ConvertInput(IEnumerable<ResponseInput> input)
    {
        var items = new List<Mantle.ResponseItem>();

        foreach (var message in input)
        {
            items.Add(new Mantle.ResponseMessageItem(
                Id: Mantle.SessionIds.Create("item"),
                Role: message.Role,
                Content: ConvertMessageContent(message.Content)));
        }

        return items;
    }

    private static IReadOnlyList<Mantle.ResponseTextContent> ConvertMessageContent(object? content)
    {
        return content switch
        {
            string text => [new Mantle.ResponseTextContent("input_text", text)],
            IEnumerable<ResponseInputContent> parts => ConvertMessageContent(parts),
            _ => [new Mantle.ResponseTextContent("input_text", content?.ToString() ?? string.Empty)],
        };
    }

    private static IReadOnlyList<Mantle.ResponseTextContent> ConvertMessageContent(IEnumerable<ResponseInputContent> parts)
    {
        var content = new List<Mantle.ResponseTextContent>();

        foreach (var part in parts)
        {
            switch (part)
            {
                case InputTextContent text:
                    content.Add(new Mantle.ResponseTextContent("input_text", text.Text));
                    break;
                case InputImageContent image when !string.IsNullOrWhiteSpace(image.ImageUrl):
                    content.Add(new Mantle.ResponseTextContent("input_image", image.ImageUrl));
                    break;
            }
        }

        return content.Count > 0
            ? content
            : [new Mantle.ResponseTextContent("input_text", string.Empty)];
    }

    private ResponseResponse MapResponse(Mantle.ResponseObject response)
    {
        return new ResponseResponse
        {
            Id = response.Id,
            Object = response.Object,
            Status = response.Status.ToString().ToLowerInvariant(),
            Output = [.. response.Output.Select(MapOutputItem)],
            ResponseId = response.Id,
            Usage = new ResponseUsage
            {
                InputTokens = response.Usage.InputTokens,
                OutputTokens = response.Usage.OutputTokens,
                TotalTokens = response.Usage.TotalTokens,
            }
        };
    }

    private static ResponseOutputItem MapOutputItem(Mantle.ResponseItem item)
    {
        return item switch
        {
            Mantle.ResponseMessageItem message => new ResponseOutputItem
            {
                Type = "message",
                Id = message.Id,
                Role = message.Role,
                Content = [.. message.Content.Select(content => new ResponseOutputContent
                {
                    Type = content.Type,
                    Text = content.Text,
                })],
            },
            Mantle.ResponseFunctionCallItem functionCall => new ResponseOutputItem
            {
                Type = "function_call",
                Id = functionCall.Id,
                Name = functionCall.Name,
                CallId = functionCall.CallId,
                Arguments = functionCall.Arguments,
            },
            Mantle.ResponseFunctionCallOutputItem functionOutput => new ResponseOutputItem
            {
                Type = "function_call_output",
                Id = functionOutput.Id,
                CallId = functionOutput.CallId,
                Output = functionOutput.Output,
            },
            _ => throw new InvalidOperationException($"Unsupported native response item type '{item.Type}'.")
        };
    }

    private string ResolveModel(string? alias) => string.IsNullOrWhiteSpace(alias) ? _defaultModel : alias;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
