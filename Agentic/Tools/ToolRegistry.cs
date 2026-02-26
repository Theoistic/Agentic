using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  Tool attributes & interfaces
// ═══════════════════════════════════════════════════════════════════════════

[AttributeUsage(AttributeTargets.Method)]
public sealed class ToolAttribute(string? name = null) : Attribute { public string? Name { get; } = name; }

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ToolParamAttribute(string description) : Attribute { public string Description { get; } = description; }

public interface IAgentToolSet { }
public interface IDisposableToolSet : IAgentToolSet { ValueTask DisposeAsync(); }

// ═══════════════════════════════════════════════════════════════════════════
//  Tool registry
// ═══════════════════════════════════════════════════════════════════════════

public sealed class AgentToolDescriptor
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public JsonElement? ParameterSchema { get; init; }
}

public sealed class ToolRegistry
{
    private readonly List<IAgentToolSet> _sets = [];
    private readonly Dictionary<string, ReflectedTool> _tools = new(StringComparer.Ordinal);

    public IEnumerable<IAgentToolSet> ToolSets => _sets;
    public int Count => _tools.Count;

    public void Register<T>(T toolSet, bool replaceExisting = false) where T : IAgentToolSet
    {
        if (replaceExisting)
        {
            foreach (var k in _tools.Where(kv => kv.Value.Instance.GetType() == typeof(T)).Select(kv => kv.Key).ToList())
                _tools.Remove(k);
            _sets.RemoveAll(s => s.GetType() == typeof(T));
        }
        _sets.Add(toolSet);

        foreach (var m in typeof(T).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = m.GetCustomAttribute<ToolAttribute>();
            if (attr is null) continue;
            var name = attr.Name ?? ToSnakeCase(m.Name);
            _tools[name] = new()
            {
                Name = name,
                Description = m.GetCustomAttribute<DescriptionAttribute>()?.Description,
                ParameterSchema = BuildSchema(m.GetParameters()),
                Method = m, Instance = toolSet, Parameters = m.GetParameters(),
            };
        }
    }

    public IReadOnlyList<AgentToolDescriptor> GetAllDescriptors() =>
        _tools.Values.Select(t => new AgentToolDescriptor
        { Name = t.Name, Description = t.Description, ParameterSchema = t.ParameterSchema }).ToList();

    public async Task<string> InvokeAsync(string name, JsonElement? arguments, CancellationToken ct = default)
    {
        if (!_tools.TryGetValue(name, out var tool))
            throw new KeyNotFoundException($"Tool '{name}' is not registered.");
        var args = BindArguments(tool.Parameters, arguments, ct);
        var result = tool.Method.Invoke(tool.Instance, args);
        if (result is Task task)
        {
            await task;
            var tt = task.GetType();
            return tt.IsGenericType ? tt.GetProperty("Result")!.GetValue(task)?.ToString() ?? "" : "";
        }
        return result?.ToString() ?? "";
    }

    private sealed class ReflectedTool
    {
        public required string Name { get; init; }
        public string? Description { get; init; }
        public JsonElement? ParameterSchema { get; init; }
        public required MethodInfo Method { get; init; }
        public required object Instance { get; init; }
        public required ParameterInfo[] Parameters { get; init; }
    }

    private static object?[] BindArguments(ParameterInfo[] parameters, JsonElement? arguments, CancellationToken ct = default)
    {
        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.ParameterType == typeof(JsonElement?))       { args[i] = arguments; continue; }
            if (p.ParameterType == typeof(JsonElement))        { args[i] = arguments ?? default; continue; }
            if (p.ParameterType == typeof(CancellationToken))  { args[i] = ct; continue; }
            if (arguments.HasValue && arguments.Value.TryGetProperty(p.Name!, out var prop))
                args[i] = JsonSerializer.Deserialize(prop.GetRawText(), p.ParameterType);
            else if (p.HasDefaultValue) args[i] = p.DefaultValue;
            else args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        }
        return args;
    }

    private static JsonElement? BuildSchema(ParameterInfo[] parameters)
    {
        var ps = parameters.Where(p =>
            p.ParameterType != typeof(JsonElement) && p.ParameterType != typeof(JsonElement?)
            && p.ParameterType != typeof(CancellationToken)).ToList();
        if (ps.Count == 0) return ToolSchema.Parse("""{"type":"object"}""");

        var props = new Dictionary<string, JsonElement>();
        var req = new List<string>();
        foreach (var p in ps)
        {
            var desc = p.GetCustomAttribute<ToolParamAttribute>()?.Description
                    ?? p.GetCustomAttribute<DescriptionAttribute>()?.Description;
            var s = new Dictionary<string, object> { ["type"] = ClrToJsonType(p.ParameterType) };
            if (desc is not null) s["description"] = desc;
            props[p.Name!] = ToolSchema.SerializeToElement(s);
            if (!p.HasDefaultValue) req.Add(p.Name!);
        }
        return ToolSchema.Object(props, req.Count > 0 ? req : null);
    }

    private static string ClrToJsonType(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(string))  return "string";
        if (u == typeof(bool))    return "boolean";
        if (u == typeof(int) || u == typeof(long) || u == typeof(short)) return "integer";
        if (u == typeof(float) || u == typeof(double) || u == typeof(decimal)) return "number";
        if (u.IsArray || (u.IsGenericType && u.GetGenericTypeDefinition() == typeof(List<>))) return "array";
        return "string";
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  JSON Schema helpers
// ═══════════════════════════════════════════════════════════════════════════

public static class ToolSchema
{
    private static readonly JsonSerializerOptions s_opts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static JsonElement Parse(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }

    public static JsonElement Object(Dictionary<string, JsonElement> properties, IEnumerable<string>? required = null)
    {
        var obj = new Dictionary<string, object> { ["type"] = "object", ["properties"] = properties };
        if (required is not null) obj["required"] = required.ToList();
        return SerializeToElement(obj);
    }

    public static JsonElement String(string? desc = null) => Prop("string", desc);
    public static JsonElement Number(string? desc = null) => Prop("number", desc);
    public static JsonElement Integer(string? desc = null) => Prop("integer", desc);
    public static JsonElement Boolean(string? desc = null) => Prop("boolean", desc);

    public static JsonElement Array(JsonElement items, string? desc = null)
    { var o = new Dictionary<string, object> { ["type"] = "array", ["items"] = items }; if (desc is not null) o["description"] = desc; return SerializeToElement(o); }

    public static JsonElement Enum(IEnumerable<string> values, string? desc = null)
    { var o = new Dictionary<string, object> { ["type"] = "string", ["enum"] = values.ToList() }; if (desc is not null) o["description"] = desc; return SerializeToElement(o); }

    private static JsonElement Prop(string type, string? desc)
    { var o = new Dictionary<string, object> { ["type"] = type }; if (desc is not null) o["description"] = desc; return SerializeToElement(o); }

    internal static JsonElement SerializeToElement(object value)
    { var j = JsonSerializer.Serialize(value, s_opts); using var d = JsonDocument.Parse(j); return d.RootElement.Clone(); }
}
