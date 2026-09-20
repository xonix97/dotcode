using DotCode.Core.Config;

namespace DotCode.Core.Permissions;

public enum PermissionAction { Allow, Ask, Deny }

/// <summary>Flattened permission rule from config, in declaration order (last match wins).</summary>
public sealed record PermissionRule(string Pattern, PermissionAction Action);

/// <summary>Parses the dotcode.json `permission` section into ordered rules (opencode semantics).</summary>
public static class PermissionParser
{
    /// <summary>
    /// Accepts either the simple form
    /// { "edit": "allow", "bash": "ask", "*": "deny" } (values: allow|ask|deny)
    /// or the opencode form
    /// { "bash": { "*": "allow", "git push": "ask" } } / { "edit": ["~/.env", { "*": "allow", "~/work/**": "ask" }] }.
    /// Rules are emitted least → most specific (global "*" < tool-wide < tool+pattern), so
    /// PermissionEngine's last-match-wins resolves to the most specific matching rule.
    /// </summary>
    public static IReadOnlyList<PermissionRule> Parse(object? permission)
    {
        var rules = new List<PermissionRule>();
        if (permission is not System.Text.Json.JsonElement el || el.ValueKind != System.Text.Json.JsonValueKind.Object)
            return rules;

        foreach (var key in el.EnumerateObject())
            if (key.Name == "*")
                CollectStringRules(key, globalKey: true, rules);

        foreach (var key in el.EnumerateObject())
            if (key.Name != "*")
                CollectStringRules(key, globalKey: false, rules);

        foreach (var key in el.EnumerateObject())
            if (key.Name != "*")
                CollectPatternRules(key, wildcardOnly: true, rules);

        foreach (var key in el.EnumerateObject())
            if (key.Name != "*")
                CollectPatternRules(key, wildcardOnly: false, rules);

        return rules;
    }

    /// <summary>Whole-key rules like "bash": "ask" (or arrays containing bare actions).</summary>
    private static void CollectStringRules(System.Text.Json.JsonProperty key, bool globalKey, List<PermissionRule> rules)
    {
        void Add(string action)
        {
            var tool = globalKey ? "*" : key.Name;
            rules.Add(new PermissionRule($"{tool}:*", ParseAction(action)));
        }

        switch (key.Value.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
                Add(key.Value.GetString() ?? "");
                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in key.Value.EnumerateArray())
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                        Add(item.GetString() ?? "");
                break;
        }
    }

    /// <summary>Pattern rules like "bash": { "git *": "allow" } or arrays of pattern objects.</summary>
    private static void CollectPatternRules(System.Text.Json.JsonProperty key, bool wildcardOnly, List<PermissionRule> rules)
    {
        void AddObject(System.Text.Json.JsonElement obj)
        {
            foreach (var pat in obj.EnumerateObject())
            {
                var isWildcard = pat.Name == "*";
                if (wildcardOnly != isWildcard) continue;
                if (pat.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    rules.Add(new PermissionRule($"{key.Name}:{pat.Name}", ParseAction(pat.Value.GetString() ?? "")));
            }
        }

        if (key.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            AddObject(key.Value);
        else if (key.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var item in key.Value.EnumerateArray())
                if (item.ValueKind == System.Text.Json.JsonValueKind.Object)
                    AddObject(item);
    }

    private static PermissionAction ParseAction(string value) => value.ToLowerInvariant() switch
    {
        "allow" => PermissionAction.Allow,
        "deny" => PermissionAction.Deny,
        _ => PermissionAction.Ask,
    };
}

public static class PermissionEngine
{
    public static PermissionAction Resolve(IReadOnlyList<(string Pattern, PermissionAction Action)> rules, string input)
    {
        // Last matching rule wins (opencode semantics). No rules => Allow (except callers override defaults).
        PermissionAction? result = null;
        foreach (var (pattern, action) in rules)
        {
            if (Matches(pattern, input))
                result = action;
        }
        return result ?? PermissionAction.Allow;
    }

    public static PermissionAction Resolve(IReadOnlyList<PermissionRule> rules, string input)
        => Resolve(rules.Select(r => (r.Pattern, r.Action)).ToList(), input);

    public static bool Matches(string pattern, string input)
    {
        // Simple wildcards: * = any sequence, ? = single char. Case-sensitive like opencode.
        if (pattern == "*") return true;
        return WildcardMatch(input, pattern);
    }

    private static bool WildcardMatch(string text, string pattern)
    {
        int ti = 0, pi = 0, star = -1, match = 0;
        while (ti < text.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' || pattern[pi] == text[ti]))
            {
                ti++; pi++;
            }
            else if (pi < pattern.Length && pattern[pi] == '*')
            {
                star = pi++;
                match = ti;
            }
            else if (star != -1)
            {
                pi = star + 1;
                ti = ++match;
            }
            else return false;
        }
        while (pi < pattern.Length && pattern[pi] == '*') pi++;
        return pi == pattern.Length;
    }

    public static string ExpandHome(string pattern)
    {
        if (pattern.StartsWith("~/") || pattern == "~")
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), pattern.Length > 1 ? pattern[2..] : "");
        if (pattern.StartsWith("$HOME/", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), pattern[6..]);
        return pattern;
    }
}

/// <summary>
/// Gates tool executions. Default posture is Ask for mutating tools (edit/write/bash),
/// Allow for read-only tools — so a fresh install with no config stays safe.
/// </summary>
public sealed class ToolGate(bool autoApprove = false)
{
    private readonly bool _autoApprove = autoApprove;
    private readonly Func<string, Task<bool>>? _prompt;

    public ToolGate(bool autoApprove, Func<string, Task<bool>>? prompt) : this(autoApprove)
    {
        _prompt = prompt;
    }

    public static bool IsReadOnly(string toolName) => toolName.ToLowerInvariant() switch
    {
        "read" or "glob" or "grep" or "todowrite" or "todoread" => true,
        _ => false,
    };

    /// <summary>Returns true when execution is permitted.</summary>
    public async Task<bool> CheckAsync(string toolName, string inputJson, IReadOnlyList<PermissionRule>? rules)
    {
        if (_autoApprove) return true;
        if (IsReadOnly(toolName)) return true;

        // Patterns match against "tool:primary-value" (command/path/pattern), not raw JSON.
        var primary = ExtractPrimary(inputJson);
        var input = $"{toolName}:{primary}";
        var decision = PermissionEngine.Resolve(rules ?? [], input);

        // No rule matched: default posture — mutating tools need approval.
        var matched = (rules ?? []).Any(r => PermissionEngine.Matches(r.Pattern, input));

        if (decision == PermissionAction.Deny) return false;
        if (decision == PermissionAction.Allow && matched) return true;

        if (_prompt is not null) return await _prompt($"{toolName} {Summarize(inputJson)}");
        return false;
    }

    private static string ExtractPrimary(string inputJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(inputJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var name in new[] { "command", "path", "pattern" })
                    if (doc.RootElement.TryGetProperty(name, out var v) && v.GetString() is { } s)
                        return s;
        }
        catch { }
        return inputJson;
    }

    private static string Summarize(string inputJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(inputJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var name in new[] { "path", "pattern", "command" })
                    if (doc.RootElement.TryGetProperty(name, out var v) && v.GetString() is { } s)
                        return s.Length > 60 ? s[..60] + "…" : s;
            }
        }
        catch { }
        return inputJson.Length > 60 ? inputJson[..60] + "…" : inputJson;
    }
}
