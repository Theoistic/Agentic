namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  Workflow
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>Execution state of a single <see cref="WorkflowStep"/>.</summary>
public enum WorkflowStepStatus
{
    /// <summary>The step has not yet been verified as complete.</summary>
    Pending,
    /// <summary>The step's verification callback returned <c>true</c>.</summary>
    Completed
}

/// <summary>A single step in a <see cref="Workflow"/>, with an instruction for the model and an optional verification callback.</summary>
public sealed class WorkflowStep
{
    /// <summary>Short name identifying this step (used in prompts and events).</summary>
    public required string Name { get; init; }
    /// <summary>Instruction text appended to the system prompt for this step.</summary>
    public required string Instruction { get; init; }
    /// <summary>Optional async predicate that returns <c>true</c> when this step is complete.</summary>
    public Func<WorkflowContext, Task<bool>>? Verify { get; init; }
    /// <summary>Current execution status of this step.</summary>
    public WorkflowStepStatus Status { get; internal set; } = WorkflowStepStatus.Pending;
}

/// <summary>Context passed to a <see cref="WorkflowStep.Verify"/> callback so it can inspect tool calls and model output.</summary>
public sealed class WorkflowContext
{
    /// <summary>All tool invocations executed so far during this workflow run.</summary>
    public required List<ToolInvocation> ToolInvocations { get; init; }
    /// <summary>Accumulated model response text across all rounds so far.</summary>
    public required string ResponseText { get; init; }
}

/// <summary>
/// Defines an ordered sequence of steps that the <see cref="Agent"/> will execute in turn,
/// verifying each before advancing to the next.
/// </summary>
/// <param name="name">Display name for this workflow.</param>
public sealed class Workflow(string name)
{
    /// <summary>The display name of this workflow.</summary>
    public string Name { get; } = name;
    /// <summary>The ordered list of steps that make up this workflow.</summary>
    public List<WorkflowStep> Steps { get; } = [];

    /// <summary>Adds a step with an async verification callback and returns this workflow for fluent chaining.</summary>
    /// <param name="name">Step name.</param>
    /// <param name="instruction">Instruction for the model.</param>
    /// <param name="verify">Optional async predicate; <c>null</c> means auto-complete.</param>
    public Workflow Step(string name, string instruction, Func<WorkflowContext, Task<bool>>? verify = null)
    {
        Steps.Add(new() { Name = name, Instruction = instruction, Verify = verify });
        return this;
    }

    /// <summary>Adds a step with a synchronous verification callback and returns this workflow for fluent chaining.</summary>
    /// <param name="name">Step name.</param>
    /// <param name="instruction">Instruction for the model.</param>
    /// <param name="verify">Synchronous predicate wrapped into a <see cref="Task"/>.</param>
    public Workflow Step(string name, string instruction, Func<WorkflowContext, bool> verify)
    {
        Steps.Add(new() { Name = name, Instruction = instruction,
            Verify = ctx => Task.FromResult(verify(ctx)) });
        return this;
    }

    internal void Reset() { foreach (var s in Steps) s.Status = WorkflowStepStatus.Pending; }
}

/// <summary>The result of a completed (or aborted) <see cref="Workflow"/> run.</summary>
public sealed class WorkflowResult
{
    /// <summary>Combined model response text from all rounds.</summary>
    public required string Text { get; init; }
    /// <summary><c>true</c> when every step reached <see cref="WorkflowStepStatus.Completed"/>.</summary>
    public required bool Completed { get; init; }
    /// <summary>Final state of each step after the run.</summary>
    public required List<WorkflowStep> Steps { get; init; }
    /// <summary>All tool invocations executed during the workflow, in order.</summary>
    public List<ToolInvocation> ToolInvocations { get; init; } = [];
}
