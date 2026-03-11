using Agentic;
using Agentic.Cli;
using Agentic.Runtime.Core;
using Microsoft.Extensions.DependencyInjection;
using Agentic.Storage;
using Mantle = Agentic.Runtime.Mantle;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var modelPath = Environment.GetEnvironmentVariable("AGENTIC_NATIVE_MODEL_PATH")
    ?? @"C:\Users\Theo\.lmstudio\models\lmstudio-community\Qwen3.5-9B-GGUF\Qwen3.5-9B-Q4_K_M.gguf";

if (string.IsNullOrWhiteSpace(modelPath))
{
    ConsoleHelper.PrintBanner("Agentic CLI  ·  Native Local Tools");
    Console.WriteLine();
    ConsoleHelper.Write(ConsoleColor.Yellow, "Set this environment variable before running the CLI:\n");
    ConsoleHelper.WriteDim("  AGENTIC_NATIVE_MODEL_PATH   → path to the GGUF model file");
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

await using var lm = new NativeBackend(sessionOptions, backend, cudaVersion: cudaVersion, releaseTag: releaseTag,
    installProgress: new Progress<(string message, double percent)>(p => Console.Write($"\r  [{p.percent,3:F0}%] {p.message,-60}")),
    modelName: Path.GetFileNameWithoutExtension(modelPath));

IScenario scenario = new HsCodeAnalyzerScenario();
using var services = new ServiceCollection()
    .AddStore()
    .BuildServiceProvider();

ConsoleHelper.PrintBanner($"Agentic CLI  ·  {scenario.Name}  ·  {Path.GetFileName(modelPath)}");
Console.WriteLine();
ConsoleHelper.WriteDim($"Model:   {modelPath}");

Console.Write("  Native backend  ");
if (await lm.PingAsync())
{
    ConsoleHelper.Write(ConsoleColor.Green, "● online\n");
    ConsoleHelper.WriteDim($"Runtime: {lm.BackendDirectory}");
}
else
    ConsoleHelper.Write(ConsoleColor.Red, "● failed to initialize\n");

Console.WriteLine();

await scenario.RunAsync(lm, services);
