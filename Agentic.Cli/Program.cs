using Agentic;
using Agentic.Cli;
using Agentic.Mcp;
using Agentic.Storage;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// ── MCP host ──────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder([]);
builder.Logging.SetMinimumLevel(LogLevel.None);
builder.Logging.AddFilter("Agentic.Mcp", LogLevel.Information);
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.WebHost.UseUrls("http://127.0.0.1:5100");
builder.Services.AddAgenticMcp(opt => opt.ApiKey = "dev-secret-key-1234");
builder.Services.AddStore(builder.Configuration);

var config = new LMConfig {
    ModelName      = "qwen3.5-9b",
    EmbeddingModel = "text-embedding-embeddinggemma-300m-qat",
    Endpoint       = "http://127.0.0.1:1234"
};

using var lm  = new LM(config);
var app       = builder.Build();
app.UseStaticFiles();
app.MapMcpServer("/mcp");
await app.StartAsync();

var mcpOptions = app.Services.GetRequiredService<McpServerOptions>();
var mcpUrl     = app.GetMcpUrl();
if (mcpOptions.ApiKey is not null)
    mcpUrl = $"{mcpUrl}?key={Uri.EscapeDataString(mcpOptions.ApiKey)}";

// ── Scenario ──────────────────────────────────────────────────────────────
IScenario scenario = new HsCodeAnalyzerScenario();

// ── Banner ────────────────────────────────────────────────────────────────
ConsoleHelper.PrintBanner($"Agentic CLI  ·  {scenario.Name}  ·  {mcpUrl}");
Console.WriteLine();

var wwwroot    = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var staticBase = app.Urls.FirstOrDefault() ?? "http://127.0.0.1:5100";
ConsoleHelper.WriteDim($"Static: {staticBase}/  →  {wwwroot}");

// ── LM health check ───────────────────────────────────────────────────────
Console.Write("  LM server  ");
if (await lm.PingAsync())
    ConsoleHelper.Write(ConsoleColor.Green, "● online\n");
else
    ConsoleHelper.Write(ConsoleColor.Red, $"● unreachable at {config.Endpoint}\n");

Console.WriteLine();

await scenario.RunAsync(lm, app.Services, mcpUrl);

await app.StopAsync();
