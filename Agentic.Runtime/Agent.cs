using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Mantle = Agentic.Runtime.Mantle;

namespace Agentic.Runtime;

/// <summary>
/// Represents the current execution state of an <see cref="Agent"/>.
/// </summary>
public enum AgentTaskStatus
{
    Created,
    Ready,
    Running,
    Completed,
    Failed,
    Disposed
}

/// <summary>
/// Raised when an agent status changes.
/// </summary>
public sealed class AgentStatusChangedEventArgs : EventArgs
{
    public AgentStatusChangedEventArgs(AgentTaskStatus previousStatus, AgentTaskStatus currentStatus)
    {
        PreviousStatus = previousStatus;
        CurrentStatus = currentStatus;
    }

    public AgentTaskStatus PreviousStatus { get; }
    public AgentTaskStatus CurrentStatus { get; }
}

/// <summary>
/// Raised when the current agent objective changes.
/// </summary>
public sealed class AgentObjectiveChangedEventArgs : EventArgs
{
    public AgentObjectiveChangedEventArgs(string? previousObjective, string? currentObjective)
    {
        PreviousObjective = previousObjective;
        CurrentObjective = currentObjective;
    }

    public string? PreviousObjective { get; }
    public string? CurrentObjective { get; }
}

/// <summary>
/// Fluent façade over the Mantle session runtime for configuring and running a purpose-driven agent.
/// </summary>
public sealed class Agent : IAsyncDisposable, IDisposable
{
    private readonly Mantle.ToolRegistry _tools = new();
    private readonly Dictionary<string, object?> _context = new(StringComparer.Ordinal);

    private Mantle.LmSession? _session;
    private Mantle.ResponseObject? _lastResponse;
    private Mantle.ResponseRequest _defaultRequest = new();
    private Mantle.ConversationCompactionOptions _compaction = new(2048, ReservedForGeneration: 256);
    private Mantle.IConversationCompactor? _conversationCompactor;
    private Mantle.IToolExecutionEngine? _toolExecutionEngine;
    private Mantle.ILogger _logger = Mantle.NullLogger.Instance;
    private Func<Mantle.LmSessionOptions, Mantle.LmSessionOptions> _sessionOptionsTransform = static options => options;

    private string? _name;
    private string? _purpose;
    private string? _workflow;
    private string? _currentObjective;
    private string? _backendDirectory;
    private string? _modelPath;
    private string? _modelId;
    private string? _previousResponseId;
    private AgentTaskStatus _status = AgentTaskStatus.Created;
    private bool _disposed;

    // Auto-backend resolution
    private Core.LlamaBackend? _autoBackend;
    private string? _autoCudaVersion;
    private string? _autoInstallRoot;
    private IProgress<(string message, double percent)>? _installProgress;

    /// <summary>
    /// Raised whenever the agent status changes.
    /// </summary>
    public event EventHandler<AgentStatusChangedEventArgs>? StatusChanged;

    /// <summary>
    /// Raised whenever the current objective changes.
    /// </summary>
    public event EventHandler<AgentObjectiveChangedEventArgs>? ObjectiveChanged;

    /// <summary>
    /// Raised whenever the underlying session prepares a prompt debug snapshot.
    /// </summary>
    public event EventHandler<Mantle.SessionDebugView>? DebugViewCreated;

    /// <summary>
    /// Gets the configured display name for the agent.
    /// </summary>
    public string Name => string.IsNullOrWhiteSpace(_name) ? nameof(Agent) : _name;

    /// <summary>
    /// Gets the configured high-level purpose of the agent.
    /// </summary>
    public string? Purpose => _purpose;

    /// <summary>
    /// Gets the configured workflow instructions.
    /// </summary>
    public string? Workflow => _workflow;

    /// <summary>
    /// Gets the current objective for the agent.
    /// </summary>
    public string? CurrentObjective => _currentObjective;

    /// <summary>
    /// Gets the current agent status.
    /// </summary>
    public AgentTaskStatus Status => _status;

    /// <summary>
    /// Gets the current session history.
    /// </summary>
    public IReadOnlyList<Mantle.ChatMessage> History => _session?.History ?? [];

    /// <summary>
    /// Gets the current contextual values that will be injected into instructions.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Context => _context;

    /// <summary>
    /// Gets the registered tools.
    /// </summary>
    public Mantle.ToolRegistry Tools => _tools;

    /// <summary>
    /// Gets the most recent response produced by the agent.
    /// </summary>
    public Mantle.ResponseObject? LastResponse => _lastResponse;

    /// <summary>
    /// Configures the agent display name.
    /// </summary>
    public Agent Named(string name)
    {
        _name = name;
        return this;
    }

    /// <summary>
    /// Configures the primary purpose of the agent.
    /// </summary>
    public Agent ForPurpose(string purpose)
    {
        _purpose = purpose;
        return this;
    }

    /// <summary>
    /// Configures the workflow as a single free-form instruction block.
    /// </summary>
    public Agent WithWorkflow(string workflow)
    {
        _workflow = workflow;
        return this;
    }

    /// <summary>
    /// Configures the workflow as an ordered list of steps.
    /// </summary>
    public Agent WithWorkflow(params string[] steps)
    {
        _workflow = string.Join(Environment.NewLine, steps.Where(step => !string.IsNullOrWhiteSpace(step)).Select((step, index) => $"{index + 1}. {step}"));
        return this;
    }

    /// <summary>
    /// Updates the agent's current objective.
    /// </summary>
    public Agent WithObjective(string? objective)
    {
        ThrowIfDisposed();

        if (string.Equals(_currentObjective, objective, StringComparison.Ordinal))
            return this;

        string? previousObjective = _currentObjective;
        _currentObjective = objective;
        ObjectiveChanged?.Invoke(this, new AgentObjectiveChangedEventArgs(previousObjective, _currentObjective));
        return this;
    }

    /// <summary>
    /// Clears the current objective.
    /// </summary>
    public Agent ClearObjective() => WithObjective(null);

    /// <summary>
    /// Configures the model backend and model path used by the session.
    /// </summary>
    public Agent UseModel(string backendDirectory, string modelPath, string? modelId = null)
    {
        _backendDirectory = backendDirectory;
        _modelPath = modelPath;
        _modelId = modelId;
        _autoBackend = null;
        _autoCudaVersion = null;
        _autoInstallRoot = null;
        InvalidateSession();
        return this;
    }

    /// <summary>
    /// Configures the model path and instructs the agent to automatically download and install
    /// the llama.cpp runtime from the latest GitHub release when no local installation is found.
    /// </summary>
    /// <param name="modelPath">Path to the GGUF model file.</param>
    /// <param name="backend">The accelerator backend to use.</param>
    /// <param name="cudaVersion">
    /// Preferred CUDA version, e.g. <c>"12.4"</c>. When <see langword="null"/> the
    /// highest available CUDA 12.x asset is chosen automatically.
    /// </param>
    /// <param name="installRoot">Override the default runtime install root directory.</param>
    /// <param name="modelId">Optional model identifier override.</param>
    public Agent UseModel(string modelPath, Core.LlamaBackend backend, string? cudaVersion = null, string? installRoot = null, string? modelId = null)
    {
        _backendDirectory = null;
        _modelPath = modelPath;
        _modelId = modelId;
        _autoBackend = backend;
        _autoCudaVersion = cudaVersion;
        _autoInstallRoot = installRoot;
        InvalidateSession();
        return this;
    }

    /// <summary>
    /// Configures a progress handler that receives status updates while the llama.cpp
    /// runtime is being downloaded and installed.
    /// </summary>
    public Agent WithInstallProgress(IProgress<(string message, double percent)>? progress)
    {
        _installProgress = progress;
        return this;
    }

    /// <summary>
    /// Replaces the logger used by the underlying session.
    /// </summary>
    public Agent WithLogger(Mantle.ILogger? logger)
    {
        _logger = logger ?? Mantle.NullLogger.Instance;
        InvalidateSession();
        return this;
    }

    /// <summary>
    /// Replaces the inference settings used by the session.
    /// </summary>
    public Agent WithInference(Mantle.ResponseRequest options)
    {
        _defaultRequest = options;
        InvalidateSession();
        return this;
    }

    /// <summary>
    /// Replaces the conversation compaction settings used by the session.
    /// </summary>
    public Agent WithCompaction(Mantle.ConversationCompactionOptions options)
    {
        _compaction = options;
        InvalidateSession();
        return this;
    }

    /// <summary>
    /// Replaces the compaction implementation used by the session.
    /// </summary>
    public Agent WithConversationCompactor(Mantle.IConversationCompactor? compactor)
    {
        _conversationCompactor = compactor;
        InvalidateSession();
        return this;
    }

    /// <summary>
    /// Replaces the tool execution engine used by the session.
    /// </summary>
    public Agent WithToolExecutionEngine(Mantle.IToolExecutionEngine? toolExecutionEngine)
    {
        _toolExecutionEngine = toolExecutionEngine;
        InvalidateSession();
        return this;
    }

    /// <summary>
    /// Applies a transformation to the low-level <see cref="Mantle.LmSessionOptions"/> used when creating the session.
    /// </summary>
    public Agent WithSessionOptions(Func<Mantle.LmSessionOptions, Mantle.LmSessionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var previous = _sessionOptionsTransform;
        _sessionOptionsTransform = options => configure(previous(options));
        InvalidateSession();
        return this;
    }

    /// <summary>
    /// Configures how images are retained across turns.
    /// </summary>
    public Agent WithImageRetention(Mantle.ImageRetentionPolicy policy)
    {
        return WithSessionOptions(options => options with { ImageRetentionPolicy = policy });
    }

    /// <summary>
    /// Registers a tool with the agent.
    /// </summary>
    public Agent AddTool(Mantle.AgentTool tool)
    {
        _tools.Register(tool);
        InvalidateSession();
        return this;
    }

    /// <summary>
    /// Adds or updates a contextual value injected into the agent instructions.
    /// </summary>
    public Agent WithContext(string key, object? value)
    {
        _context[key] = value;
        return this;
    }

    /// <summary>
    /// Removes a contextual value.
    /// </summary>
    public Agent WithoutContext(string key)
    {
        _context.Remove(key);
        return this;
    }

    /// <summary>
    /// Clears all contextual values.
    /// </summary>
    public Agent ClearContext()
    {
        _context.Clear();
        return this;
    }

    /// <summary>
    /// Returns a copy of the current context values.
    /// </summary>
    public IReadOnlyDictionary<string, object?> GetContextSnapshot() => new Dictionary<string, object?>(_context, StringComparer.Ordinal);

    /// <summary>
    /// Creates the underlying session if it has not already been initialized.
    /// </summary>
    public async Task<Agent> InitializeAsync(CancellationToken ct = default)
    {
        await EnsureSessionAsync(ct);
        return this;
    }

    /// <summary>
    /// Builds the exact response-style request that will be sent for the provided input.
    /// </summary>
    public Mantle.ResponseRequest PrepareRequest(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        return _defaultRequest with
        {
            Model = ResolveModelId(),
            Input =
            [
                new Mantle.ResponseMessageItem(
                    Id: Mantle.SessionIds.Create("item"),
                    Role: "user",
                    Content: [new Mantle.ResponseTextContent("input_text", input)])
            ],
            Instructions = BuildInstructions(),
            Tools = CreateResponseToolDefinitions(),
            PreviousResponseId = _previousResponseId,
            Stream = false
        };
    }

    /// <summary>
    /// Runs the agent against a user input and returns the full response object.
    /// </summary>
    public async Task<Mantle.ResponseObject> RunAsync(string input, CancellationToken ct = default)
        => await RunAsync(PrepareRequest(input), ct);

    /// <summary>
    /// Runs the agent using a prepared response-style request.
    /// </summary>
    public async Task<Mantle.ResponseObject> RunAsync(Mantle.ResponseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await EnsureSessionAsync(ct);

        SetStatus(AgentTaskStatus.Running);

        try
        {
            var response = await _session!.CreateResponseAsync(request, ct);

            _previousResponseId = response.Id;
            _lastResponse = response;
            SetStatus(AgentTaskStatus.Completed);
            return response;
        }
        catch
        {
            SetStatus(AgentTaskStatus.Failed);
            throw;
        }
    }

    /// <summary>
    /// Streams the agent response for the provided input.
    /// </summary>
    public async IAsyncEnumerable<Mantle.ChatResponseChunk> GenerateAsync(
        string input,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var chunk in GenerateAsync(PrepareRequest(input), ct).ConfigureAwait(false))
            yield return chunk;
    }

    /// <summary>
    /// Streams the agent response for a prepared response-style request.
    /// </summary>
    public async IAsyncEnumerable<Mantle.ChatResponseChunk> GenerateAsync(
        Mantle.ResponseRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await EnsureSessionAsync(ct);

        SetStatus(AgentTaskStatus.Running);

        await using var enumerator = _session!.GenerateResponseAsync(request, ct).GetAsyncEnumerator(ct);
        bool completed = false;

        try
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                yield return enumerator.Current;

            _lastResponse = _session.LastResponse;
            _previousResponseId = _lastResponse?.Id;
            completed = true;
            SetStatus(AgentTaskStatus.Completed);
        }
        finally
        {
            if (!completed && !_disposed)
                SetStatus(AgentTaskStatus.Failed);
        }
    }

    /// <summary>
    /// Runs the agent and returns the concatenated assistant text output.
    /// </summary>
    public async Task<string> AskAsync(string input, CancellationToken ct = default)
    {
        var response = await RunAsync(input, ct);
        return string.Concat(response.Output
            .OfType<Mantle.ResponseMessageItem>()
            .Where(item => item.Role == "assistant")
            .SelectMany(item => item.Content)
            .Select(content => content.Text));
    }

    /// <summary>
    /// Resets the active conversation while preserving configuration, tools, and context.
    /// </summary>
    public Agent ResetConversation()
    {
        ThrowIfDisposed();

        _session?.Reset();
        _previousResponseId = null;
        _lastResponse = null;
        SetStatus(_session is null ? AgentTaskStatus.Created : AgentTaskStatus.Ready);
        return this;
    }

    /// <summary>
    /// Disposes the underlying session asynchronously.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        if (_session is not null)
        {
            _session.DebugViewCreated -= HandleSessionDebugViewCreated;
            await _session.DisposeAsync();
        }

        _session = null;
        _disposed = true;
        SetStatus(AgentTaskStatus.Disposed);
    }

    /// <summary>
    /// Disposes the underlying session synchronously.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_session is not null)
            return;

        if (_autoBackend.HasValue && string.IsNullOrWhiteSpace(_backendDirectory))
        {
            _backendDirectory = await Core.LlamaRuntimeInstaller.EnsureInstalledAsync(
                _autoBackend.Value,
                cudaVersion: _autoCudaVersion,
                installRoot: _autoInstallRoot,
                progress: _installProgress,
                ct: ct);
        }

        ValidateConfiguration();

        var sessionOptions = _sessionOptionsTransform(new Mantle.LmSessionOptions
        {
            BackendDirectory = _backendDirectory!,
            ModelPath = _modelPath!,
            ToolRegistry = _tools,
            Compaction = _compaction,
            DefaultRequest = _defaultRequest with { Tools = CreateResponseToolDefinitions() },
            ConversationCompactor = _conversationCompactor,
            ToolExecutionEngine = _toolExecutionEngine,
            Logger = _logger
        });

        _session = await Mantle.LmSession.CreateAsync(sessionOptions, ct);
        _session.DebugViewCreated += HandleSessionDebugViewCreated;

        SetStatus(AgentTaskStatus.Ready);
    }

    private void InvalidateSession()
    {
        if (_session is not null)
        {
            _session.DebugViewCreated -= HandleSessionDebugViewCreated;
            _session.Dispose();
        }
        _session = null;
        _previousResponseId = null;
        _lastResponse = null;

        if (!_disposed)
            SetStatus(AgentTaskStatus.Created);
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_backendDirectory))
            throw new InvalidOperationException("The agent backend directory has not been configured.");

        if (string.IsNullOrWhiteSpace(_modelPath))
            throw new InvalidOperationException("The agent model path has not been configured.");
    }

    private string ResolveModelId()
    {
        if (!string.IsNullOrWhiteSpace(_modelId))
            return _modelId;

        if (string.IsNullOrWhiteSpace(_modelPath))
            return "model";

        return Path.GetFileNameWithoutExtension(_modelPath) ?? _modelPath;
    }

    private void HandleSessionDebugViewCreated(object? sender, Mantle.SessionDebugView debugView)
        => DebugViewCreated?.Invoke(this, debugView);

    private IReadOnlyList<Mantle.ResponseToolDefinition> CreateResponseToolDefinitions()
    {
        return [..
            _tools.Select(tool => new Mantle.ResponseToolDefinition(
                Type: "function",
                Name: tool.Name,
                Parameters: new
                {
                    type = "object",
                    properties = tool.Parameters.ToDictionary(
                        parameter => parameter.Name,
                        parameter => (object)new { type = parameter.Type, description = parameter.Description },
                        StringComparer.Ordinal),
                    required = tool.Parameters.Where(parameter => parameter.Required).Select(parameter => parameter.Name).ToArray()
                },
                Description: tool.Description))];
    }

    private string? BuildInstructions()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"You are {Name}.");

        if (!string.IsNullOrWhiteSpace(_purpose))
        {
            sb.AppendLine();
            sb.AppendLine("Purpose:");
            sb.AppendLine(_purpose);
        }

        if (!string.IsNullOrWhiteSpace(_workflow))
        {
            sb.AppendLine();
            sb.AppendLine("Workflow:");
            sb.AppendLine(_workflow);
        }

        if (!string.IsNullOrWhiteSpace(_currentObjective))
        {
            sb.AppendLine();
            sb.AppendLine("Current objective:");
            sb.AppendLine(_currentObjective);
        }

        if (_context.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Context:");

            foreach (var entry in _context.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                sb.AppendLine($"- {entry.Key}: {entry.Value}");
        }

        string instructions = sb.ToString().Trim();
        return instructions.Length == 0 ? null : instructions;
    }

    private void SetStatus(AgentTaskStatus status)
    {
        if (_status == status)
            return;

        AgentTaskStatus previousStatus = _status;
        _status = status;
        StatusChanged?.Invoke(this, new AgentStatusChangedEventArgs(previousStatus, _status));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
