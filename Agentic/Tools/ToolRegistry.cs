using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  Tool attributes & interfaces
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Marks a public method on an <see cref="IAgentToolSet"/> as an agent tool.
/// The method will be discovered by <see cref="ToolRegistry"/> and exposed to the model.
/// </summary>
/// <param name="name">Optional explicit tool name. Defaults to the snake_case form of the method name.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ToolAttribute(string? name = null) : Attribute { public string? Name { get; } = name; }

/// <summary>
/// Provides a description for a tool method parameter, included in the JSON Schema
/// sent to the model so it knows how to fill the argument.
/// </summary>
/// <param name="description">Human-readable description of the parameter.</param>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ToolParamAttribute(string description) : Attribute { public string Description { get; } = description; }

/// <summary>Marker interface for classes that expose one or more <see cref="ToolAttribute"/>-decorated methods.</summary>
public interface IAgentToolSet { }
/// <summary>Extension of <see cref="IAgentToolSet"/> for tool sets that hold resources requiring async cleanup.</summary>
public interface IDisposableToolSet : IAgentToolSet { ValueTask DisposeAsync(); }

// ═══════════════════════════════════════════════════════════════════════════
//  Tool registry
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>A lightweight descriptor for a registered tool, used when listing tools to MCP clients.</summary>
public sealed class AgentToolDescriptor
{
    /// <summary>Snake_case tool name exposed to the model.</summary>
    public required string Name { get; init; }
    /// <summary>Human-readable description sourced from <see cref="System.ComponentModel.DescriptionAttribute"/>.</summary>
    public string? Description { get; init; }
    /// <summary>JSON Schema object describing the tool's input parameters.</summary>
    public JsonElement? ParameterSchema { get; init; }
}

/// <summary>
/// Central registry for all <see cref="IAgentToolSet"/> instances.
/// Discovers <see cref="ToolAttribute"/>-decorated methods via reflection, builds their JSON Schemas,
/// and dispatches tool calls from the model at runtime.
/// </summary>
public sealed class ToolRegistry
{
    private readonly List<IAgentToolSet> _sets = [];
    private readonly Dictionary<string, ReflectedTool> _tools = new(StringComparer.Ordinal);

    /// <summary>All registered tool set instances, in registration order.</summary>
    public IEnumerable<IAgentToolSet> ToolSets => _sets;
    /// <summary>Total number of individual tools registered across all tool sets.</summary>
    public int Count => _tools.Count;
    /// <summary>
    /// Custom <see cref="JsonSerializerOptions"/> used when deserialising tool call arguments.
    /// Assign this to add converters for your domain types (e.g. <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>).
    /// <c>null</c> = use the default options.
    /// </summary>
    public JsonSerializerOptions? JsonOptions { get; set; }

    /// <summary>
    /// Registers all <see cref="ToolAttribute"/>-decorated methods from <paramref name="toolSet"/>.
    /// </summary>
    /// <typeparam name="T">The tool set type to register.</typeparam>
    /// <param name="toolSet">An instance whose public methods will be scanned.</param>
    /// <param name="replaceExisting">When <c>true</c>, any previously registered tools from the same type are removed first.</param>
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

    /// <summary>Returns a read-only snapshot of all registered tool descriptors.</summary>
    public IReadOnlyList<AgentToolDescriptor> GetAllDescriptors() =>
        _tools.Values.Select(t => new AgentToolDescriptor
        { Name = t.Name, Description = t.Description, ParameterSchema = t.ParameterSchema }).ToList();

    /// <summary>
    /// Invokes the named tool with the provided JSON arguments and returns its string result.
    /// </summary>
    /// <param name="name">The tool name as registered (snake_case).</param>
    /// <param name="arguments">JSON object containing the argument values.</param>
    /// <param name="context">Optional execution context (headers, properties) injected into tool methods that declare a <see cref="ToolContext"/> parameter.</param>
    /// <param name="ct">Cancellation token forwarded to the tool method.</param>
    /// <exception cref="KeyNotFoundException">Thrown when <paramref name="name"/> is not registered.</exception>
    public async Task<string> InvokeAsync(string name, JsonElement? arguments, ToolContext? context = null, CancellationToken ct = default)
    {
        if (!_tools.TryGetValue(name, out var tool))
            throw new KeyNotFoundException($"Tool '{name}' is not registered.");
        var args = BindArguments(tool.Parameters, arguments, JsonOptions, context, ct);
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

    private static object?[] BindArguments(ParameterInfo[] parameters, JsonElement? arguments, JsonSerializerOptions? jsonOptions, ToolContext? context, CancellationToken ct = default)
    {
        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.ParameterType == typeof(JsonElement?))       { args[i] = arguments; continue; }
            if (p.ParameterType == typeof(JsonElement))        { args[i] = arguments ?? default; continue; }
            if (p.ParameterType == typeof(CancellationToken))  { args[i] = ct; continue; }
            if (p.ParameterType == typeof(ToolContext))         { args[i] = context ?? ToolContext.Empty; continue; }
            if (arguments.HasValue && arguments.Value.TryGetProperty(p.Name!, out var prop))
                args[i] = JsonSerializer.Deserialize(prop.GetRawText(), p.ParameterType, jsonOptions);
            else if (p.HasDefaultValue) args[i] = p.DefaultValue;
            else args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        }
        return args;
    }

    private static JsonElement? BuildSchema(ParameterInfo[] parameters)
    {
        var ps = parameters.Where(p =>
            p.ParameterType != typeof(JsonElement) && p.ParameterType != typeof(JsonElement?)
            && p.ParameterType != typeof(CancellationToken)
            && p.ParameterType != typeof(ToolContext)).ToList();
        if (ps.Count == 0) return ToolSchema.Parse("""{"type":"object"}""");

        var props = new Dictionary<string, JsonElement>();
        var req = new List<string>();
        foreach (var p in ps)
        {
            var desc = p.GetCustomAttribute<ToolParamAttribute>()?.Description
                    ?? p.GetCustomAttribute<DescriptionAttribute>()?.Description;
            props[p.Name!] = BuildTypeSchema(p.ParameterType, desc);
            if (!p.HasDefaultValue) req.Add(p.Name!);
        }
        return ToolSchema.Object(props, req.Count > 0 ? req : null);
    }

    private static JsonElement BuildTypeSchema(Type t, string? desc = null)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(string))  return ToolSchema.String(desc);
        if (u == typeof(bool))    return ToolSchema.Boolean(desc);
        if (u == typeof(int) || u == typeof(long) || u == typeof(short)) return ToolSchema.Integer(desc);
        if (u == typeof(float) || u == typeof(double) || u == typeof(decimal)) return ToolSchema.Number(desc);

        if (u.IsArray || (u.IsGenericType && u.GetGenericTypeDefinition() == typeof(List<>)))
        {
            var elem = u.IsArray ? u.GetElementType()! : u.GetGenericArguments()[0];
            return ToolSchema.Array(BuildTypeSchema(elem), desc);
        }

        if (u.IsClass || (u.IsValueType && !u.IsPrimitive && !u.IsEnum))
        {
            var props = new Dictionary<string, JsonElement>();
            var req = new List<string>();
            foreach (var prop in u.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead))
            {
                var name = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                         ?? (char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..]);
                var propDesc = prop.GetCustomAttribute<DescriptionAttribute>()?.Description;
                props[name] = BuildTypeSchema(prop.PropertyType, propDesc);
                if (prop.PropertyType.IsValueType && Nullable.GetUnderlyingType(prop.PropertyType) is null)
                    req.Add(name);
            }
            var obj = new Dictionary<string, object> { ["type"] = "object", ["properties"] = props };
            if (req.Count > 0) obj["required"] = req;
            if (desc is not null) obj["description"] = desc;
            return ToolSchema.SerializeToElement(obj);
        }

        return ToolSchema.String(desc);
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

/// <summary>Helpers for building and parsing JSON Schema fragments used in tool parameter definitions.</summary>
public static class ToolSchema
{
    private static readonly JsonSerializerOptions s_opts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Parses a raw JSON string into a cloned <see cref="JsonElement"/>.</summary>
    public static JsonElement Parse(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }

    /// <summary>Builds an <c>object</c> JSON Schema from a property map and optional required list.</summary>
    public static JsonElement Object(Dictionary<string, JsonElement> properties, IEnumerable<string>? required = null)
    {
        var obj = new Dictionary<string, object> { ["type"] = "object", ["properties"] = properties };
        if (required is not null) obj["required"] = required.ToList();
        return SerializeToElement(obj);
    }

    /// <summary>Creates a <c>string</c> JSON Schema property with an optional description.</summary>
    public static JsonElement String(string? desc = null) => Prop("string", desc);
    /// <summary>Creates a <c>number</c> JSON Schema property with an optional description.</summary>
    public static JsonElement Number(string? desc = null) => Prop("number", desc);
    /// <summary>Creates an <c>integer</c> JSON Schema property with an optional description.</summary>
    public static JsonElement Integer(string? desc = null) => Prop("integer", desc);
    /// <summary>Creates a <c>boolean</c> JSON Schema property with an optional description.</summary>
    public static JsonElement Boolean(string? desc = null) => Prop("boolean", desc);

    /// <summary>Creates an <c>array</c> JSON Schema property with an item schema and optional description.</summary>
    public static JsonElement Array(JsonElement items, string? desc = null)
    { var o = new Dictionary<string, object> { ["type"] = "array", ["items"] = items }; if (desc is not null) o["description"] = desc; return SerializeToElement(o); }

    /// <summary>Creates a <c>string</c> JSON Schema property restricted to the given enum values.</summary>
    public static JsonElement Enum(IEnumerable<string> values, string? desc = null)
    { var o = new Dictionary<string, object> { ["type"] = "string", ["enum"] = values.ToList() }; if (desc is not null) o["description"] = desc; return SerializeToElement(o); }

    private static JsonElement Prop(string type, string? desc)
    { var o = new Dictionary<string, object> { ["type"] = type }; if (desc is not null) o["description"] = desc; return SerializeToElement(o); }

    internal static JsonElement SerializeToElement(object value)
    { var j = JsonSerializer.Serialize(value, s_opts); using var d = JsonDocument.Parse(j); return d.RootElement.Clone(); }
}
