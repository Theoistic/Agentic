using Agentic;
using Agentic.Cli;
using Agentic.Runtime.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Agentic.Storage;
using Mantle = Agentic.Runtime.Mantle;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var modelPath = Environment.GetEnvironmentVariable("AGENTIC_NATIVE_MODEL_PATH")
    ?? @"C:\Users\Theo\.lmstudio\models\lmstudio-community\Qwen3.5-9B-GGUF\Qwen3.5-9B-Q4_K_M.gguf";

var embedModelPath = Environment.GetEnvironmentVariable("AGENTIC_EMBED_MODEL_PATH")
    ?? @"C:\Users\Theo\.lmstudio\models\lmstudio-community\embeddinggemma-300m-qat-GGUF\embeddinggemma-300m-qat-Q4_0.gguf";

if (string.IsNullOrWhiteSpace(modelPath))
{
    ConsoleHelper.PrintBanner("Agentic CLI  ·  Native Local Tools");
    Console.WriteLine();
    ConsoleHelper.Write(ConsoleColor.Yellow, "Set these environment variables before running the CLI:\n");
    ConsoleHelper.WriteDim("  AGENTIC_NATIVE_MODEL_PATH   → path to the chat GGUF model file");
    ConsoleHelper.WriteDim("  AGENTIC_EMBED_MODEL_PATH    → path to the embedding GGUF model file (optional)");
    return;
}

var backend = Enum.TryParse<LlamaBackend>(
    Environment.GetEnvironmentVariable("AGENTIC_BACKEND"), ignoreCase: true, out var parsedBackend)
    ? parsedBackend
    : LlamaBackend.Cuda;

var cudaVersion = Environment.GetEnvironmentVariable("AGENTIC_CUDA_VERSION"); // e.g. "12.4", null = auto
var releaseTag  = Environment.GetEnvironmentVariable("AGENTIC_RELEASE_TAG");  // e.g. "b8269", null = latest

var sessionOptions = new Mantle.LmSessionOptions
{
    ModelPath = modelPath,
    ToolRegistry = new Mantle.ToolRegistry(),
    Compaction = new Mantle.ConversationCompactionOptions(4096, ReservedForGeneration: 256),
    DefaultRequest = new Mantle.ResponseRequest
    {
        MaxOutputTokens = 1024,
        EnableThinking = false,
    },
    ContextTokens = 8192,
    ResetContextTokens = 4096,
    BatchTokens = 1024,
    MicroBatchTokens = 1024,
    MaxToolRounds = 32,
};

var installProgress = new Progress<(string message, double percent)>(
    p => Console.Write($"\r  [{p.percent,3:F0}%] {p.message,-60}"));

await using var chatBackend = new NativeBackend(sessionOptions, backend, cudaVersion: cudaVersion, releaseTag: releaseTag,
    installProgress: installProgress,
    modelName: Path.GetFileNameWithoutExtension(modelPath));

var embedSessionOptions = new Mantle.LmSessionOptions
{
    ModelPath = embedModelPath,
    ToolRegistry = new Mantle.ToolRegistry(),
    Compaction = new Mantle.ConversationCompactionOptions(2048, ReservedForGeneration: 0),
    ContextTokens = 2048,
    BatchTokens = 512,
    MicroBatchTokens = 512,
};

await using var embedBackend = new NativeBackend(embedSessionOptions, backend, cudaVersion: cudaVersion, releaseTag: releaseTag,
    installProgress: installProgress,
    modelName: Path.GetFileNameWithoutExtension(embedModelPath));

await using var lm = new BackendRouter()
    .Add(Path.GetFileNameWithoutExtension(modelPath), chatBackend, isDefault: true)
    .Add(Path.GetFileNameWithoutExtension(embedModelPath), embedBackend, isEmbedding: true);

IScenario scenario = new HsCodeAnalyzerScenario();
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddStore();
using var host = builder.Build();

ConsoleHelper.PrintBanner($"Agentic CLI  ·  {scenario.Name}  ·  {Path.GetFileName(modelPath)}");
Console.WriteLine();
ConsoleHelper.WriteDim($"Chat:    {modelPath}");
ConsoleHelper.WriteDim($"Embed:   {embedModelPath}");

Console.Write("  Native backend  ");
if (await lm.PingAsync())
{
    ConsoleHelper.Write(ConsoleColor.Green, "● online\n");
    ConsoleHelper.WriteDim($"Runtime: {chatBackend.BackendDirectory}");
}
else
    ConsoleHelper.Write(ConsoleColor.Red, "● failed to initialize\n");

Console.WriteLine();

await scenario.RunAsync(lm, host.Services);
