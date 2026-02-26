namespace Agentic.Mcp;

// ═══════════════════════════════════════════════════════════════════════════
//  Resource & Prompt registries
// ═══════════════════════════════════════════════════════════════════════════

public sealed class ResourceRegistry
{
    private readonly object _lock = new();
    private readonly List<ResourceEntry> _resources = [];
    private readonly List<ResourceTemplateEntry> _templates = [];

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

    public bool Remove(string uri) { lock (_lock) return _resources.RemoveAll(r => r.Uri == uri) > 0; }
    public bool RemoveTemplate(string uriTemplate) { lock (_lock) return _templates.RemoveAll(t => t.UriTemplate == uriTemplate) > 0; }

    public List<McpResourceInfo> ListResources()
    {
        lock (_lock) return _resources.Select(r => new McpResourceInfo
        { Uri = r.Uri, Name = r.Name, Description = r.Description, MimeType = r.MimeType }).ToList();
    }

    public List<McpResourceTemplate> ListTemplates()
    {
        lock (_lock) return _templates.Select(t => new McpResourceTemplate
        { UriTemplate = t.UriTemplate, Name = t.Name, Description = t.Description, MimeType = t.MimeType }).ToList();
    }

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

    public bool HasAny { get { lock (_lock) return _resources.Count > 0 || _templates.Count > 0; } }
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

public sealed class PromptRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, PromptEntry> _prompts = new(StringComparer.Ordinal);

    public PromptRegistry Add(string name, Func<Dictionary<string, string>?, List<McpPromptMessage>> builder,
        string? description = null, List<McpPromptArgument>? arguments = null)
    {
        lock (_lock) _prompts[name] = new() { Name = name, Description = description, Arguments = arguments, Builder = builder };
        return this;
    }

    public bool Remove(string name) { lock (_lock) return _prompts.Remove(name); }

    public List<McpPromptInfo> ListPrompts()
    {
        lock (_lock) return _prompts.Values.Select(p => new McpPromptInfo
        { Name = p.Name, Description = p.Description, Arguments = p.Arguments }).ToList();
    }

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

    public bool HasAny { get { lock (_lock) return _prompts.Count > 0; } }
    public int Count { get { lock (_lock) return _prompts.Count; } }

    private sealed class PromptEntry
    {
        public required string Name { get; init; }
        public string? Description { get; init; }
        public List<McpPromptArgument>? Arguments { get; init; }
        public required Func<Dictionary<string, string>?, List<McpPromptMessage>> Builder { get; init; }
    }
}
