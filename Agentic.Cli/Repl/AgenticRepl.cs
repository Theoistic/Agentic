using System.Text.Json;

namespace Agentic.Cli;

/// <summary>
/// A reusable console REPL that attaches to an <see cref="Agent"/> as its IO layer.
/// Handles streaming output, tool events, and built-in commands
/// (/reset, /compact, /img) before forwarding plain text to the agent.
/// </summary>
public sealed class AgenticRepl
{
    private static readonly HttpClient s_http = new();

    private readonly Agent  _agent;
    private readonly string? _mcpUrl;

    private bool _inTextStream;
    private bool _atLineStart   = true;
    private bool _hadToolEvents;

    public AgenticRepl(Agent agent, string? mcpUrl = null)
    {
        _agent         = agent;
        _mcpUrl        = mcpUrl;
        agent.Options.OnEvent = HandleEvent;
    }

    // ── Public entry point ────────────────────────────────────────────────

    public async Task RunAsync()
    {
        if (_agent.Context is not null)
            ConsoleHelper.WriteDim(
                $"Context: auto-compact at {_agent.Context.Options.CompactionThreshold:P0} " +
                $"of {_agent.Context.Options.MaxContextTokens:#,0} tokens");

        ConsoleHelper.WriteDim("Commands: exit | quit | /reset | /compact [light|standard|detailed] | /img <url-or-path> [prompt]");

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("\nYou › ");
            Console.ResetColor();

            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input)) continue;

            if (input is "exit" or "quit" or "/exit" or "/quit") break;

            if (input is "/reset")
            {
                _agent.ResetConversation();
                ConsoleHelper.WriteDim("[conversation reset]");
                continue;
            }

            if (input.StartsWith("/compact"))
            {
                var level = input.Contains("light")    ? CompactionLevel.Light
                          : input.Contains("detailed") ? CompactionLevel.Detailed
                          : CompactionLevel.Standard;
                try
                {
                    var cp = await _agent.CompactAsync(level);
                    if (cp is not null)
                        ConsoleHelper.WriteDim(
                            $"[compacted: {cp.Level} · #{cp.CompactionCount} · {cp.Objective[..Math.Min(80, cp.Objective.Length)]}]");
                }
                catch (Exception ex) { ConsoleHelper.Write(ConsoleColor.Red, $"  compact failed: {ex.Message}\n"); }
                continue;
            }

            if (input.StartsWith("/img "))
            {
                var rest   = input["/img ".Length..].Trim();
                var sep    = rest.IndexOf(' ');
                var target = sep < 0 ? rest : rest[..sep];
                var prompt = sep < 0 ? "Describe this image in detail." : rest[(sep + 1)..].Trim();

                Console.WriteLine();
                try
                {
                    var dataUrl = File.Exists(target)
                        ? InputImageContent.FromFile(target).ImageUrl!
                        : await ToDataUrlAsync(target);
                    if (string.IsNullOrWhiteSpace(_mcpUrl))
                        await _agent.ChatStreamAsync(prompt, [dataUrl]);
                    else
                        await _agent.ChatStreamAsync(prompt, [dataUrl], _mcpUrl);
                }
                catch (Exception ex) { ConsoleHelper.Write(ConsoleColor.Red, $"[img error] {ex.Message}\n"); }
                continue;
            }

            Console.WriteLine();
            try
            {
                if (string.IsNullOrWhiteSpace(_mcpUrl))
                    await _agent.ChatStreamAsync(input);
                else
                    await _agent.ChatStreamAsync(input, _mcpUrl);
            }
            catch (LMException ex) when (ex.StatusCode == 0)
            {
                EnsureNewLine();
                ConsoleHelper.Write(ConsoleColor.Red, $"⚡ LM server connection lost — {ex.Message}\n");
            }
            catch (LMException ex)
            {
                EnsureNewLine();
                ConsoleHelper.Write(ConsoleColor.Red, $"[LM error {ex.StatusCode}] {ex.Message}\n");
            }
            catch (Exception ex)
            {
                EnsureNewLine();
                ConsoleHelper.Write(ConsoleColor.Red, $"[error] {ex.Message}\n");
            }
        }
    }

    // ── Event handler ─────────────────────────────────────────────────────

    private void HandleEvent(AgentEvent e)
    {
        switch (e.Kind)
        {
            case AgentEventKind.Reasoning:
                EnsureNewLine();
                Console.ForegroundColor = ConsoleColor.DarkMagenta;
                Console.Write("  ◆ think  ");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine(Truncate(e.Text, 220));
                Console.ResetColor();
                _atLineStart = true;
                break;

            case AgentEventKind.TextDelta:
                if (!_inTextStream)
                {
                    if (_hadToolEvents) { Console.WriteLine(); _atLineStart = true; }
                    _hadToolEvents = false;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("Agent › ");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    _inTextStream = true;
                    _atLineStart  = false;
                }
                Console.Write(e.Text);
                if (!string.IsNullOrEmpty(e.Text))
                    _atLineStart = e.Text[^1] == '\n';
                break;

            case AgentEventKind.ToolCall:
                EnsureNewLine();
                _hadToolEvents = true;
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"  ⚙  {e.ToolName}");
                if (!string.IsNullOrWhiteSpace(e.Arguments))
                {
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.Write($"  {FormatArgs(e.Arguments)}");
                }
                Console.WriteLine();
                Console.ResetColor();
                _atLineStart = true;
                break;

            case AgentEventKind.ToolResult:
                _hadToolEvents = true;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("  ↳  ");
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine(Truncate(e.Text, 300));
                Console.ResetColor();
                _atLineStart = true;
                break;

            case AgentEventKind.Answer:
                EnsureNewLine();
                _hadToolEvents = false;
                if (e.TotalTokens > 0)
                {
                    var maxCtx  = _agent.Context?.Options.MaxContextTokens ?? 0;
                    var ctxInfo = (maxCtx > 0 && e.InputTokens > 0)
                        ? $"  ctx={(double)e.InputTokens.Value / maxCtx:P0}"
                        : "";
                    ConsoleHelper.WriteDim($"  [{e.InputTokens}↑  {e.OutputTokens}↓  {e.TotalTokens} tokens{ctxInfo}]");
                }
                break;

            case AgentEventKind.Compacted:
                EnsureNewLine();
                ConsoleHelper.Write(ConsoleColor.Magenta, $"  ◇ {e.Text}\n");
                _atLineStart = true;
                break;

            case AgentEventKind.StepCompleted:
                EnsureNewLine();
                ConsoleHelper.Write(ConsoleColor.DarkGreen, $"  ✓ step: {e.Text}\n");
                _atLineStart = true;
                break;

            case AgentEventKind.WorkflowCompleted:
                EnsureNewLine();
                ConsoleHelper.Write(ConsoleColor.Green, $"  ✓✓ workflow complete: {e.Text}\n");
                _atLineStart = true;
                break;
        }
    }

    // Closes an in-progress TextDelta run. Only emits \n if not already at line start.
    private void EnsureNewLine()
    {
        if (!_inTextStream) return;
        if (!_atLineStart) Console.WriteLine();
        Console.ResetColor();
        _inTextStream = false;
        _atLineStart  = true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static async Task<string> ToDataUrlAsync(string imageUrl)
    {
        using var r   = await s_http.GetAsync(imageUrl);
        r.EnsureSuccessStatusCode();
        var mime  = r.Content.Headers.ContentType?.MediaType ?? InferMimeFromUrl(imageUrl);
        var bytes = await r.Content.ReadAsByteArrayAsync();
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string InferMimeFromUrl(string url)
    {
        var ext = Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".gif"            => "image/gif",
            ".webp"           => "image/webp",
            _                 => "image/jpeg",
        };
    }

    private static string FormatArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            var doc = JsonDocument.Parse(json);
            return string.Join("  ", doc.RootElement.EnumerateObject()
                .Select(p => $"{p.Name}={p.Value}"));
        }
        catch
        {
            return Truncate(json, 140);
        }
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var single = text.ReplaceLineEndings(" ");
        return single.Length <= max ? single : single[..max] + "…";
    }
}
