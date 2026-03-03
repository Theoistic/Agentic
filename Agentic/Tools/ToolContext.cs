namespace Agentic;

/// <summary>
/// Execution context available to tool methods during invocation.
/// Carries HTTP headers forwarded from the MCP request, plus arbitrary
/// key-value properties set by the calling agent or workflow.
/// <para>
/// Declare a <see cref="ToolContext"/> parameter on any <c>[Tool]</c> method
/// and the framework will inject it automatically — no <c>IHttpContextAccessor</c> needed.
/// </para>
/// </summary>
public sealed class ToolContext
{
    /// <summary>HTTP headers forwarded from the inbound MCP request (case-insensitive keys).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Arbitrary key-value properties set by the caller (e.g. workflow scope data).</summary>
    public IReadOnlyDictionary<string, object?> Properties { get; }

    /// <summary>Creates a new tool context.</summary>
    /// <param name="headers">HTTP headers; <c>null</c> = empty.</param>
    /// <param name="properties">Arbitrary properties; <c>null</c> = empty.</param>
    public ToolContext(
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Properties = properties ?? new Dictionary<string, object?>();
    }

    /// <summary>Gets a header value by name, or <c>null</c> if not present.</summary>
    public string? GetHeader(string name) =>
        Headers.TryGetValue(name, out var v) ? v : null;

    /// <summary>Gets a typed property value by key, or <c>default</c> if absent or wrong type.</summary>
    public T? Get<T>(string key) =>
        Properties.TryGetValue(key, out var v) && v is T t ? t : default;

    /// <summary>An empty context with no headers or properties.</summary>
    public static ToolContext Empty { get; } = new();
}
