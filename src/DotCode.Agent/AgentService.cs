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

    public static string BuildSystemPrompt(string agentName, string workspaceRoot)
    {
        var basePrompt = agentName.Equals("plan", StringComparison.OrdinalIgnoreCase)
            ? "You are dotcode's plan agent. Analyze, explain and propose changes. Do NOT call edit/write tools; read-only tools and bash (read-only commands) are fine."
            : "You are dotcode, an AI coding agent in a Blazor/.NET port of OpenCode. Use the available tools to read, search, run and edit code. Keep responses concise.";
        var agentsMd = Path.Combine(workspaceRoot, "AGENTS.md");
        if (File.Exists(agentsMd))
        {
            try { return basePrompt + "\n\nProject instructions (AGENTS.md):\n" + File.ReadAllText(agentsMd); }
            catch { }
        }
        return basePrompt;
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
        var tools = new DotCodeToolset(new Workspace(_workspaceRoot))
            .WithSession(todoStore, sessionId)
            .AsAITools();
        var messages = new List<ChatMessage> { new(ChatRole.System, BuildSystemPrompt(agentName, _workspaceRoot)) };
        foreach (var (role, text) in history)
        {
            var chatRole = role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? ChatRole.Assistant
                : role.Equals("system", StringComparison.OrdinalIgnoreCase) ? ChatRole.System
                : ChatRole.User;
            messages.Add(new ChatMessage(chatRole, text));
        }
        if (!string.IsNullOrWhiteSpace(instructions))
            messages[0] = new ChatMessage(ChatRole.System,
                messages[0].Text + "\n\nAdditional instructions from the user:\n" + instructions);
        var stepLimit = maxSteps is > 0 ? maxSteps.Value : _maxSteps;
        var options = new ChatOptions { Tools = [.. tools], Temperature = temperature is null ? null : (float)temperature };
        var assistantId = store?.BeginAssistantTurn(sessionId ?? "", agentName, $"{providerId}/{modelId}")
            ?? Guid.NewGuid().ToString("n")[..12];

        try
        {
            for (int step = 0; step < stepLimit; step++)
            {
            var response = await client.GetResponseAsync(messages, options, ct);
            var calls = response.Messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToList();
            // Persist the model's reasoning text before tool runs so the UI shows it live.
            if (calls.Count > 0 && !string.IsNullOrWhiteSpace(response.Text))
                store?.AppendAssistantTextPart(assistantId, response.Text);
            messages.AddMessages(response);
            if (calls.Count == 0)
            {
                var text = response.Text.Trim();
                store?.AppendAssistantTextPart(assistantId, text);
                return (assistantId, text);
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
                    var err = $"Error: unknown tool '{toolName}'.";
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
                    results.Add(new FunctionResultContent(call.CallId ?? "", result));
                    store?.AppendToolResult(assistantId, recorded?.CallId ?? call.CallId ?? "", toolName, text);
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
            var stopped = "Stopped after max steps. Summarize progress and suggest remaining work based on the tool history above.";
            store?.AppendAssistantTextPart(assistantId, stopped);
            return (assistantId, stopped);
        }
        catch (Exception ex) when (sessionId is not null && store is not null)
        {
            // Provider/transport failure mid-turn: keep everything on the same assistant turn.
            var err = $"Agent error: {ex.Message}";
            store.AppendAssistantTextPart(assistantId, err);
            return (assistantId, err);
        }
    }
}
