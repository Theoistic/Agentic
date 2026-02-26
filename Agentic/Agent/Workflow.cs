namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  Workflow
// ═══════════════════════════════════════════════════════════════════════════

public enum WorkflowStepStatus { Pending, Completed }

public sealed class WorkflowStep
{
    public required string Name { get; init; }
    public required string Instruction { get; init; }
    public Func<WorkflowContext, Task<bool>>? Verify { get; init; }
    public WorkflowStepStatus Status { get; internal set; } = WorkflowStepStatus.Pending;
}

public sealed class WorkflowContext
{
    public required List<ToolInvocation> ToolInvocations { get; init; }
    public required string ResponseText { get; init; }
}

public sealed class Workflow(string name)
{
    public string Name { get; } = name;
    public List<WorkflowStep> Steps { get; } = [];

    public Workflow Step(string name, string instruction, Func<WorkflowContext, Task<bool>>? verify = null)
    {
        Steps.Add(new() { Name = name, Instruction = instruction, Verify = verify });
        return this;
    }

    public Workflow Step(string name, string instruction, Func<WorkflowContext, bool> verify)
    {
        Steps.Add(new() { Name = name, Instruction = instruction,
            Verify = ctx => Task.FromResult(verify(ctx)) });
        return this;
    }

    internal void Reset() { foreach (var s in Steps) s.Status = WorkflowStepStatus.Pending; }
}

public sealed class WorkflowResult
{
    public required string Text { get; init; }
    public required bool Completed { get; init; }
    public required List<WorkflowStep> Steps { get; init; }
    public List<ToolInvocation> ToolInvocations { get; init; } = [];
}
