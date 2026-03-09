---
title: Workflows
parent: Features
nav_order: 7
---

# Workflows
{: .no_toc }

## Table of contents
{: .no_toc .text-delta }

1. TOC
{:toc}

---

Workflows let you break a complex task into a sequence of verified steps. The agent cannot skip ahead — each step has an optional async guardrail that must pass before the next step begins.

## Defining a Workflow

```csharp
var workflow = new Workflow("Process Invoice")
    .Step(
        name:        "Extract header",
        instruction: "Extract the invoice number, date, vendor name, currency, and total amount.",
        verify:      ctx => ctx.ToolInvocations.Any(t => t.ToolName == "save_invoice_header"))

    .Step(
        name:        "Extract line items",
        instruction: "Extract every line item into the database.",
        verify:      ctx => ctx.ToolInvocations.Any(t => t.ToolName == "save_invoice_lines"))

    .Step(
        name:        "Confirm totals",
        instruction: "Sum the line item values and confirm they match the invoice total.",
        verify:      ctx => ctx.ResponseText.Contains("match", StringComparison.OrdinalIgnoreCase));
```

Steps are verified **in order**. If a guardrail fails for step N, execution stops at that step even if later steps would have passed.

## Running a Workflow

```csharp
var result = await agent.RunWorkflowAsync(
    workflow,
    input:        "Process the attached invoice.",
    mcpServerUrl: "http://localhost:5100/mcp",
    maxRounds:    10);

if (result.Completed)
    Console.WriteLine("All steps completed.");
else
{
    var pending = result.Steps.Where(s => s.Status == WorkflowStepStatus.Pending);
    Console.WriteLine($"Incomplete steps: {string.Join(", ", pending.Select(s => s.Name))}");
}
```

## WorkflowStep Reference

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Short label used in prompts and `StepCompleted` events |
| `Instruction` | `string` | Text appended to the system prompt for this step |
| `Verify` | `Func<WorkflowContext, Task<bool>>?` | Async guardrail; `null` means the step auto-completes after each round |
| `Status` | `WorkflowStepStatus` | `Pending` or `Completed` — updated by the agent after each round |

## WorkflowContext

Available inside a `verify` callback:

| Property | Description |
|----------|-------------|
| `ToolInvocations` | All tool calls made so far in this workflow run |
| `ResponseText` | Accumulated model output across all rounds |

## WorkflowResult

| Property | Description |
|----------|-------------|
| `Completed` | `true` when every step reached `Completed` |
| `Text` | Combined model response text from all rounds |
| `Steps` | Final state of each step |
| `ToolInvocations` | All tool calls made during the run, in order |

## Guardrail Patterns

```csharp
// Tool was called at least once
verify: ctx => ctx.ToolInvocations.Any(t => t.ToolName == "save_data")

// Tool was called with a specific argument value
verify: ctx => ctx.ToolInvocations
    .Any(t => t.ToolName == "save_data" && t.Arguments.Contains("\"status\":\"ok\""))

// Model confirmed something in its output
verify: ctx => ctx.ResponseText.Contains("confirmed", StringComparison.OrdinalIgnoreCase)

// Async database check
verify: async ctx =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    return await db.Invoices.AnyAsync(i => i.Id == invoiceId && i.LineItems.Count > 0);
}
```

## Example: Document Processing Pipeline

A complete workflow that processes a multi-page document:

```csharp
var workflow = new Workflow("Document Intake")
    .Step(
        name:        "Classify document",
        instruction: "Identify the document type (invoice, contract, report, or other).",
        verify:      ctx => ctx.ToolInvocations.Any(t => t.ToolName == "set_document_type"))

    .Step(
        name:        "Extract key fields",
        instruction: "Extract all key metadata fields appropriate for this document type.",
        verify:      ctx => ctx.ToolInvocations.Any(t => t.ToolName == "save_metadata"))

    .Step(
        name:        "Generate summary",
        instruction: "Write a 2–3 sentence summary of the document's content.",
        verify:      ctx => ctx.ResponseText.Length > 100)

    .Step(
        name:        "Flag for review",
        instruction: "If the document contains any anomalies or requires human review, flag it.",
        verify:      null);  // auto-completes — no specific check needed

var result = await agent.RunWorkflowAsync(
    workflow,
    input:        "Process document: " + documentPath,
    mcpServerUrl: "http://localhost:5100/mcp",
    maxRounds:    20);

Console.WriteLine(result.Completed ? "Pipeline complete." : "Pipeline incomplete.");
```
