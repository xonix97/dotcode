using System.Text;
using System.Text.Json;
using DotCode.Core.Auth;
using DotCode.Core.Config;
using DotCode.Core.Permissions;
using DotCode.Core.Sessions;
using DotCode.Providers;
using DotCode.Tools;
using Microsoft.Extensions.AI;

namespace DotCode.Agent;

/// <summary>Manual agentic loop: sends history, executes tool calls, repeats up to maxSteps.</summary>
public sealed class AgentService(string workspaceRoot, int maxSteps = 25)
{
    private readonly string _workspaceRoot = workspaceRoot;
    private readonly int _maxSteps = maxSteps;

    /// <summary>Rough per-message character budget before we summarize/trim history.</summary>
    private const int MaxHistoryChars = 120_000;
    /// <summary>Per tool result character cap (huge outputs are persisted, trimmed for the model).</summary>
    private const int MaxToolResultChars = 12_000;

    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
        { "Read", "Glob", "Grep", "TodoRead", "ListDir" };

    public static string BuildSystemPrompt(string agentName, string workspaceRoot)
    {
        var sb = new StringBuilder();
        bool planMode = agentName.Equals("plan", StringComparison.OrdinalIgnoreCase);
        var projectName = new DirectoryInfo(workspaceRoot).Name;

        sb.Append(planMode
            ? """
              You are dotcode's plan agent: a senior engineer who investigates before proposing.

              TASK
              Understand the request, investigate the codebase with your read-only tools, then deliver
              a concrete, actionable plan. Do NOT attempt edits — your tools cannot write.

              METHOD
              1. Restate the goal in one sentence.
              2. Investigate: Read/Glob/Grep the relevant files until you know exactly what would change.
              3. Plan: numbered steps, each with the file(s) involved and the specific change.
              4. Risks: what could break, what to test.

              OUTPUT
              Markdown. Short paragraphs, no filler. End with "### Plan" and the numbered steps.
              """
            : """
              You are dotcode, an autonomous senior software engineer working directly in the user's
              repository via a tool loop. You are meticulous, and you verify your own work.

              OPERATING PRINCIPLES
              - Investigate before you change: read the code involved before editing anything.
              - Smallest correct change: no drive-by refactors, no reformatting untouched code.
              - Match existing conventions: naming, style, framework usage of the surrounding code.
              - Never invent APIs: if unsure a symbol exists, Read/Grep before using it.
              - Be honest about failures: report errors plainly; never claim work you did not do.

              TOOL RULES
              - Batch independent reads/searches in one turn; dependent calls must wait for results.
              - Prefer Grep/Glob to locate code; use Read with offset/limit for large files.
              - Edit requires an EXACT oldString copied from the file, with enough surrounding
                context to be unique. If it fails, re-read the region and retry once, differently.
              - After code changes, verify: run the project's build and relevant tests via Bash.
                Fix what breaks, then report honestly. If you cannot verify, say so.
              - Use TodoWrite for multi-step tasks (3+ steps) and keep it updated.
              - Stay inside the workspace; do not run destructive or irreversible commands.

              FINAL RESPONSE
              When the task is done (or the budget is nearly exhausted), stop calling tools and
              answer with markdown: what you did (bullets, files touched), verification results,
              and anything left undone. No preamble, no apologizing, no restating the prompt.
              """);

        sb.Append("\n\nPROJECT: ").Append(projectName).Append(" (workspace root: ").Append(workspaceRoot).Append(')');
        var agentsMd = Path.Combine(workspaceRoot, "AGENTS.md");
        if (File.Exists(agentsMd))
        {
            try
            {
                var md = File.ReadAllText(agentsMd);
                if (md.Length > 8_000) md = md[..8_000] + "\n… (truncated)";
                sb.Append("\n\nProject instructions (AGENTS.md):\n").Append(md);
            }
            catch { }
        }
        return sb.ToString();
    }

    public async Task<(string AssistantMessageId, string Text)> CompleteAsync(
        string providerId, string modelId, string agentName,
        IReadOnlyList<(string Role, string Text)> history,
        IMessageStore? store = null, string? sessionId = null,
        bool autoApprove = false,
        ITodoStore? todoStore = null,
        double? temperature = null,
        int? maxSteps = null,
        string? instructions = null,
        CancellationToken ct = default)
    {
        var config = ConfigLoader.Load(_workspaceRoot);
        var auth = AuthStore.Load();
        var endpoint = ProviderResolver.Resolve(providerId, config, auth, _workspaceRoot);
        using var client = ChatClientFactory.Create(endpoint, modelId);

        var rules = PermissionParser.Parse(config.Permission);
        var gate = new ToolGate(autoApprove);
        bool planMode = agentName.Equals("plan", StringComparison.OrdinalIgnoreCase);

        var toolset = new DotCodeToolset(new Workspace(_workspaceRoot)).WithSession(todoStore, sessionId);
        var allTools = toolset.AsAITools();
        // Plan mode gets only read-only tools — enforced, not just prompted.
        var tools = planMode
            ? allTools.Where(t => ReadOnlyTools.Contains(t.Name)).ToList()
            : allTools;

        var messages = new List<ChatMessage> { new(ChatRole.System, BuildSystemPrompt(agentName, _workspaceRoot)) };
        foreach (var (role, text) in TrimHistory(history))
        {
            var chatRole = role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? ChatRole.Assistant
                : role.Equals("system", StringComparison.OrdinalIgnoreCase) ? ChatRole.System
                : ChatRole.User;
            messages.Add(new ChatMessage(chatRole, text));
        }
        if (!string.IsNullOrWhiteSpace(instructions))
            messages[0] = new ChatMessage(ChatRole.System,
                messages[0].Text + "\n\nAdditional instructions from the user for this session:\n" + instructions);

        var stepLimit = maxSteps is > 0 ? maxSteps.Value : _maxSteps;
        var options = new ChatOptions
        {
            Tools = [.. tools],
            Temperature = temperature is null ? null : (float)temperature,
        };
        var assistantId = store?.BeginAssistantTurn(sessionId ?? "", agentName, $"{providerId}/{modelId}")
            ?? Guid.NewGuid().ToString("n")[..12];

        try
        {
            var done = false;
            var finalText = "";
            for (int step = 0; step < stepLimit && !done; step++)
            {
                // Last step: forbid new tool calls so the model must produce a final answer.
                if (step == stepLimit - 1)
                {
                    options.Tools = null;
                    messages.Add(new ChatMessage(ChatRole.User,
                        "Budget nearly exhausted. Do not call any more tools. Summarize now: what was done, " +
                        "what was verified, what remains. Be specific and honest."));
                }

                var response = await client.GetResponseAsync(messages, options, ct);
                var calls = response.Messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToList();

                // Persist the model's reasoning text before tool runs so the UI shows it live.
                if (calls.Count > 0 && !string.IsNullOrWhiteSpace(response.Text))
                    store?.AppendAssistantTextPart(assistantId, response.Text);
                messages.AddMessages(response);

                if (calls.Count == 0)
                {
                    finalText = response.Text.Trim();
                    if (!string.IsNullOrEmpty(finalText)) done = true;
                    continue;
                }

                var results = new List<AIContent>();
                foreach (var call in calls)
                {
                    var toolName = call.Name ?? "?";
                    string inputJson;
                    try { inputJson = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>()); }
                    catch { inputJson = "{}"; }
                    var recorded = store?.AppendToolCall(assistantId, toolName, inputJson, call.CallId);

                    var fn = tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == call.Name);
                    if (fn is null)
                    {
                        var err = $"Error: unknown tool '{toolName}'. Available: {string.Join(", ", tools.Select(t => t.Name))}";
                        results.Add(new FunctionResultContent(call.CallId ?? "", err));
                        store?.AppendToolResult(assistantId, recorded?.CallId ?? call.CallId ?? "", toolName, err, true);
                        continue;
                    }

                    // Permission gate: deny/ask per config rules; mutating tools ask by default.
                    if (!await gate.CheckAsync(toolName, inputJson, rules))
                    {
                        var denied = $"Error: permission denied for '{toolName}'. " +
                            (autoApprove ? "" : "Enable auto-approve in the UI or add a permission rule in dotcode.json.");
                        results.Add(new FunctionResultContent(call.CallId ?? "", denied));
                        store?.AppendToolResult(assistantId, recorded?.CallId ?? call.CallId ?? "", toolName, denied, true);
                        continue;
                    }

                    try
                    {
                        var args = new AIFunctionArguments();
                        if (call.Arguments != null)
                            foreach (var kv in call.Arguments) args[kv.Key] = kv.Value;
                        var result = await fn.InvokeAsync(args, ct);
                        var text = result?.ToString() ?? "";
                        var persisted = text;
                        if (text.Length > MaxToolResultChars)
                        {
                            persisted = text[..MaxToolResultChars]
                                + $"\n… (output truncated at {MaxToolResultChars} chars; total {text.Length}. " +
                                  "Use Read with offset/limit or narrower searches to see more.)";
                        }
                        results.Add(new FunctionResultContent(call.CallId ?? "", persisted));
                        store?.AppendToolResult(assistantId, recorded?.CallId ?? call.CallId ?? "", toolName, persisted);
                    }
                    catch (Exception ex)
                    {
                        var err = $"Error: {ex.Message}";
                        results.Add(new FunctionResultContent(call.CallId ?? "", err));
                        store?.AppendToolResult(assistantId, recorded?.CallId ?? call.CallId ?? "", toolName, err, true);
                    }
                }
                messages.Add(new ChatMessage(ChatRole.Tool, results));
            }

            if (string.IsNullOrWhiteSpace(finalText))
            {
                finalText = "Stopped at the step budget mid-task. Based on the tool history above: " +
                            "progress made so far, immediate next steps, and what to verify.";
                try
                {
                    var wrap = await client.GetResponseAsync(messages, new ChatOptions { Tools = null }, ct);
                    if (!string.IsNullOrWhiteSpace(wrap.Text)) finalText = wrap.Text.Trim();
                }
                catch { }
            }
            store?.AppendAssistantTextPart(assistantId, finalText);
            return (assistantId, finalText);
        }
        catch (Exception ex) when (sessionId is not null && store is not null)
        {
            // Provider/transport failure mid-turn: keep everything on the same assistant turn.
            var err = $"Agent error: {ex.Message}";
            store.AppendAssistantTextPart(assistantId, err);
            return (assistantId, err);
        }
    }

    /// <summary>Keeps the prompt within budget: drops oldest middle turns, keeps the first user turn
    /// (the task) and recent turns verbatim; older oversized bodies are elided.</summary>
    private static IReadOnlyList<(string Role, string Text)> TrimHistory(IReadOnlyList<(string Role, string Text)> history)
    {
        if (history.Count == 0) return history;
        static string Elide(string t) => t.Length <= 4_000
            ? t
            : t[..2_000] + "\n… (earlier content elided) …\n" + t[^1_000..];

        var total = history.Sum(h => h.Text?.Length ?? 0);
        if (total <= MaxHistoryChars) return history;

        // Keep first turn and the most recent turns fully; elide the middle.
        var result = new List<(string, string)> { history[0] };
        long kept = history[0].Text?.Length ?? 0;
        int i = history.Count - 1;
        var tail = new Stack<(string, string)>();
        while (i > 0 && kept < MaxHistoryChars / 2)
        {
            var h = history[i];
            kept += h.Text?.Length ?? 0;
            tail.Push(h);
            i--;
        }
        for (int m = 1; m <= i; m++)
            result.Add((history[m].Role, Elide(history[m].Text ?? "")));
        result.AddRange(tail);
        return result;
    }
}
