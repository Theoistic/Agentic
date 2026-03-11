using Agentic.Runtime;
using Agentic.Runtime.Core;
using Mantle = Agentic.Runtime.Mantle;

const string modelPath = @"C:\Users\Theo\.lmstudio\models\lmstudio-community\Qwen3.5-9B-GGUF\Qwen3.5-9B-Q4_K_M.gguf";

Mantle.IChatRenderer renderer = new Mantle.ConsoleChatRenderer(Console.Out);

var installProgress = new Progress<(string message, double percent)>(
    p => Console.Write($"\r  [{p.percent:F0,3}%] {p.message,-60}"));

await using var agent = await new Agent()
    .Named("Challenge Runner")
    .ForPurpose("Work through long-running, multi-step challenges that require deep reasoning, repeated tool use, verification, persistence across many turns, and strict instruction following under sustained pressure.")
    .WithWorkflow(
        "Break the objective into a sequence of concrete sub-problems.",
        "Use tools repeatedly to gather facts, compute intermediate results, and verify progress.",
        "Keep working until the full challenge is solved, even when it requires over one hundred tool calls.",
        "Track partial progress and explain what remains before finishing.",
        "Do not prematurely summarize or stop before every required tool-driven checkpoint has been completed.")
    .WithObjective("Solve long-running challenges that require over one hundred tool calls with strong instruction fidelity.")
    .WithContext("environment", "local console demo")
    .WithContext("challenge_mode", true)
    .WithContext("minimum_expected_tool_calls", 100)
    .UseModel(modelPath, LlamaBackend.Cuda, cudaVersion: "12.4")
    .WithInstallProgress(installProgress)
    .WithLogger(Mantle.NullLogger.Instance)
    .WithCompaction(new Mantle.ConversationCompactionOptions(
        MaxInputTokens: 16384,
        ReservedForGeneration: 256))
    .WithSessionOptions(options => options with
    {
        ContextTokens = 16384,
        ResetContextTokens = 4096,
        BatchTokens = 1024,
        MicroBatchTokens = 1024,
        MaxToolRounds = 128
    })
    .WithInference(new Mantle.ResponseRequest
    {
        MaxOutputTokens = 4096,
    })
    .AddTool(new Mantle.AgentTool(
        "get_current_time",
        "Returns the current UTC date and time.",
        [],
        _ => $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC"))
    .AddTool(new Mantle.AgentTool(
        "calculate",
        "Evaluates an arithmetic expression.",
        [new Mantle.ToolParameter("expression", "string", "Arithmetic expression to evaluate.")],
        args =>
        {
            string expression = args.GetValueOrDefault("expression")?.ToString() ?? "";
            object? result = new System.Data.DataTable().Compute(expression, null);
            return result?.ToString() ?? "null";
        }))
    .AddTool(new Mantle.AgentTool(
        "reverse_text",
        "Reverses text exactly as provided.",
        [new Mantle.ToolParameter("text", "string", "Text to reverse.")],
        args => new string((args.GetValueOrDefault("text")?.ToString() ?? string.Empty).Reverse().ToArray())))
    .AddTool(new Mantle.AgentTool(
        "count_characters",
        "Counts the number of characters in a string.",
        [new Mantle.ToolParameter("text", "string", "Text to count.")],
        args => (args.GetValueOrDefault("text")?.ToString() ?? string.Empty).Length.ToString()))
    .InitializeAsync();

agent.StatusChanged += (_, e) => Console.WriteLine($"(status: {e.PreviousStatus} -> {e.CurrentStatus})");
agent.ObjectiveChanged += (_, e) => Console.WriteLine($"(objective: {e.PreviousObjective ?? "<none>"} -> {e.CurrentObjective ?? "<none>"})");
agent.DebugViewCreated += (_, debugView) => renderer.RenderDebug(debugView);

Console.WriteLine("Interactive agent demo. Type /exit to quit, /reset to clear history, /objective <text> to change the objective, /challenge to run a 100+ tool-call stress test.");
Console.WriteLine();

while (true)
{
    Console.Write("> ");
    string? input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input))
        continue;

    if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
        break;

    if (input.Equals("/reset", StringComparison.OrdinalIgnoreCase))
    {
        agent.ResetConversation();
        Console.WriteLine("(conversation reset)");
        Console.WriteLine();
        continue;
    }

    if (input.StartsWith("/objective ", StringComparison.OrdinalIgnoreCase))
    {
        agent.WithObjective(input[11..].Trim());
        Console.WriteLine();
        continue;
    }

    if (input.Equals("/challenge", StringComparison.OrdinalIgnoreCase))
    {
        input = "You are running a deliberate instruction-following stress test. Complete the entire challenge exactly and do not stop early. " +
                "You must use tools more than 100 times before giving your final answer. " +
                "Phase 1: call get_current_time once and record the timestamp. " +
                "Phase 2: perform 40 arithmetic tool calculations in sequence, starting simple and becoming progressively more complex, and explicitly use previous results inside later expressions. " +
                "Phase 3: create 20 short checkpoint summaries in your head, and for each checkpoint use reverse_text once and count_characters once on the reversed checkpoint, for 40 more tool calls. " +
                "Phase 4: perform 20 final verification calculations that recombine earlier numeric results in different ways to detect inconsistencies. " +
                "Phase 5: produce a final structured report containing the timestamp, the staged results, the verification status, and the exact number of tool calls used. " +
                "Do not provide the final answer until every phase is complete and the total tool calls exceed 100.";
    }

    try
    {
        renderer.BeginAssistantMessage();

        await foreach (var chunk in agent.GenerateAsync(input))
            renderer.Render(chunk);

        renderer.EndAssistantMessage();
        Console.WriteLine($"(history count: {agent.History.Count})");
    }
    catch (Exception ex)
    {
        renderer.RenderError(ex);
    }

    Console.WriteLine();
}
