using Agentic;
using Agentic.Cli;
using Microsoft.Extensions.DependencyInjection;
using Mantle = Agentic.Runtime.Mantle;

Console.OutputEncoding = System.Text.Encoding.UTF8;

//var backendDirectory = Environment.GetEnvironmentVariable("AGENTIC_NATIVE_BACKEND_DIR");
//var modelPath = Environment.GetEnvironmentVariable("AGENTIC_NATIVE_MODEL_PATH");

const string backendDirectory = @"C:\Users\theo\Downloads\llama-b8250-bin-win-cuda-12.4-x64";
const string modelPath = @"C:\Users\theo\.lmstudio\models\lmstudio-community\Qwen3.5-9B-GGUF\Qwen3.5-9B-Q4_K_M.gguf";

if (string.IsNullOrWhiteSpace(backendDirectory) || string.IsNullOrWhiteSpace(modelPath))
{
    ConsoleHelper.PrintBanner("Agentic CLI  ·  Native Local Tools");
    Console.WriteLine();
    ConsoleHelper.Write(ConsoleColor.Yellow, "Set these environment variables before running the CLI:\n");
    ConsoleHelper.WriteDim("  AGENTIC_NATIVE_BACKEND_DIR  → path to the llama.cpp backend binaries");
    ConsoleHelper.WriteDim("  AGENTIC_NATIVE_MODEL_PATH   → path to the GGUF model file");
    return;
}

var sessionOptions = new Mantle.LmSessionOptions
{
    BackendDirectory = backendDirectory,
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

await using var lm = new NativeBackend(sessionOptions, Path.GetFileNameWithoutExtension(modelPath));

IScenario scenario = new NativeLocalToolsScenario();
using var services = new ServiceCollection().BuildServiceProvider();

ConsoleHelper.PrintBanner($"Agentic CLI  ·  {scenario.Name}  ·  {Path.GetFileName(modelPath)}");
Console.WriteLine();
ConsoleHelper.WriteDim($"Backend: {backendDirectory}");
ConsoleHelper.WriteDim($"Model:   {modelPath}");

Console.Write("  Native backend  ");
if (await lm.PingAsync())
    ConsoleHelper.Write(ConsoleColor.Green, "● online\n");
else
    ConsoleHelper.Write(ConsoleColor.Red, "● failed to initialize\n");

Console.WriteLine();

await scenario.RunAsync(lm, services);
