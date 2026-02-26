using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentic;

// ═══════════════════════════════════════════════════════════════════════════
//  Context management — compaction, checkpointing, rehydration
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>How aggressively to compress conversation history during compaction.</summary>
public enum CompactionLevel
{
    /// <summary>Keep only goals + next actions. Minimal detail.</summary>
    Light,
    /// <summary>Goals, decisions, status, next steps. Balanced.</summary>
    Standard,
    /// <summary>Full nuance: decisions + rationale, edge cases, hypotheses, key outputs.</summary>
    Detailed
}

/// <summary>Configuration for automatic and manual context compaction.</summary>
public sealed class CompactionOptions
{
    /// <summary>Maximum context window size in tokens (model-dependent).</summary>
    public int MaxContextTokens { get; set; } = 128_000;

    /// <summary>Auto-compact triggers when input_tokens / MaxContextTokens ≥ this value.</summary>
    public double CompactionThreshold { get; set; } = 0.85;

    /// <summary>Default compression level when auto-compacting.</summary>
    public CompactionLevel DefaultLevel { get; set; } = CompactionLevel.Standard;

    /// <summary>Number of recent user turns to keep verbatim ("hot tail").</summary>
    public int HotTailTurns { get; set; } = 4;

    /// <summary>Target token budget for the checkpoint (0 = model decides).</summary>
    public int TargetCheckpointTokens { get; set; }

    /// <summary>Automatically compact when threshold is reached.</summary>
    public bool AutoCompact { get; set; } = true;
}

/// <summary>Structured machine-readable checkpoint produced by compaction.</summary>
public sealed class Checkpoint
{
    [JsonPropertyName("objective")]        public string Objective { get; set; } = "";
    [JsonPropertyName("current_status")]   public string CurrentStatus { get; set; } = "";
    [JsonPropertyName("next_steps")]       public List<string> NextSteps { get; set; } = [];
    [JsonPropertyName("key_decisions")]    public List<string> KeyDecisions { get; set; } = [];
    [JsonPropertyName("constraints")]      public List<string> Constraints { get; set; } = [];
    [JsonPropertyName("open_questions")]   public List<string> OpenQuestions { get; set; } = [];
    [JsonPropertyName("key_artifacts")]    public List<string> KeyArtifacts { get; set; } = [];
    [JsonPropertyName("created_at")]       public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("compaction_count")] public int CompactionCount { get; set; }
    [JsonPropertyName("level")]            public CompactionLevel Level { get; set; }

    /// <summary>Render the checkpoint as a prompt section the LM can consume.</summary>
    public string ToPromptText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("═══ CONTEXT CHECKPOINT ═══");
        sb.AppendLine($"(Compaction #{CompactionCount} · {Level} · {CreatedAt:u})");

        AppendSection(sb, "Objective", Objective);
        AppendSection(sb, "Current Status", CurrentStatus);
        AppendList(sb, "Next Steps", NextSteps, numbered: true);
        AppendList(sb, "Key Decisions & Rationale", KeyDecisions);
        AppendList(sb, "Constraints & Non-goals (PINNED)", Constraints);
        AppendList(sb, "Open Questions / Risks", OpenQuestions);
        AppendList(sb, "Key Artifacts", KeyArtifacts);

        sb.AppendLine("═══ END CHECKPOINT ═══");
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string title, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        sb.AppendLine().Append("## ").AppendLine(title);
        sb.AppendLine(content);
    }

    private static void AppendList(StringBuilder sb, string title, List<string> items, bool numbered = false)
    {
        if (items.Count == 0) return;
        sb.AppendLine().Append("## ").AppendLine(title);
        for (int i = 0; i < items.Count; i++)
            sb.AppendLine(numbered ? $"{i + 1}. {items[i]}" : $"- {items[i]}");
    }
}

/// <summary>A single entry in the tracked conversation history.</summary>
public sealed class ConversationEntry
{
    [JsonPropertyName("role")]      public string Role { get; set; } = "";
    [JsonPropertyName("content")]   public string Content { get; set; } = "";
    [JsonPropertyName("tool_name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolName { get; set; }
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Tracks token usage, manages conversation history, and performs compaction
/// (summarising older context into structured checkpoints) so the agent can
/// run effectively unbounded sessions across many LLM calls.
/// </summary>
public sealed class ContextManager
{
    private readonly List<ConversationEntry> _history = [];
    private readonly List<string> _pinnedConstraints = [];
    private Checkpoint? _lastCheckpoint;
    private int _lastInputTokens;
    private int _lastOutputTokens;
    private int _lastTotalTokens;
    private int _compactionCount;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public CompactionOptions Options { get; }

    // ── Observable state ─────────────────────────────────────────────────

    public int LastInputTokens => _lastInputTokens;
    public int LastOutputTokens => _lastOutputTokens;
    public int LastTotalTokens => _lastTotalTokens;

    public double ContextUsageRatio => Options.MaxContextTokens > 0
        ? (double)_lastInputTokens / Options.MaxContextTokens : 0;

    public bool ShouldCompact => Options.AutoCompact
        && _lastInputTokens > 0
        && ContextUsageRatio >= Options.CompactionThreshold;

    public Checkpoint? LastCheckpoint => _lastCheckpoint;
    public bool IsCheckpointed => _lastCheckpoint is not null;
    public IReadOnlyList<ConversationEntry> History => _history;
    public int CompactionCount => _compactionCount;
    public IReadOnlyList<string> PinnedConstraints => _pinnedConstraints;

    public ContextManager(CompactionOptions? options = null)
    {
        Options = options ?? new();
    }

    // ── Recording ────────────────────────────────────────────────────────

    public void RecordUserInput(string input) =>
        _history.Add(new() { Role = "user", Content = input });

    public void RecordAssistantResponse(string text) =>
        _history.Add(new() { Role = "assistant", Content = text });

    public void RecordToolCall(string name, string arguments) =>
        _history.Add(new() { Role = "tool_call", Content = arguments, ToolName = name });

    public void RecordToolResult(string name, string result) =>
        _history.Add(new() { Role = "tool_result", Content = result, ToolName = name });

    public void UpdateTokenUsage(int inputTokens, int outputTokens, int totalTokens)
    {
        _lastInputTokens = inputTokens;
        _lastOutputTokens = outputTokens;
        _lastTotalTokens = totalTokens;
    }

    // ── Pinned constraints ───────────────────────────────────────────────

    public void PinConstraint(string constraint)
    {
        if (!_pinnedConstraints.Contains(constraint))
            _pinnedConstraints.Add(constraint);
    }

    public bool UnpinConstraint(string constraint) => _pinnedConstraints.Remove(constraint);

    // ── Compaction ───────────────────────────────────────────────────────

    /// <summary>
    /// Compress older conversation history into a structured checkpoint.
    /// Keeps the most recent <see cref="CompactionOptions.HotTailTurns"/> verbatim.
    /// Uses the LM to generate the summary — this makes an additional API call.
    /// </summary>
    public async Task<Checkpoint?> CompactAsync(LM lm, CompactionLevel? level = null,
        int? targetTokens = null, CancellationToken ct = default)
    {
        var effectiveLevel = level ?? Options.DefaultLevel;

        // Find user-turn boundaries so we can split by turn count
        var turnBoundaries = new List<int>();
        for (int i = 0; i < _history.Count; i++)
            if (_history[i].Role == "user") turnBoundaries.Add(i);

        var tailStartTurn = Math.Max(0, turnBoundaries.Count - Options.HotTailTurns);
        var tailStartIndex = tailStartTurn < turnBoundaries.Count
            ? turnBoundaries[tailStartTurn] : _history.Count;

        var head = _history.Take(tailStartIndex).ToList();
        var tail = _history.Skip(tailStartIndex).ToList();

        // Nothing to compress and no prior checkpoint — nothing to do
        if (head.Count == 0 && _lastCheckpoint is null)
            return null;

        // Build the conversation text to compress
        var conversationText = BuildConversationText(head);
        if (_lastCheckpoint is not null)
            conversationText = $"[Previous Checkpoint]\n{_lastCheckpoint.ToPromptText()}\n\n[Conversation Since Checkpoint]\n{conversationText}";

        var prompt = BuildCompactionPrompt(effectiveLevel, conversationText,
            targetTokens ?? Options.TargetCheckpointTokens);

        var response = await lm.RespondAsync(prompt,
            instructions: CompactionSystemPrompt, ct: ct);

        var responseText = ExtractResponseText(response);
        var checkpoint = ParseCheckpoint(responseText, effectiveLevel);
        checkpoint.CompactionCount = ++_compactionCount;

        // Merge pinned constraints (always survive compaction)
        foreach (var c in _pinnedConstraints)
            if (!checkpoint.Constraints.Contains(c))
                checkpoint.Constraints.Add(c);

        // Replace history with hot tail only
        _history.Clear();
        _history.AddRange(tail);
        _lastCheckpoint = checkpoint;
        _lastInputTokens = 0;

        return checkpoint;
    }

    // ── Input building (post-compaction rehydration) ─────────────────────

    /// <summary>
    /// Returns the effective system prompt: base prompt + checkpoint text (if any).
    /// The checkpoint is injected into instructions so it is always available
    /// to the model regardless of conversation chaining mode.
    /// </summary>
    public string GetEffectiveSystemPrompt(string? basePrompt)
    {
        if (_lastCheckpoint is null) return basePrompt ?? "";
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(basePrompt))
            sb.AppendLine(basePrompt).AppendLine();
        sb.Append(_lastCheckpoint.ToPromptText());
        return sb.ToString();
    }

    /// <summary>
    /// Build the hot tail as a list of <see cref="ResponseInput"/> messages
    /// for replaying recent context after compaction resets the response chain.
    /// Consecutive same-role entries are merged into single messages.
    /// </summary>
    public List<ResponseInput> GetHotTailAsInput()
    {
        var inputs = new List<ResponseInput>();
        var buffer = new StringBuilder();
        string? currentRole = null;

        foreach (var entry in _history)
        {
            var mappedRole = entry.Role is "user" ? "user" : "assistant";

            if (mappedRole != currentRole)
            {
                if (currentRole is not null && buffer.Length > 0)
                    inputs.Add(new ResponseInput { Role = currentRole, Content = buffer.ToString().TrimEnd() });
                buffer.Clear();
                currentRole = mappedRole;
            }

            switch (entry.Role)
            {
                case "user":
                case "assistant":
                    buffer.AppendLine(entry.Content);
                    break;
                case "tool_call":
                    buffer.AppendLine($"[Tool Call: {entry.ToolName}({entry.Content})]");
                    break;
                case "tool_result":
                    buffer.AppendLine($"[Tool Result from {entry.ToolName}: {entry.Content}]");
                    break;
            }
        }

        if (currentRole is not null && buffer.Length > 0)
            inputs.Add(new ResponseInput { Role = currentRole, Content = buffer.ToString().TrimEnd() });

        return inputs;
    }

    // ── Reset ────────────────────────────────────────────────────────────

    public void Reset()
    {
        _history.Clear();
        _lastCheckpoint = null;
        _lastInputTokens = 0;
        _lastOutputTokens = 0;
        _lastTotalTokens = 0;
        _compactionCount = 0;
    }

    // ── Externalize / rehydrate state ────────────────────────────────────

    /// <summary>Serialize the full context manager state to JSON for external storage.</summary>
    public string SerializeState() => JsonSerializer.Serialize(new ContextManagerState
    {
        History = [.. _history],
        PinnedConstraints = [.. _pinnedConstraints],
        LastCheckpoint = _lastCheckpoint,
        CompactionCount = _compactionCount,
        LastInputTokens = _lastInputTokens,
    }, s_json);

    /// <summary>Restore context manager state from a previously serialized JSON string.</summary>
    public void LoadState(string json)
    {
        var state = JsonSerializer.Deserialize<ContextManagerState>(json, s_json);
        if (state is null) return;
        _history.Clear();
        _history.AddRange(state.History);
        _pinnedConstraints.Clear();
        _pinnedConstraints.AddRange(state.PinnedConstraints);
        _lastCheckpoint = state.LastCheckpoint;
        _compactionCount = state.CompactionCount;
        _lastInputTokens = state.LastInputTokens;
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static string BuildConversationText(List<ConversationEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            var label = e.Role switch
            {
                "user"        => "USER",
                "assistant"   => "ASSISTANT",
                "tool_call"   => $"TOOL_CALL [{e.ToolName}]",
                "tool_result" => $"TOOL_RESULT [{e.ToolName}]",
                _             => e.Role.ToUpperInvariant(),
            };
            sb.Append(label).Append(": ").AppendLine(e.Content);
        }
        return sb.ToString();
    }

    private static string BuildCompactionPrompt(CompactionLevel level, string conversation, int targetTokens)
    {
        var levelInstructions = level switch
        {
            CompactionLevel.Light => """
                Focus on brevity. Include only:
                - "objective": the high-level goal
                - "current_status": one-sentence status
                - "next_steps": ordered list of immediate next actions
                - "constraints": any hard constraints mentioned
                Other fields can be empty arrays/strings.
                """,
            CompactionLevel.Detailed => """
                Be thorough. Include ALL of the following with rich detail:
                - "objective": full objective and success criteria
                - "current_status": detailed current state including partial progress
                - "next_steps": ordered list of actions with context
                - "key_decisions": every decision made and its rationale, trade-offs considered
                - "constraints": all constraints, non-goals, and invariants
                - "open_questions": unresolved questions, risks, edge cases, hypotheses
                - "key_artifacts": files changed, commands run, important outputs, URLs referenced
                """,
            _ => """
                Include all sections with reasonable detail:
                - "objective": the goal and success criteria
                - "current_status": what has been accomplished so far
                - "next_steps": ordered list of what to do next
                - "key_decisions": important decisions and why they were made
                - "constraints": constraints and non-goals
                - "open_questions": open questions or risks
                - "key_artifacts": key files, outputs, or references
                """,
        };

        var budget = targetTokens > 0
            ? $"\nTarget your response to approximately {targetTokens} tokens." : "";

        return $$"""
            Compress the following conversation into a structured checkpoint.
            Output ONLY valid JSON matching this schema:
            {
              "objective": "string",
              "current_status": "string",
              "next_steps": ["string"],
              "key_decisions": ["string"],
              "constraints": ["string"],
              "open_questions": ["string"],
              "key_artifacts": ["string"]
            }

            {{levelInstructions}}
            {{budget}}

            === CONVERSATION TO COMPRESS ===
            {{conversation}}
            === END CONVERSATION ===
            """;
    }

    private const string CompactionSystemPrompt =
        "You are a precise context-compression assistant. Output ONLY valid JSON. " +
        "No markdown code fences, no explanations, no extra text. Just the JSON object.";

    private static string ExtractResponseText(ResponseResponse response) =>
        string.Join("", response.Output
            .Where(o => o.Type == "message")
            .SelectMany(o => o.Content ?? [])
            .Where(c => c.Type == "output_text" && c.Text is not null)
            .Select(c => c.Text));

    private static Checkpoint ParseCheckpoint(string text, CompactionLevel level)
    {
        var json = text.Trim();

        // Strip markdown code fences if the model wrapped the output
        if (json.StartsWith("```"))
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start >= 0 && end > start)
                json = json[start..(end + 1)];
        }

        try
        {
            var checkpoint = JsonSerializer.Deserialize<Checkpoint>(json, new JsonSerializerOptions
            { PropertyNameCaseInsensitive = true }) ?? new();
            checkpoint.Level = level;
            checkpoint.CreatedAt = DateTime.UtcNow;
            return checkpoint;
        }
        catch
        {
            // Fallback: treat the entire response as the status
            return new Checkpoint
            {
                CurrentStatus = text,
                Level = level,
                CreatedAt = DateTime.UtcNow,
            };
        }
    }

    private sealed class ContextManagerState
    {
        [JsonPropertyName("history")]
        public List<ConversationEntry> History { get; set; } = [];
        [JsonPropertyName("pinned_constraints")]
        public List<string> PinnedConstraints { get; set; } = [];
        [JsonPropertyName("last_checkpoint")]
        public Checkpoint? LastCheckpoint { get; set; }
        [JsonPropertyName("compaction_count")]
        public int CompactionCount { get; set; }
        [JsonPropertyName("last_input_tokens")]
        public int LastInputTokens { get; set; }
    }
}
