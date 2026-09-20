using DotCode.Core.Auth;
using DotCode.Core.Config;
using Microsoft.Data.Sqlite;

namespace DotCode.Providers;

public enum ProviderAuthType { None, ApiKey, OAuth }

public sealed record ProviderCatalogEntry(
    string Id,
    string Name,
    ProviderAuthType Auth,
    string[] Env,
    string? DefaultBaseUrl,
    bool OpenAiCompatible);

public static class ProviderCatalog
{
    public static readonly IReadOnlyList<ProviderCatalogEntry> All = new List<ProviderCatalogEntry>
    {
        new("dotcode-gateway", "DotCode Gateway (zero-config)", ProviderAuthType.OAuth, new[] { "DOTCODE_API_KEY" }, "https://gateway.dotcode.dev/api", true),
        new("openrouter", "OpenRouter", ProviderAuthType.ApiKey, new[] { "OPENROUTER_API_KEY" }, "https://openrouter.ai/api/v1", true),
        new("openai", "OpenAI", ProviderAuthType.ApiKey, new[] { "OPENAI_API_KEY" }, "https://api.openai.com/v1", true),
        new("anthropic", "Anthropic (via gateway)", ProviderAuthType.ApiKey, new[] { "ANTHROPIC_API_KEY" }, null, false),
        new("azure", "Azure OpenAI", ProviderAuthType.ApiKey, new[] { "AZURE_OPENAI_API_KEY" }, null, true),
        new("ollama", "Ollama (local)", ProviderAuthType.None, Array.Empty<string>(), "http://localhost:11434/v1", true),
        new("lmstudio", "LM Studio (local)", ProviderAuthType.None, Array.Empty<string>(), "http://localhost:1234/v1", true),
        new("google", "Google Gemini (AI Studio, free tier)", ProviderAuthType.ApiKey, new[] { "GOOGLE_API_KEY", "GEMINI_API_KEY" }, "https://generativelanguage.googleapis.com/v1beta/openai", true),
        new("antigravity", "Google Antigravity (Gemini, sign in with Google)", ProviderAuthType.OAuth, new[] { "GOOGLE_API_KEY", "GEMINI_API_KEY" }, "https://generativelanguage.googleapis.com/v1beta/openai", true),
        new("groq", "Groq (free tier)", ProviderAuthType.ApiKey, new[] { "GROQ_API_KEY" }, "https://api.groq.com/openai/v1", true),
        new("mistral", "Mistral (free tier)", ProviderAuthType.ApiKey, new[] { "MISTRAL_API_KEY" }, "https://api.mistral.ai/v1", true),
        new("cerebras", "Cerebras (free tier)", ProviderAuthType.ApiKey, new[] { "CEREBRAS_API_KEY" }, "https://api.cerebras.ai/v1", true),
        new("github-models", "GitHub Models (free with GitHub account)", ProviderAuthType.ApiKey, new[] { "GITHUB_TOKEN", "GITHUB_MODELS_TOKEN" }, "https://models.github.ai/inference", true),
        new("9router", "9router (local router — Gemini, Claude, Kimi, GLM)", ProviderAuthType.ApiKey, new[] { "NINE_ROUTER_API_KEY", "NINEROUTER_API_KEY" }, "http://localhost:20128/v1", true),
        new("github-copilot", "GitHub Copilot (OAuth)", ProviderAuthType.OAuth, new[] { "GITHUB_TOKEN" }, null, false),
    };

    public static ProviderCatalogEntry? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed record ResolvedEndpoint(string ProviderId, string BaseUrl, string? ApiKey);

public sealed class ProviderNotConnectedException(string providerId, string hint)
    : Exception($"Provider '{providerId}' is not connected. {hint}")
{
    public string ProviderId { get; } = providerId;
}

public static class ProviderResolver
{
    public static ResolvedEndpoint Resolve(string providerId, DotCodeConfig config, AuthStore auth, string projectRoot)
    {
        var entry = ProviderCatalog.Find(providerId)
            ?? throw new ProviderNotConnectedException(providerId, "Unknown provider. Use dotcode auth list to see available providers.");

        // 1. Explicit per-provider config options (baseURL / apiKey with {env:}/{file:} expansion)
        string? cfgBaseUrl = null;
        string? cfgApiKey = null;
        if (config.Provider?.TryGetValue(providerId, out var raw) == true && raw is System.Text.Json.JsonElement el && el.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (el.TryGetProperty("options", out var opts) && opts.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (opts.TryGetProperty("baseURL", out var b)) cfgBaseUrl = ConfigLoader.ExpandVars(b.GetString() ?? "", projectRoot);
                if (opts.TryGetProperty("apiKey", out var k)) cfgApiKey = ConfigLoader.ExpandVars(k.GetString() ?? "", projectRoot);
            }
        }

        // 2. Gateway: JWT from auth store, baseURL from config or default
        if (providerId.Equals("dotcode-gateway", StringComparison.OrdinalIgnoreCase))
        {
            var jwt = auth.GatewayJwt ?? Environment.GetEnvironmentVariable("DOTCODE_API_KEY");
            if (string.IsNullOrWhiteSpace(jwt))
                throw new ProviderNotConnectedException(providerId, "Run `dotcode auth login` or set DOTCODE_API_KEY.");
            var baseUrl = cfgBaseUrl
                ?? config.Gateway?.BaseUrl
                ?? entry.DefaultBaseUrl!;
            return new ResolvedEndpoint(providerId, baseUrl, jwt);
        }

        var apiKey = cfgApiKey ?? auth.ResolveProviderKey(providerId, entry.Env);
        // 9router runs locally with its own generated key; read it straight from its db so no manual setup is needed.
        if (providerId.Equals("9router", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(apiKey))
            apiKey = TryReadNineRouterKey();
        var baseUrl2 = cfgBaseUrl ?? entry.DefaultBaseUrl;

        if (entry.Auth == ProviderAuthType.None)
        {
            // Local providers need no key; placeholder key keeps OpenAI SDK happy.
            return new ResolvedEndpoint(providerId, baseUrl2 ?? "http://localhost:11434/v1", apiKey ?? "ollama");
        }

        if (!entry.OpenAiCompatible)
            throw new ProviderNotConnectedException(providerId,
                entry.Id == "anthropic"
                    ? "Native Anthropic API is not implemented yet — use dotcode-gateway or openrouter for Claude models."
                    : "OAuth flow is not implemented yet — use a BYOK key for this provider.");

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ProviderNotConnectedException(providerId,
                $"Set {string.Join(" or ", entry.Env)} or run `dotcode auth set-key {providerId} <key>`.");
        if (string.IsNullOrWhiteSpace(baseUrl2))
            throw new ProviderNotConnectedException(providerId,
                $"Configure options.baseURL for '{providerId}' in dotcode.json.");

        return new ResolvedEndpoint(providerId, baseUrl2, apiKey);
    }

    /// <summary>Read the active API key from a local 9router install (~/.9router/db/data.sqlite), if present.</summary>
    private static string? TryReadNineRouterKey()
    {
        try
        {
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".9router", "db", "data.sqlite");
            if (!File.Exists(dbPath)) return null;
            var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
            using var con = new SqliteConnection(cs);
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT key FROM apiKeys WHERE isActive = 1 ORDER BY createdAt DESC LIMIT 1";
            return cmd.ExecuteScalar() as string;
        }
        catch { return null; }
    }
}
