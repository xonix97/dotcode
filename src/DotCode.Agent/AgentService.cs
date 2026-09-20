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
        { "Read", "Glob", "Grep", "TodoRead", "Tree" };

    public static string BuildSystemPrompt(string agentName, string workspaceRoot)
    {
        var sb = new StringBuilder();
        bool planMode = agentName.Equals("plan", StringComparison.OrdinalIgnoreCase);
        var projectName = new DirectoryInfo(workspaceRoot).Name;

        // ---- Core prompt: follows the OpenCode system prompt structure closely. ----
        sb.Append("""
                  You are dotcode, a coding agent that helps the user with software engineering tasks in this repository. Use the instructions below and the tools available to you.

                  # Tone and style
                  You are concise, direct, and to the point. When you run a non-trivial command, explain in one short line what it does and why. Output is rendered as GitHub-flavored markdown in a terminal UI. Text you output outside of tool calls is shown to the user; only use tools to do work, never to communicate.
                  If you cannot or will not do something, say so plainly in 1-2 sentences and offer an alternative; do not lecture.
                  IMPORTANT: minimize output tokens. Answer the specific query only. If you can answer in 1-3 sentences, do. No preamble ("Sure!", "Great question"), no postamble ("Let me know if..."), no restating the task, no explanations of your own code unless asked. After finishing a file change, stop — do not summarize what you did unless it is non-obvious or the task was complex.
                  Never use emojis unless the user uses them first.

                  # Proactiveness
                  Do what was asked, including obvious follow-up steps (e.g. run tests after a fix). Do not surprise the user with unrequested actions. If the user asks "how do I..." answer the question — do not start editing files. NEVER commit, push, or amend git state unless explicitly asked.

                  # Following conventions
                  When changing files, first understand the file's conventions. Mimic style, use existing libraries and utilities, follow existing patterns.
                  - NEVER assume a library is available. Check that the codebase already uses it (neighboring files, package.json, csproj, go.mod...) before importing it.
                  - When creating a new component/module, first look at existing ones: naming, typing, framework choices.
                  - When editing, look at surrounding context (especially imports) and make the most idiomatic change.
                  - Follow security best practices. Never introduce code that exposes or logs secrets. Never commit secrets.
                  - DO NOT ADD COMMENTS to code you write or edit unless asked, or unless the file's convention is heavily commented.

                  # Doing tasks
                  - Use search tools (Glob/Grep) to understand the codebase and the user's query first. Batch independent searches in one turn.
                  - Implement with the available tools. Prefer Edit on existing files; only Write genuinely new files.
                  - Verify the solution: NEVER assume the test framework or command — check README, AGENTS.md, or the project files to determine how to run tests/build, then run them. Fix what breaks.
                  - Before editing, think about what the code you're touching is supposed to do based on names and structure.
                  - For multi-step work (3+ steps), use TodoWrite and keep it updated as you go.
                  - Reference code as file_path:line_number when discussing specific functions.

                  # Tool usage policy
                  - Batch independent tool calls in a single message (e.g. two Glob searches, or git status + git diff).
                  - File search: Glob (not `find`/`ls`). Content search: Grep (not `grep`/`rg` in bash). Reading: Read (not `cat`). Editing: Edit/Write (not `sed`/`echo >`). Never use bash for file operations when a tool exists.
                  - Prefer Edit with exact oldString from a fresh Read; include enough surrounding lines to be unique. Read the file (or region) before editing — stale edits fail.
                  - Bash is for terminal work (git, npm, dotnet, python, docker...). Chain dependent commands with &&; separate independent ones into parallel calls.
                  """);

        if (planMode)
        {
            sb.Append("""

                      # Plan mode
                      You are in PLAN mode. You cannot create or modify files — write/edit/bash-mutating tools are unavailable. Investigate with Read/Glob/Grep/Tree, then deliver:
                      1. One-sentence understanding of the goal.
                      2. What you found (files + file:line references).
                      3. `### Plan` — numbered, concrete steps: file(s), what changes in each, and how to verify.
                      4. Risks / open questions.
                      Keep it tight. No code dumps unless asked.
                      """);
        }
        else
        {
            sb.Append("""

                      # Git and GitHub
                      - Only commit/amend/push/create PRs when explicitly asked.
                      - Before committing: inspect `git status`, `git diff`, `git log --oneline -10`; stage only intended files; never commit secrets; match the repo's commit style.
                      - No `--no-verify`, no interactive rebase, no force-push, no config changes, no empty commits unless asked.
                      - If hooks reject a commit, fix the issue and make a NEW commit; do not amend.
                      - Use `gh` for GitHub work (PRs, issues, checks) when available.
                      """);
        }

        sb.Append("\n\nPROJECT: ").Append(projectName).Append(" (workspace root: ").Append(workspaceRoot).Append(')');
        sb.Append("\nEnvironment: shell is ").Append(OperatingSystem.IsWindows() ? "PowerShell (Windows)" : "bash (Unix)")
          .Append(". File paths in tools are relative to the workspace root.");
        var agentsMd = Path.Combine(workspaceRoot, "AGENTS.md");
        if (File.Exists(agentsMd))
        {
            try
            {
                var md = File.ReadAllText(agentsMd);
                if (md.Length > 8_000) md = md[..8_000] + "\n… (truncated)";
                sb.Append("\n\n# Project instructions (AGENTS.md)\n").Append(md);
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
        toolset.BeginTurn(); // reset read-before-edit tracking per turn
        var allTools = toolset.AsAITools();
        // Plan mode gets only read-only tools — enforced, not just prompted.
        var tools = planMode
            ? allTools.Where(t => ReadOnlyTools.Contains(t.Name)).ToList()
            : allTools;
        var toolMap = tools.OfType<AIFunction>().ToDictionary(t => t.Name, t => t);

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
                messages[0].Text + "\n\nAdditional instructions from the user for this session (highest priority):\n" + instructions);

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
            var finalText = "";
            for (int step = 0; step < stepLimit; step++)
            {
                // Last step: forbid new tool calls so the model must produce a final answer.
                var stepOptions = step == stepLimit - 1 ? new ChatOptions { Temperature = options.Temperature } : options;
                if (step == stepLimit - 1)
                {
                    messages.Add(new ChatMessage(ChatRole.User,
                        "Step budget nearly exhausted. Wrap up NOW without tools: state what changed (files), what was verified, what remains. Be brief."));
                }

                var response = await GetResponseWithRetryAsync(client, messages, stepOptions, ct);
                var calls = response.Messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToList();

                // Persist the model's reasoning text before tool runs so the UI shows it live.
                if (calls.Count > 0 && !string.IsNullOrWhiteSpace(response.Text))
                    store?.AppendAssistantTextPart(assistantId, response.Text);
                messages.AddMessages(response);

                if (calls.Count == 0)
                {
                    finalText = response.Text.Trim();
                    if (!string.IsNullOrEmpty(finalText)) break;
                    continue;
                }

                // Execute independent tool calls in parallel (OpenCode-style batching).
                var callList = calls.ToList();
                var results = new AIContent[callList.Count];
                await Task.WhenAll(callList.Select(async (call, idx) =>
                {
                    var toolName = call.Name ?? "?";
                    string inputJson;
                    try { inputJson = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>()); }
                    catch { inputJson = "{}"; }
                    var recorded = store?.AppendToolCall(assistantId, toolName, inputJson, call.CallId);

                    if (!toolMap.TryGetValue(toolName, out var fn))
                    {
                        var err = $"Error: unknown tool '{toolName}'. Available: {string.Join(", ", toolMap.Keys.OrderBy(k => k))}";
                        results[idx] = new FunctionResultContent(call.CallId ?? "", err);
                        store?.AppendToolResult(assistantId, recorded?.CallId ?? call.CallId ?? "", toolName, err, true);
                        return;
                    }

                    // Permission gate: deny/ask per config rules; mutating tools ask by default.
                    if (!await gate.CheckAsync(toolName, inputJson, rules))
                    {
                        var denied = $"Error: permission denied for '{toolName}'. " +
                            (autoApprove ? "" : "Enable auto-approve in the UI or add a permission rule in dotcode.json.");
                        results[idx] = new FunctionResultContent(call.CallId ?? "", denied);
                        store?.AppendToolResult(assistantId, recorded?.CallId ?? call.CallId ?? "", toolName, denied, true);
                        return;
                    }

                    try
                    {
                        var args = new AIFunctionArguments();
                        if (call.Arguments != null)
                            foreach (var kv in call.Arguments) args[kv.Key] = kv.Value;
                        var result = await fn.InvokeAsync(args, ct);
                        var text = result?.ToString() ?? "";
                        var persisted = TruncateToolOutput(text);
                        results[idx] = new FunctionResultContent(call.CallId ?? "", persisted);
                        store?.AppendToolResult(assistantId, recorded?.CallId ?? call.CallId ?? "", toolName, persisted);
                    }
                    catch (Exception ex)
                    {
                        var err = $"Error: {ex.Message}";
                        results[idx] = new FunctionResultContent(call.CallId ?? "", err);
                        store?.AppendToolResult(assistantId, recorded?.CallId ?? call.CallId ?? "", toolName, err, true);
                    }
                })).WaitAsync(ct);

                var toolMsg = new ChatMessage { Role = ChatRole.Tool };
                foreach (var r in results)
                    if (r is not null) toolMsg.Contents.Add(r);
                messages.Add(toolMsg);
            }

            if (string.IsNullOrWhiteSpace(finalText))
            {
                finalText = "Stopped at the step budget mid-task. Based on the tool history above: " +
                            "progress made so far, immediate next steps, and what to verify.";
                try
                {
                    var wrap = await GetResponseWithRetryAsync(client, messages, new ChatOptions(), ct);
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

    private static string TruncateToolOutput(string text)
    {
        if (text.Length <= MaxToolResultChars) return text;
        return text[..MaxToolResultChars]
            + $"\n… (truncated at {MaxToolResultChars} of {text.Length} chars. Use Read with offset/limit or a narrower Grep for more.)";
    }

    /// <summary>GetResponseAsync with exponential backoff (0.5s, 1s, 2s) on transient errors.</summary>
    private static async Task<ChatResponse> GetResponseWithRetryAsync(
        IChatClient client, List<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await client.GetResponseAsync(messages, options, ct);
            }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt)), ct);
            }
        }
    }

    /// <summary>Transient failures worth retrying: network/timeout/429/5xx-shaped messages.</summary>
    private static bool IsTransient(Exception ex)
    {
        var msg = ex.Message;
        return ex is TaskCanceledException or TimeoutException or IOException
            || msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("ECONNRESET", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("socket", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("429", StringComparison.Ordinal)
            || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("502", StringComparison.Ordinal)
            || msg.Contains("503", StringComparison.Ordinal)
            || msg.Contains("504", StringComparison.Ordinal);
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
