using System.Text.Json;
using Agentic;
using Agentic.Cli;
using Agentic.Mcp;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// ── Config (override via CLI args: [endpoint] [model]) ────────────────────
var endpoint = args.Length > 0 ? args[0] : "http://10.1.20.127:1234";
var model = args.Length > 1 ? args[1] : "qwen/qwen3.5-9b";

// ── MCP host ──────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder([]);  // don't forward CLI args to ASP.NET
builder.Logging.SetMinimumLevel(LogLevel.None);                          // suppress ASP.NET/Kestrel noise
builder.Logging.AddFilter("Agentic.Mcp", LogLevel.Information);          // show MCP traffic
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.WebHost.UseUrls("http://10.1.20.127:5100");
builder.Services.AddAgenticMcp(opt => opt.ApiKey = "dev-secret-key-1234");
builder.Services.AddStore(builder.Configuration);  // provider driven by Database:Provider config key

using var lm = new LM(new LMConfig
{
    ModelName      = model,
    EmbeddingModel = "text-embedding-qwen3-embedding-0.6b",
    Endpoint       = endpoint,
});

var app = builder.Build();
app.MapMcpServer("/mcp");

var toolRegistry  = app.Services.GetRequiredService<ToolRegistry>();
var store         = app.Services.GetRequiredService<IStore>();
var mcpOptions    = app.Services.GetRequiredService<McpServerOptions>();
var docsFolder    = @"C:\Users\Theo\Downloads\zikksamples\zikksamples";
toolRegistry.Register(new EmbeddingTools(lm));
toolRegistry.Register(new DocumentTools(lm, docsFolder));
toolRegistry.Register(new HsCodeTools(lm, store.Collection<HSDescription>("hscodes")));

await app.StartAsync();
var mcpUrl = app.GetMcpUrl();
if (mcpOptions.ApiKey is not null)
    mcpUrl = $"{mcpUrl}?key={Uri.EscapeDataString(mcpOptions.ApiKey)}";

// ── Banner ────────────────────────────────────────────────────────────────
PrintBanner($"Agentic CLI  ·  {mcpUrl}  ·  {model}");
Console.WriteLine();
WriteDim($"Tools: {string.Join(", ", toolRegistry.GetAllDescriptors().Select(d => d.Name))}");
var docFiles = Directory.Exists(docsFolder)
    ? Directory.GetFiles(docsFolder, "*.pdf", SearchOption.AllDirectories)
    : [];
WriteDim($"Docs:  {(docFiles.Length == 0 ? "(none)" : string.Join(", ", docFiles.Select(Path.GetFileName)))}");
WriteDim("Commands: exit | quit | /reset | /compact [light|standard|detailed]");

// ── LM health check ───────────────────────────────────────────────────────
Console.Write("  LM server  ");
if (await lm.PingAsync())
    Write(ConsoleColor.Green, "● online\n");
else
    Write(ConsoleColor.Red, $"● unreachable at {endpoint}\n");

Console.WriteLine();

var inTextStream  = false;  // mid-stream TextDelta run in progress
var atLineStart   = true;   // last char written was \n — prevents double-blank-lines
var hadToolEvents = false;  // tool call/result occurred since last text block

const string SystemPrompt = """
    You are an image-processing assistant. You have access to a database of image records.
    Each record has an id, name, imageUrl, and an optional description.
    When asked to process a record: fetch the image, analyse it visually, then
    update the record's description with a detailed account of what the image shows.
    Always confirm what you did.
    """;

Agent agent = null!;
agent = new Agent(lm, new AgentOptions
{
    SystemPrompt = SystemPrompt,
    OnEvent      = HandleEvent,
    Compaction   = new CompactionOptions(),
    Thinking     = new ThinkingConfig { Enabled = false },
});

WriteDim($"Context: auto-compact at {agent.Context!.Options.CompactionThreshold:P0} of {agent.Context.Options.MaxContextTokens:#,0} tokens");

// ── REPL ─────────────────────────────────────────────────────────────────
while (true)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("\nYou › ");
    Console.ResetColor();

    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(input)) continue;

    if (input is "exit" or "quit" or "/exit" or "/quit") break;

    if (input is "/reset")
    {
        agent.ResetConversation();
        WriteDim("[conversation reset]");
        continue;
    }

    if (input.StartsWith("/compact"))
    {
        var level = input.Contains("light") ? CompactionLevel.Light
            : input.Contains("detailed") ? CompactionLevel.Detailed
            : CompactionLevel.Standard;
        try
        {
            var cp = await agent.CompactAsync(level);
            if (cp is not null)
                WriteDim($"[compacted: {cp.Level} · #{cp.CompactionCount} · {cp.Objective[..Math.Min(80, cp.Objective.Length)]}]");
        }
        catch (Exception ex) { Write(ConsoleColor.Red, $"  compact failed: {ex.Message}\n"); }
        continue;
    }

    Console.WriteLine();

    try
    {
        await agent.ChatStreamAsync(input, mcpUrl);
    }
    catch (LMException ex) when (ex.StatusCode == 0)
    {
        EnsureNewLine();
        Write(ConsoleColor.Red, $"⚡ LM server connection lost — {ex.Message}\n");
    }
    catch (LMException ex)
    {
        EnsureNewLine();
        Write(ConsoleColor.Red, $"[LM error {ex.StatusCode}] {ex.Message}\n");
    }
    catch (Exception ex)
    {
        EnsureNewLine();
        Write(ConsoleColor.Red, $"[error] {ex.Message}\n");
    }
}

await app.StopAsync();

// ── Event handler ─────────────────────────────────────────────────────────

void HandleEvent(AgentEvent e)
{
    switch (e.Kind)
    {
        // Reasoning / thinking text (non-streaming path)
        case AgentEventKind.Reasoning:
            EnsureNewLine();
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.Write("  ◆ think  ");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(Truncate(e.Text, 220));
            Console.ResetColor();
            atLineStart = true;
            break;

        // Streaming text delta — cyan so it reads differently from the user's white input
        case AgentEventKind.TextDelta:
            if (!inTextStream)
            {
                if (hadToolEvents) { Console.WriteLine(); atLineStart = true; }
                hadToolEvents = false;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Agent › ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                inTextStream = true;
                atLineStart  = false;
            }
            Console.Write(e.Text);
            if (!string.IsNullOrEmpty(e.Text))
                atLineStart = e.Text[^1] == '\n';
            break;

        // Tool invocation
        case AgentEventKind.ToolCall:
            EnsureNewLine();
            hadToolEvents = true;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"  ⚙  {e.ToolName}");
            if (!string.IsNullOrWhiteSpace(e.Arguments))
            {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.Write($"  {FormatArgs(e.Arguments)}");
            }
            Console.WriteLine();
            Console.ResetColor();
            atLineStart = true;
            break;

        // Tool result
        case AgentEventKind.ToolResult:
            hadToolEvents = true;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("  ↳  ");
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(Truncate(e.Text, 300));
            Console.ResetColor();
            atLineStart = true;
            break;

        // Final answer — print token stats if available
        case AgentEventKind.Answer:
            EnsureNewLine();
            hadToolEvents = false;
            if (e.TotalTokens > 0)
            {
                var maxCtx = agent.Context?.Options.MaxContextTokens ?? 0;
                var ctxInfo = (maxCtx > 0 && e.InputTokens > 0)
                    ? $"  ctx={(double)e.InputTokens.Value / maxCtx:P0}"
                    : "";
                WriteDim($"  [{e.InputTokens}↑  {e.OutputTokens}↓  {e.TotalTokens} tokens{ctxInfo}]");
            }
            break;

        case AgentEventKind.Compacted:
            EnsureNewLine();
            Write(ConsoleColor.Magenta, $"  ◇ {e.Text}\n");
            atLineStart = true;
            break;

        case AgentEventKind.StepCompleted:
            EnsureNewLine();
            Write(ConsoleColor.DarkGreen, $"  ✓ step: {e.Text}\n");
            atLineStart = true;
            break;

        case AgentEventKind.WorkflowCompleted:
            EnsureNewLine();
            Write(ConsoleColor.Green, $"  ✓✓ workflow complete: {e.Text}\n");
            atLineStart = true;
            break;
    }
}

// Closes an in-progress TextDelta run. Only emits \n if not already at line start.
void EnsureNewLine()
{
    if (!inTextStream) return;
    if (!atLineStart) Console.WriteLine();
    Console.ResetColor();
    inTextStream = false;
    atLineStart  = true;
}

// ── Helpers ───────────────────────────────────────────────────────────────

static string FormatArgs(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return "";
    try
    {
        var doc = JsonDocument.Parse(json);
        return string.Join("  ", doc.RootElement.EnumerateObject()
            .Select(p => $"{p.Name}={p.Value}"));
    }
    catch
    {
        return Truncate(json, 140);
    }
}

static string Truncate(string? text, int max)
{
    if (string.IsNullOrEmpty(text)) return "";
    var single = text.ReplaceLineEndings(" ");
    return single.Length <= max ? single : single[..max] + "…";
}

static void Write(ConsoleColor color, string text)
{
    Console.ForegroundColor = color;
    Console.Write(text);
    Console.ResetColor();
}

static void WriteDim(string text) => Write(ConsoleColor.DarkGray, text + "\n");

static void PrintBanner(string text)
{
    var rule = new string('═', text.Length + 4);
    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.WriteLine(rule);
    Console.WriteLine($"  {text}  ");
    Console.WriteLine(rule);
    Console.ResetColor();
}
