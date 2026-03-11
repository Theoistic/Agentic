using System.Runtime.CompilerServices;
using System.Text.Json;
using Mantle = Agentic.Runtime.Mantle;

namespace Agentic;

/// <summary>
/// Backend implementation that runs inference through <see cref="Mantle.LmSession"/>.
/// </summary>
public sealed class NativeBackend : ILLMBackend, IAsyncDisposable, IDisposable
{
    private readonly Mantle.LmSessionOptions _sessionOptions;
    private readonly string _defaultModel;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly HashSet<string> _projectedLocalToolNames = new(StringComparer.Ordinal);

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
        var session = await EnsureSessionAsync(ct);
        var request = CreateRequest(input, instructions, previousResponseId, inference, tools, reasoning, model, stream: false);
        var response = await session.CreateResponseAsync(request, ct);
        return MapResponse(response);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var session = await EnsureSessionAsync(ct);
        return await session.EmbedAsync(input, ct);
    }

    public async Task<List<float[]>> EmbedBatchAsync(IEnumerable<string> inputs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var vectors = new List<float[]>();
        foreach (var input in inputs)
            vectors.Add(await EmbedAsync(input, ct));
        return vectors;
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
            _session ??= await Mantle.LmSession.CreateAsync(_sessionOptions, ct);
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
        SynchronizeLocalTools(tools);

        return new Mantle.ResponseRequest
        {
            Model = ResolveModel(model),
            Input = ConvertInput(input),
            Instructions = instructions,
            Tools = CreateToolDefinitions(tools),
            PreviousResponseId = previousResponseId,
            Stream = stream,
            Temperature = inference?.Temperature is double temperature ? (float)temperature : null,
            TopP = inference?.TopP is double topP ? (float)topP : null,
            TopK = inference?.TopK,
            PresencePenalty = inference?.PresencePenalty is double presencePenalty ? (float)presencePenalty : null,
            RepetitionPenalty = inference?.RepetitionPenalty is double repetitionPenalty ? (float)repetitionPenalty : null,
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
        {
            if (item.Content is IEnumerable<ResponseInputContent> parts && parts.OfType<InputImageContent>().Any())
                throw new NotSupportedException("NativeBackend does not support image inputs through the response adapter.");
        }
    }

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
            ? requiredElement.EnumerateArray().Select(item => item.GetString()).Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.Ordinal)
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
            string text = message.Content switch
            {
                string s => s,
                IEnumerable<ResponseInputContent> parts => string.Concat(parts.OfType<InputTextContent>().Select(part => part.Text)),
                _ => message.Content?.ToString() ?? string.Empty,
            };

            items.Add(new Mantle.ResponseMessageItem(
                Id: Mantle.SessionIds.Create("item"),
                Role: message.Role,
                Content: [new Mantle.ResponseTextContent("input_text", text)]));
        }

        return items;
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
