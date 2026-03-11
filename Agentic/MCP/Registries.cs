namespace Agentic.Mcp;

// ═══════════════════════════════════════════════════════════════════════════
//  Resource & Prompt registries
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Thread-safe registry of MCP resources and URI-template-based resource templates
/// that the <see cref="McpRequestHandler"/> exposes to clients.
/// </summary>
public sealed class ResourceRegistry
{
    private readonly object _lock = new();
    private readonly List<ResourceEntry> _resources = [];
    private readonly List<ResourceTemplateEntry> _templates = [];

    /// <summary>Registers a static text resource reachable at <paramref name="uri"/>.</summary>
    /// <param name="uri">Unique resource URI.</param>
    /// <param name="name">Human-readable name.</param>
    /// <param name="reader">Async factory that produces the resource text.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="mimeType">MIME type; defaults to <c>text/plain</c>.</param>
    public ResourceRegistry Add(string uri, string name, Func<Task<string>> reader,
        string? description = null, string? mimeType = "text/plain")
    {
        lock (_lock) _resources.Add(new()
        {
            Uri = uri, Name = name, Description = description, MimeType = mimeType,
            ContentFactory = async u => new McpResourceContent
            { Uri = u, MimeType = mimeType, Text = await reader() },
        });
        return this;
    }

    /// <summary>Registers a static binary resource reachable at <paramref name="uri"/>.</summary>
    /// <param name="uri">Unique resource URI.</param>
    /// <param name="name">Human-readable name.</param>
    /// <param name="reader">Async factory that produces the raw bytes.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="mimeType">MIME type; defaults to <c>application/octet-stream</c>.</param>
    public ResourceRegistry AddBlob(string uri, string name, Func<Task<byte[]>> reader,
        string? description = null, string? mimeType = "application/octet-stream")
    {
        lock (_lock) _resources.Add(new()
        {
            Uri = uri, Name = name, Description = description, MimeType = mimeType,
            ContentFactory = async u => new McpResourceContent
            { Uri = u, MimeType = mimeType, Blob = Convert.ToBase64String(await reader()) },
        });
        return this;
    }

    /// <summary>Registers a text resource template that matches URIs via RFC 6570 URI templates.</summary>
    /// <param name="uriTemplate">URI template (e.g. <c>file:///{path}</c>).</param>
    /// <param name="name">Human-readable name.</param>
    /// <param name="reader">Async factory receiving extracted template variables and returning text content.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="mimeType">MIME type; defaults to <c>text/plain</c>.</param>
    public ResourceRegistry AddTemplate(string uriTemplate, string name,
        Func<Dictionary<string, string>, Task<string>> reader,
        string? description = null, string? mimeType = "text/plain")
    {
        lock (_lock) _templates.Add(new()
        {
            UriTemplate = uriTemplate, Name = name, Description = description, MimeType = mimeType,
            ContentFactory = async (u, args) => new McpResourceContent
            { Uri = u, MimeType = mimeType, Text = await reader(args) },
        });
        return this;
    }

    /// <summary>Registers a binary resource template that matches URIs via RFC 6570 URI templates.</summary>
    /// <param name="uriTemplate">URI template.</param>
    /// <param name="name">Human-readable name.</param>
    /// <param name="reader">Async factory receiving extracted template variables and returning raw bytes.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="mimeType">MIME type; defaults to <c>application/octet-stream</c>.</param>
    public ResourceRegistry AddBlobTemplate(string uriTemplate, string name,
        Func<Dictionary<string, string>, Task<byte[]>> reader,
        string? description = null, string? mimeType = "application/octet-stream")
    {
        lock (_lock) _templates.Add(new()
        {
            UriTemplate = uriTemplate, Name = name, Description = description, MimeType = mimeType,
            ContentFactory = async (u, args) => new McpResourceContent
            { Uri = u, MimeType = mimeType, Blob = Convert.ToBase64String(await reader(args)) },
        });
        return this;
    }

    /// <summary>Removes the resource registered at <paramref name="uri"/>. Returns <c>true</c> if found.</summary>
    public bool Remove(string uri) { lock (_lock) return _resources.RemoveAll(r => r.Uri == uri) > 0; }
    /// <summary>Removes the resource template registered for <paramref name="uriTemplate"/>. Returns <c>true</c> if found.</summary>
    public bool RemoveTemplate(string uriTemplate) { lock (_lock) return _templates.RemoveAll(t => t.UriTemplate == uriTemplate) > 0; }

    /// <summary>Returns metadata for all registered static resources.</summary>
    public List<McpResourceInfo> ListResources()
    {
        lock (_lock) return _resources.Select(r => new McpResourceInfo
        { Uri = r.Uri, Name = r.Name, Description = r.Description, MimeType = r.MimeType }).ToList();
    }

    /// <summary>Returns metadata for all registered resource templates.</summary>
    public List<McpResourceTemplate> ListTemplates()
    {
        lock (_lock) return _templates.Select(t => new McpResourceTemplate
        { UriTemplate = t.UriTemplate, Name = t.Name, Description = t.Description, MimeType = t.MimeType }).ToList();
    }

    /// <summary>Reads the content of the resource identified by <paramref name="uri"/>, matching against templates if needed.</summary>
    /// <exception cref="KeyNotFoundException">Thrown when no resource or template matches the URI.</exception>
    public async Task<ResourceReadResult> ReadAsync(string uri)
    {
        ResourceEntry? res;
        lock (_lock) res = _resources.FirstOrDefault(r => r.Uri == uri);
        if (res is not null)
            return new() { Contents = [await res.ContentFactory(uri)] };

        List<ResourceTemplateEntry> templates;
        lock (_lock) templates = [.. _templates];
        foreach (var t in templates)
        {
            if (TryMatchTemplate(t.UriTemplate, uri, out var args))
                return new() { Contents = [await t.ContentFactory(uri, args)] };
        }
        throw new KeyNotFoundException($"Resource not found: {uri}");
    }

    /// <summary><c>true</c> when at least one resource or template is registered.</summary>
    public bool HasAny { get { lock (_lock) return _resources.Count > 0 || _templates.Count > 0; } }
    /// <summary>Total number of registered resources and templates.</summary>
    public int Count { get { lock (_lock) return _resources.Count + _templates.Count; } }

    internal static bool TryMatchTemplate(string template, string uri, out Dictionary<string, string> args)
    {
        args = [];
        var segments = new List<(string? Literal, string? Param)>();
        var pos = 0;
        while (pos < template.Length)
        {
            var braceStart = template.IndexOf('{', pos);
            if (braceStart < 0) { segments.Add((template[pos..], null)); break; }
            if (braceStart > pos) segments.Add((template[pos..braceStart], null));
            var braceEnd = template.IndexOf('}', braceStart);
            if (braceEnd < 0) return false;
            segments.Add((null, template[(braceStart + 1)..braceEnd]));
            pos = braceEnd + 1;
        }

        var uriPos = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            var (literal, param) = segments[i];
            if (literal is not null)
            {
                if (!uri[uriPos..].StartsWith(literal, StringComparison.Ordinal)) return false;
                uriPos += literal.Length;
            }
            else if (param is not null)
            {
                int endPos;
                if (i + 1 < segments.Count && segments[i + 1].Literal is string nextLit)
                    endPos = uri.IndexOf(nextLit, uriPos, StringComparison.Ordinal);
                else
                    endPos = uri.Length;
                if (endPos < 0 || endPos == uriPos) return false;
                args[param] = uri[uriPos..endPos];
                uriPos = endPos;
            }
        }
        return uriPos == uri.Length && args.Count > 0;
    }

    private sealed class ResourceEntry
    {
        public required string Uri { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public string? MimeType { get; init; }
        public required Func<string, Task<McpResourceContent>> ContentFactory { get; init; }
    }

    private sealed class ResourceTemplateEntry
    {
        public required string UriTemplate { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public string? MimeType { get; init; }
        public required Func<string, Dictionary<string, string>, Task<McpResourceContent>> ContentFactory { get; init; }
    }
}

/// <summary>
/// Thread-safe registry of reusable prompt templates exposed by the MCP server
/// via the <c>prompts/list</c> and <c>prompts/get</c> methods.
/// </summary>
public sealed class PromptRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, PromptEntry> _prompts = new(StringComparer.Ordinal);

    /// <summary>Registers a prompt template.</summary>
    /// <param name="name">Unique prompt name.</param>
    /// <param name="builder">Factory that receives the caller's arguments and returns the prompt messages.</param>
    /// <param name="description">Optional human-readable description.</param>
    /// <param name="arguments">Optional argument descriptors for the prompt.</param>
    public PromptRegistry Add(string name, Func<Dictionary<string, string>?, List<McpPromptMessage>> builder,
        string? description = null, List<McpPromptArgument>? arguments = null)
    {
        lock (_lock) _prompts[name] = new() { Name = name, Description = description, Arguments = arguments, Builder = builder };
        return this;
    }

    /// <summary>Removes the prompt registered under <paramref name="name"/>. Returns <c>true</c> if found.</summary>
    public bool Remove(string name) { lock (_lock) return _prompts.Remove(name); }

    /// <summary>Returns metadata for all registered prompts.</summary>
    public List<McpPromptInfo> ListPrompts()
    {
        lock (_lock) return _prompts.Values.Select(p => new McpPromptInfo
        { Name = p.Name, Description = p.Description, Arguments = p.Arguments }).ToList();
    }

    /// <summary>Retrieves and renders the named prompt with the supplied arguments.</summary>
    /// <exception cref="KeyNotFoundException">Thrown when <paramref name="name"/> is not registered.</exception>
    public PromptGetResult Get(string name, Dictionary<string, string>? arguments)
    {
        PromptEntry entry;
        lock (_lock)
        {
            if (!_prompts.TryGetValue(name, out var e))
                throw new KeyNotFoundException($"Prompt not found: {name}");
            entry = e;
        }
        return new() { Description = entry.Description, Messages = entry.Builder(arguments) };
    }

    /// <summary><c>true</c> when at least one prompt is registered.</summary>
    public bool HasAny { get { lock (_lock) return _prompts.Count > 0; } }
    /// <summary>Total number of registered prompts.</summary>
    public int Count { get { lock (_lock) return _prompts.Count; } }

    private sealed class PromptEntry
    {
        public required string Name { get; init; }
        public string? Description { get; init; }
        public List<McpPromptArgument>? Arguments { get; init; }
        public required Func<Dictionary<string, string>?, List<McpPromptMessage>> Builder { get; init; }
    }
}
