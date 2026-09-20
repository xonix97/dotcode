using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCode.Core.Config;

public sealed record GatewayConfig(string? BaseUrl = null, string Auth = "oauth-jwt");
public sealed record DotCodeConfig(
    string? Model = null,
    string? SmallModel = null,
    string? DefaultAgent = "build",
    int SubagentDepth = 1,
    GatewayConfig? Gateway = null,
    Dictionary<string, object>? Provider = null,
    Dictionary<string, object>? Agent = null,
    object? Permission = null,
    object? Mcp = null,
    bool Snapshot = true,
    object? Autoupdate = null,
    string Share = "manual");

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static DotCodeConfig Load(string projectRoot)
    {
        // Precedence: global ~/.config/dotcode/dotcode.json[c] < DOTCODE_CONFIG < project dotcode.json[c] (opencode.json fallback)
        var merged = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in CandidatePaths(projectRoot))
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var prop in doc.RootElement.EnumerateObject())
                    merged[prop.Name] = prop.Value.Clone();
            }
            catch { /* ignore malformed config, keep going */ }
        }
        if (merged.Count == 0) return new DotCodeConfig(Gateway: new GatewayConfig(BaseUrl: "https://gateway.dotcode.dev/api"));
        var raw = JsonSerializer.Serialize(merged, JsonOpts);
        var cfg = JsonSerializer.Deserialize<DotCodeConfig>(raw, JsonOpts) ?? new DotCodeConfig();
        return cfg with { Gateway = cfg.Gateway ?? new GatewayConfig(BaseUrl: "https://gateway.dotcode.dev/api") };
    }

    private static IEnumerable<string> CandidatePaths(string projectRoot)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".config", "dotcode", "dotcode.json");
        yield return Path.Combine(home, ".config", "dotcode", "dotcode.jsonc");
        var custom = Environment.GetEnvironmentVariable("DOTCODE_CONFIG");
        if (!string.IsNullOrWhiteSpace(custom)) yield return custom;
        // opencode compat fallback checked after dotcode names
        yield return Path.Combine(projectRoot, "dotcode.json");
        yield return Path.Combine(projectRoot, "dotcode.jsonc");
        yield return Path.Combine(projectRoot, "opencode.json");
        yield return Path.Combine(projectRoot, "opencode.jsonc");
    }

    public static string ExpandVars(string value, string configDir)
    {
        // {env:VAR} and {file:path} expansion
        if (value.StartsWith("{env:") && value.EndsWith("}"))
            return Environment.GetEnvironmentVariable(value[5..^1]) ?? "";
        if (value.StartsWith("{file:") && value.EndsWith("}"))
        {
            var p = value[6..^1];
            if (p.StartsWith("~")) p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[1..].TrimStart('/'));
            else if (!Path.IsPathRooted(p)) p = Path.Combine(configDir, p);
            return File.Exists(p) ? File.ReadAllText(p).Trim() : "";
        }
        return value;
    }
}
