using System.ComponentModel;
using System.Data;

namespace Agentic.Cli;

public sealed class NativeLocalToolbox : IAgentToolSet
{
    private readonly List<string> _notes = [];

    [Tool, Description("Returns the current UTC date and time.")]
    public string GetCurrentTime() => $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";

    [Tool, Description("Evaluates an arithmetic expression and returns the numeric result.")]
    public string Calculate([ToolParam("Arithmetic expression such as (2 + 3) * 4")] string expression)
    {
        var value = new DataTable().Compute(expression, null);
        return value?.ToString() ?? "null";
    }

    [Tool, Description("Reverses text exactly as provided.")]
    public string ReverseText([ToolParam("Text to reverse")] string text) => new(text.Reverse().ToArray());

    [Tool, Description("Stores a short note in memory and returns the new total count.")]
    public Task<string> SaveNote([ToolParam("The note text to store")] string note)
    {
        _notes.Add(note);
        return Task.FromResult($"Saved note #{_notes.Count}.");
    }

    [Tool, Description("Lists every note saved during the current session.")]
    public string ListNotes() => _notes.Count == 0
        ? "No notes saved yet."
        : string.Join("\n", _notes.Select((note, index) => $"[{index + 1}] {note}"));
}

public sealed class NativeLocalToolsScenario : IScenario
{
    private const string SystemPrompt = """
        You are testing Agentic's NativeBackend with local tool calling.
        Prefer tools whenever they can make the answer more precise, verifiable, or stateful.
        Use SaveNote and ListNotes to prove that tool state persists across turns.
        Show concise final answers after the necessary tool calls.
        """;

    public string Name => "Native Local Tools";

    public Task RunAsync(ILLMBackend lm, IServiceProvider services, string? mcpUrl = null)
    {
        var agent = new Agent(lm, new AgentOptions
        {
            SystemPrompt = SystemPrompt,
            Compaction = new CompactionOptions(),
            Reasoning = ReasoningEffort.None,
        });

        agent.RegisterTools(new NativeLocalToolbox());

        ConsoleHelper.WriteDim($"Tools: {string.Join(", ", agent.Tools.GetAllDescriptors().Select(d => d.Name))}");
        ConsoleHelper.WriteDim("Try: 'what time is it?', 'calculate (12.5 + 7.5) * 3', 'save note apples', 'list notes'");
        Console.WriteLine();

        return new AgenticRepl(agent).RunAsync();
    }
}
