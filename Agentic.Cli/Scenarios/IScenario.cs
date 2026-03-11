namespace Agentic.Cli;

/// <summary>
/// A self-contained, runnable scenario. Implement this to define a custom agent
/// with its own tools, system prompt, and conversation flow.
/// Scenarios run in isolation — register only the tools they need.
/// </summary>
public interface IScenario
{
    /// <summary>Display name shown in the CLI banner.</summary>
    string Name { get; }

    /// <summary>Configure tools, create the agent, and start the REPL.</summary>
    Task RunAsync(ILLMBackend lm, IServiceProvider services, string mcpUrl);
}
