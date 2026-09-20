using System.Collections.Concurrent;
using System.Text.Json;
using DotCode.Agent;
using DotCode.Core.Auth;
using DotCode.Core.Config;
using DotCode.Core.Models;
using DotCode.Core.Sessions;
using DotCode.Providers;
using DotCode.Tools;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton<ISessionStore>(_ => new FileSessionStore());
builder.Services.AddSingleton<IMessageStore>(_ => new FileMessageStore());
builder.Services.AddSingleton<ITodoStore>(_ => new FileTodoStore());
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(4096));
var app = builder.Build();
app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();

var workspace = new WorkspaceState(Directory.GetCurrentDirectory());
Project CurrentProject() => new("proj-local", new DirectoryInfo(workspace.Root).Name, workspace.Root);

// ---- Event bus: store mutations -> SSE /event (opencode-style live updates) ----
var eventChannel = System.Threading.Channels.Channel.CreateUnbounded<StoreEvent>();
MessageStoreBase.StoreChanged += (sessionId, partId) =>
    eventChannel.Writer.TryWrite(new StoreEvent("message.part_updated", sessionId, DateTimeOffset.UtcNow));

// ---- Global ----
app.MapGet("/global/health", () => Results.Ok(new { healthy = true, version = "0.1.0-dotcode" }));
app.MapGet("/global/event", async (HttpContext ctx) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    await ctx.Response.WriteAsync("event: server.connected\ndata: {}\n\n");
    await ctx.Response.Body.FlushAsync();
    var reader = eventChannel.Reader;
    try
    {
        await foreach (var ev in reader.ReadAllAsync(ctx.RequestAborted))
            await ctx.Response.WriteAsync($"event: {ev.Type}\ndata: {{\"sessionID\":\"{ev.SessionId}\"}}\n\n", ctx.RequestAborted);
    }
    catch (OperationCanceledException) { }
});

// ---- Project / Path ----
app.MapGet("/project", () => Results.Ok(new[] { CurrentProject() }));
app.MapGet("/project/current", () => Results.Ok(CurrentProject()));
app.MapGet("/path", () => Results.Ok(new { root = workspace.Root }));
app.MapGet("/vcs", () => Results.Ok(new { branch = GitBranch(workspace.Root), dirty = false }));
app.MapGet("/project/fs", (string? path) =>
{
    string target;
    try
    {
        if (string.IsNullOrWhiteSpace(path)) target = workspace.Root;
        else
        {
            var candidate = Path.IsPathRooted(path) ? path : Path.Combine(workspace.Root, path);
            target = Path.GetFullPath(candidate);
        }
    }
    catch { return Results.BadRequest(new { error = "invalid path" }); }
    if (!Directory.Exists(target)) return Results.NotFound(new { error = "not a directory" });
    List<object> dirs;
    try
    {
        dirs = Directory.GetDirectories(target)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => (object)new { name = Path.GetFileName(d), path = d })
            .ToList();
    }
    catch { dirs = new(); }
    return Results.Ok(new { path = target, parent = Directory.GetParent(target)?.FullName, dirs });
});
app.MapPost("/project/select", (JsonElement body) =>
{
    var path = body.TryGetProperty("path", out var p) ? p.GetString() : null;
    if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });
    try
    {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspace.Root, path));
        if (!Directory.Exists(full)) return Results.NotFound(new { error = $"directory not found: {full}" });
        workspace.SetRoot(full);
        return Results.Ok(new { root = full, name = new DirectoryInfo(full).Name });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// ---- Config ----
app.MapGet("/config", () =>
{
    var cfg = ConfigLoader.Load(workspace.Root);
    return Results.Ok(cfg);
});
app.MapGet("/config/providers", () => Results.Ok(new
{
    providers = BuiltinProviders.All,
    @default = new Dictionary<string, string> { ["openai"] = "gpt-4o-mini", ["anthropic"] = "claude-sonnet-4-5", ["ollama"] = "YuriiFominYoung/fable-5:latest" }
}));

// ---- Provider / Auth (Kilo-style connection hub, opencode-compatible routes) ----
app.MapGet("/provider", () =>
{
    var auth = AuthStore.Load();
    var connected = new List<string>();
    if (!string.IsNullOrWhiteSpace(auth.GatewayJwt)) connected.Add("dotcode-gateway");
    foreach (var p in BuiltinProviders.All)
        if (!string.IsNullOrWhiteSpace(auth.ResolveProviderKey(p.Id, p.Env)))
            connected.Add(p.Id);
    return Results.Ok(new
    {
        all = BuiltinProviders.All,
        @default = new Dictionary<string, string>(),
        connected,
        keyUrls = BuiltinProviders.All.ToDictionary(p => p.Id, p => BuiltinProviders.GetKeyUrl(p.Id)),
        models = BuiltinProviders.CuratedModels,
    });
});
app.MapGet("/provider/{id}/models", async (string id, HttpContext ctx) =>
{
    var curated = BuiltinProviders.CuratedModels.TryGetValue(id, out var c)
        ? c : Array.Empty<ModelInfoDto>();
    List<ModelInfoDto>? live = null;
    try
    {
        var config = ConfigLoader.Load(workspace.Root);
        var auth = AuthStore.Load();
        var endpoint = ProviderResolver.Resolve(id, config, auth, workspace.Root);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", endpoint.ApiKey ?? "none");
        var doc = await http.GetFromJsonAsync<JsonDocument>(
            endpoint.BaseUrl.TrimEnd('/') + "/models",
            ctx.RequestAborted);
        live = doc?.RootElement.GetProperty("data").EnumerateArray()
            .Where(m => m.TryGetProperty("id", out _))
            .Select(m =>
            {
                var mid = m.GetProperty("id").GetString() ?? "";
                return new ModelInfoDto(mid, mid,
                    Free: id == "openrouter" ? mid.EndsWith(":free", StringComparison.Ordinal) : true);
            })
            .Where(m => !string.IsNullOrEmpty(m.Id))
            .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    catch { /* fall back to curated below */ }
    return Results.Ok(new { live = (object?)live ?? (object)curated, curated, source = live is null ? "curated" : "live" });
});
app.MapGet("/provider/auth", () => Results.Ok(BuiltinProviders.AuthMethods()));
app.MapPut("/auth/{id}", (string id, JsonElement body) =>
{
    var auth = AuthStore.Load();
    var keys = new Dictionary<string, string>(auth.ProviderKeys ?? new());
    if (body.TryGetProperty("key", out var k)) keys[id] = k.GetString() ?? "";
    else if (body.TryGetProperty("apiKey", out var k2)) keys[id] = k2.GetString() ?? "";
    auth = auth with { ProviderKeys = keys };
    auth.Save();
    return Results.Ok(true);
});
// OAuth-style connect: authorize returns a URL to open; callback accepts the pasted code/key and stores it.
// Custom OpenAI-compatible provider: any baseURL + API key + model id.
app.MapPost("/provider/custom", (JsonElement body) =>
{
    string? name = body.TryGetProperty("name", out var n) ? n.GetString() : null;
    string? baseUrl = body.TryGetProperty("baseUrl", out var b) ? b.GetString() : null;
    string? apiKey = body.TryGetProperty("apiKey", out var k) ? k.GetString() : null;
    string? model = body.TryGetProperty("model", out var m) ? m.GetString() : null;
    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(baseUrl))
        return Results.BadRequest(new { error = "name and baseUrl are required" });
    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        return Results.BadRequest(new { error = "baseUrl must be an absolute http(s) URL" });
    var slug = "custom-" + new string(name.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).Take(24).ToArray());
    if (slug == "custom-") return Results.BadRequest(new { error = "name must contain letters or digits" });
    var auth = AuthStore.Load();
    var keys = new Dictionary<string, string>(auth.ProviderKeys ?? new());
    if (!string.IsNullOrWhiteSpace(apiKey)) keys[slug] = apiKey.Trim();
    auth = auth with { ProviderKeys = keys };
    auth.Save();
    // Persist options.baseURL into the project dotcode.json so the resolver picks it up.
    try
    {
        var cfgPath = Path.Combine(workspace.Root, "dotcode.json");
        var cfgDoc = File.Exists(cfgPath)
            ? JsonDocument.Parse(File.ReadAllText(cfgPath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
            : JsonDocument.Parse("{}");
        var root = cfgDoc.RootElement.Clone();
        var cfgDict = new Dictionary<string, object?>();
        foreach (var prop in root.EnumerateObject()) cfgDict[prop.Name] = JsonElementToObj(prop.Value);
        var prov = cfgDict.TryGetValue("provider", out var pv) && pv is Dictionary<string, object?> pd ? pd : new();
        var opts = new Dictionary<string, object?> { ["baseURL"] = baseUrl.Trim(), ["models"] = new Dictionary<string, object?> { [model ?? "default"] = new Dictionary<string, object?> { } } };
        prov[slug] = new Dictionary<string, object?> { ["name"] = name.Trim(), ["options"] = opts };
        cfgDict["provider"] = prov;
        File.WriteAllText(cfgPath, JsonSerializer.Serialize(cfgDict, new JsonSerializerOptions { WriteIndented = true }));
    }
    catch { }
    return Results.Ok(new { id = slug, name, baseUrl, model });
});
app.MapGet("/provider/custom", () =>
{
    var cfg = ConfigLoader.Load(workspace.Root);
    var customs = new List<object>();
    if (cfg.Provider is not null)
        foreach (var kv in cfg.Provider)
            if (kv.Key.StartsWith("custom-", StringComparison.OrdinalIgnoreCase) && kv.Value is System.Text.Json.JsonElement el && el.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var name = el.TryGetProperty("name", out var pn) ? pn.GetString() : kv.Key;
                var url = el.TryGetProperty("options", out var po) && po.ValueKind == System.Text.Json.JsonValueKind.Object && po.TryGetProperty("baseURL", out var pb) ? pb.GetString() : null;
                customs.Add(new { id = kv.Key, name = name ?? kv.Key, baseUrl = url });
            }
    return Results.Ok(customs);
});
app.MapPost("/provider/{id}/oauth/authorize", (string id) =>
{
    var url = id.ToLowerInvariant() switch
    {
        "antigravity" or "google" => "https://aistudio.google.com/apikey",
        "github-copilot" => "https://github.com/login/device",
        "github-models" => "https://github.com/settings/personal-access-tokens/new",
        "9router" => "http://localhost:20128/dashboard",
        _ => (string?)null,
    };
    if (url is null) return Results.NotFound(new { error = $"no oauth flow for '{id}'" });
    return Results.Ok(new
    {
        url,
        instructions = id.ToLowerInvariant() switch
        {
            "antigravity" or "google" => "Sign in with your Google account, create an API key, then paste it back. It works for both Antigravity and Gemini (same key).",
            "github-copilot" => "Enter the device code shown on the page after you authorize, or paste a GitHub token.",
            "9router" => "Open the 9router dashboard, copy its local API key, then paste it back.",
            _ => "Create the token, then paste it back.",
        },
    });
});
app.MapPost("/provider/{id}/oauth/callback", (string id, JsonElement body) =>
{
    string? value = null;
    if (body.TryGetProperty("code", out var c)) value = c.GetString();
    else if (body.TryGetProperty("key", out var k)) value = k.GetString();
    if (string.IsNullOrWhiteSpace(value)) return Results.BadRequest(new { error = "code or key required" });
    var auth = AuthStore.Load();
    var keys = new Dictionary<string, string>(auth.ProviderKeys ?? new());
    // Antigravity and google share one credential under both ids.
    keys[id] = value;
    if (id.Equals("antigravity", StringComparison.OrdinalIgnoreCase)) keys["google"] = value;
    if (id.Equals("google", StringComparison.OrdinalIgnoreCase)) keys["antigravity"] = value;
    auth = auth with { ProviderKeys = keys };
    auth.Save();
    return Results.Ok(true);
});

// ---- Sessions ----
app.MapGet("/session", (ISessionStore s) => Results.Ok(s.List()));
app.MapPost("/session", (ISessionStore s, JsonElement body) =>
{
    string? title = body.TryGetProperty("title", out var t) ? t.GetString() : null;
    string? parent = body.TryGetProperty("parentID", out var p) ? p.GetString() : null;
    return Results.Ok(s.Create(title, parent, CurrentProject().Id));
});
app.MapGet("/session/{id}", (string id, ISessionStore s) => s.Get(id) is { } ses ? Results.Ok(ses) : Results.NotFound());
app.MapDelete("/session/{id}", (string id, ISessionStore s, ITodoStore todos) =>
{
    var ok = s.Delete(id);
    if (ok) todos.ReplaceAll(id, Array.Empty<Todo>());
    return Results.Ok(ok);
});
app.MapPatch("/session/{id}", (string id, JsonElement body, ISessionStore s) =>
{
    if (body.TryGetProperty("title", out var t) && t.GetString() is { } title) s.UpdateTitle(id, title);
    return Results.Ok(s.Get(id));
});
app.MapGet("/session/{id}/children", (string id) => Results.Ok(Array.Empty<Session>()));
app.MapPost("/session/{id}/abort", (string id) => Results.Ok(true));
app.MapPost("/session/{id}/share", (string id, ISessionStore s) => Results.Ok(s.Get(id)));
app.MapDelete("/session/{id}/share", (string id) => Results.Ok(true));
app.MapGet("/session/{id}/diff", (string id) => Results.Ok(Array.Empty<FileDiff>()));
app.MapPost("/session/{id}/revert", (string id, JsonElement body, IMessageStore m) =>
{
    var mid = body.TryGetProperty("messageID", out var x) ? x.GetString() ?? "" : "";
    return Results.Ok(m.Revert(id, mid));
});
app.MapGet("/session/{id}/todo", (string id, ITodoStore t) => Results.Ok(t.List(id)));

// ---- Messages ----
app.MapGet("/session/{id}/message", (string id, int? limit, IMessageStore m) => Results.Ok(m.List(id, limit).Select(ShapeThread)));
app.MapPost("/session/{id}/message", async (string id, JsonElement body, ISessionStore s, IMessageStore m, ITodoStore todos, HttpRequest req) =>
{
    var text = ExtractText(body) ?? "(empty)";
    var config = ConfigLoader.Load(workspace.Root);
    var (providerId, modelId) = ExtractModel(body, config);
    var agentName = body.TryGetProperty("agent", out var a) ? a.GetString() ?? "build" : "build";
    var autoApprove = body.TryGetProperty("autoApprove", out var ap) && ap.ValueKind == JsonValueKind.True;
    double? temperature = null; int? maxSteps = null; string? instructions = null;
    if (body.TryGetProperty("settings", out var st) && st.ValueKind == JsonValueKind.Object)
    {
        if (st.TryGetProperty("temperature", out var tv) && tv.ValueKind == JsonValueKind.Number) temperature = tv.GetDouble();
        if (st.TryGetProperty("maxSteps", out var mv) && mv.ValueKind == JsonValueKind.Number) maxSteps = mv.GetInt32();
        if (st.TryGetProperty("instructions", out var iv) && iv.ValueKind == JsonValueKind.String) instructions = iv.GetString();
    }
    m.AppendUserText(id, text, agentName, $"{providerId}/{modelId}");
    s.Touch(id);

    string assistantId;
    try
    {
        var history = m.List(id).SelectMany(e => e.Parts.OfType<TextPart>().Select(p => (e.Info.Role == MessageRole.Assistant ? "assistant" : "user", p.Text))).ToList();
        (assistantId, _) = await new AgentService(workspace.Root).CompleteAsync(
            providerId, modelId, agentName, history, m, id,
            autoApprove: autoApprove, todoStore: todos,
            temperature: temperature, maxSteps: maxSteps, instructions: instructions);
    }
    catch (ProviderNotConnectedException ex)
    {
        assistantId = Guid.NewGuid().ToString("n")[..12];
        m.AppendAssistantText(id, assistantId, $"Not connected: {ex.Message}");
    }
    catch (Exception ex)
    {
        assistantId = Guid.NewGuid().ToString("n")[..12];
        m.AppendAssistantText(id, assistantId, $"Agent error: {ex.Message}");
    }
    var last = m.List(id).FirstOrDefault(e => e.Info.Id == assistantId);
    if (last.Info is null) last = m.List(id).Last();
    return Results.Ok(ShapeThread(last));
});
app.MapPost("/session/{id}/prompt_async", (string id, JsonElement body, ISessionStore s, IMessageStore m) =>
{
    var text = ExtractText(body) ?? "(empty)";
    m.AppendUserText(id, text);
    s.Touch(id);
    return Results.NoContent();
});
app.MapPost("/session/{id}/command", (string id, JsonElement body) => Results.Ok(new { ok = true, command = body.ToString() }));
app.MapPost("/session/{id}/shell", (string id, JsonElement body) => Results.Ok(new { ok = true }));

// ---- Files / Find ----
app.MapGet("/find", (string? pattern) => Results.Ok(FindText(workspace.Root, pattern ?? "")));
app.MapGet("/find/file", (string? query) => Results.Ok(FindFiles(workspace.Root, query ?? "")));
app.MapGet("/file", (string? path) => Results.Ok(ListDir(string.IsNullOrWhiteSpace(path) ? workspace.Root : Path.Combine(workspace.Root, path))));
app.MapGet("/file/content", (string? path) =>
{
    if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest();
    var full = Path.GetFullPath(Path.Combine(workspace.Root, path));
    if (!full.StartsWith(workspace.Root)) return Results.Forbid();
    if (!File.Exists(full)) return Results.NotFound();
    return Results.Ok(new { type = "raw", content = File.ReadAllText(full) });
});
app.MapGet("/file/status", () => Results.Ok(Array.Empty<object>()));

// ---- Agents / Commands / LSP / MCP / Events ----
app.MapGet("/agent", () => Results.Ok(BuiltinAgents.All));
app.MapGet("/command", () => Results.Ok(Array.Empty<object>()));
app.MapGet("/lsp", () => Results.Ok(Array.Empty<object>()));
app.MapGet("/formatter", () => Results.Ok(Array.Empty<object>()));
app.MapGet("/mcp", () => Results.Ok(new { }));
app.MapGet("/event", async (HttpContext ctx) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    await ctx.Response.WriteAsync("event: server.connected\ndata: {}\n\n");
    await ctx.Response.Body.FlushAsync();
    var reader = eventChannel.Reader;
    try
    {
        await foreach (var ev in reader.ReadAllAsync(ctx.RequestAborted))
            await ctx.Response.WriteAsync($"event: {ev.Type}\ndata: {{\"sessionID\":\"{ev.SessionId}\"}}\n\n", ctx.RequestAborted);
    }
    catch (OperationCanceledException) { }
});
app.MapGet("/doc", () => Results.Redirect("/swagger"));
app.MapPost("/log", (JsonElement body) => Results.Ok(true));

app.Run();

static (string ProviderId, string ModelId) ExtractModel(JsonElement body, DotCodeConfig config)
{
    if (body.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object)
    {
        var p = m.TryGetProperty("providerID", out var pid) ? pid.GetString() : null;
        var mid = m.TryGetProperty("modelID", out var mm) ? mm.GetString() : null;
        if (!string.IsNullOrWhiteSpace(p) && !string.IsNullOrWhiteSpace(mid)) return (p, mid);
    }
    var full = config.Model
        ?? Environment.GetEnvironmentVariable("DOTCODE_MODEL")
        ?? "ollama/YuriiFominYoung/fable-5:latest";
    var slash = full.IndexOf('/');
    return slash > 0 ? (full[..slash], full[(slash + 1)..]) : ("ollama", full);
}

static object ShapeThread((Message Info, IReadOnlyList<Part> Parts) e)
    => new { info = e.Info, parts = e.Parts.Select(ShapePart).ToList() };

static object ShapePart(Part p)
{
    if (p is TextPart t) return new { id = t.Id, messageId = t.MessageId, type = t.Type, text = t.Text };
    if (p is ToolCallPart c) return new { id = c.Id, messageId = c.MessageId, type = c.Type, tool = c.Tool, input = c.InputJson, callId = c.CallId };
    if (p is ToolResultPart r) return new { id = r.Id, messageId = r.MessageId, type = r.Type, tool = r.Tool, callId = r.CallId, output = r.Output, isError = r.IsError };
    return new { id = p.Id, messageId = p.MessageId, type = p.Type };
}

static string? ExtractText(JsonElement body)
{
    if (!body.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) return null;
    foreach (var p in parts.EnumerateArray())
        if (p.TryGetProperty("text", out var t)) return t.GetString();
    return null;
}

static object? JsonElementToObj(JsonElement el) => el.ValueKind switch
{
    JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => (object?)JsonElementToObj(p.Value)),
    JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObj).ToList(),
    JsonValueKind.String => el.GetString(),
    JsonValueKind.Number => el.TryGetInt64(out var l) && l.ToString() == el.GetRawText() ? l : el.GetDouble(),
    JsonValueKind.True => true,
    JsonValueKind.False => false,
    _ => null,
};

static string? GitBranch(string root)
{
    try
    {
        var head = Path.Combine(root, ".git", "HEAD");
        if (!File.Exists(head)) return null;
        var line = File.ReadAllText(head).Trim();
        return line.StartsWith("ref: refs/heads/") ? line["ref: refs/heads/".Length..] : line;
    }
    catch { return null; }
}

static object FindText(string root, string pattern)
{
    var results = new List<object>();
    if (string.IsNullOrWhiteSpace(pattern)) return results;
    try
    {
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Take(2000))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")) continue;
            string[] lines;
            try { lines = File.ReadAllLines(f); } catch { continue; }
            for (int i = 0; i < lines.Length && results.Count < 50; i++)
                if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    results.Add(new { path = Path.GetRelativePath(root, f), line_number = i + 1, lines = lines[i].Trim() });
        }
    }
    catch { }
    return results;
}

static object FindFiles(string root, string query)
{
    try
    {
        return Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
            .Where(p => string.IsNullOrWhiteSpace(query) || Path.GetFileName(p).Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(root, p)).Take(100).ToList();
    }
    catch { return new List<string>(); }
}

static object ListDir(string dir)
{
    try
    {
        return Directory.GetFileSystemEntries(dir)
            .Select(p => new { name = Path.GetFileName(p), path = p, isDir = Directory.Exists(p) }).ToList();
    }
    catch { return new List<object>(); }
}

/// <summary>Holds the active workspace root; changed at runtime via POST /project/select.</summary>
public sealed class WorkspaceState(string initialRoot)
{
    public string Root { get; private set; } = Path.GetFullPath(initialRoot);
    public void SetRoot(string path) => Root = Path.GetFullPath(path);
}

public sealed record StoreEvent(string Type, string SessionId, DateTimeOffset At);

public sealed record ProviderInfo(string Id, string Name, string[] Env);
public sealed record ModelInfoDto(string Id, string Name, bool Free);
public static class BuiltinProviders
{
    public static readonly ProviderInfo[] All = new ProviderInfo[]
    {
        new("dotcode-gateway", "DotCode Gateway (zero-config)", new[] { "DOTCODE_API_KEY" }),
        new("openrouter", "OpenRouter", new[] { "OPENROUTER_API_KEY" }),
        new("anthropic", "Anthropic", new[] { "ANTHROPIC_API_KEY" }),
        new("openai", "OpenAI", new[] { "OPENAI_API_KEY" }),
        new("azure", "Azure OpenAI", new[] { "AZURE_OPENAI_API_KEY" }),
        new("ollama", "Ollama (local)", Array.Empty<string>()),
        new("lmstudio", "LM Studio (local)", Array.Empty<string>()),
        new("google", "Google Gemini (AI Studio, free tier)", new[] { "GOOGLE_API_KEY", "GEMINI_API_KEY" }),
        new("antigravity", "Google Antigravity (Gemini, sign in with Google)", new[] { "GOOGLE_API_KEY", "GEMINI_API_KEY" }),
        new("groq", "Groq (free tier)", new[] { "GROQ_API_KEY" }),
        new("mistral", "Mistral (free tier)", new[] { "MISTRAL_API_KEY" }),
        new("cerebras", "Cerebras (free tier)", new[] { "CEREBRAS_API_KEY" }),
        new("github-models", "GitHub Models (free with GitHub account)", new[] { "GITHUB_TOKEN", "GITHUB_MODELS_TOKEN" }),
        new("9router", "9router (local router — Gemini, Claude, Kimi, GLM)", new[] { "NINE_ROUTER_API_KEY", "NINEROUTER_API_KEY" }),
        new("github-copilot", "GitHub Copilot (OAuth)", new[] { "GITHUB_TOKEN" }),
    };

    /// <summary>Curated starter models per provider (free-tier friendly). The live /models fetch overrides this when it succeeds.</summary>
    public static readonly Dictionary<string, ModelInfoDto[]> CuratedModels = new()
    {
        ["ollama"] = new[]
        {
            new ModelInfoDto("YuriiFominYoung/fable-5:latest", "Fable 5 (local)", true),
            new ModelInfoDto("qwen3-coder:latest", "Qwen3 Coder (local)", true),
            new ModelInfoDto("deepseek-r1:latest", "DeepSeek R1 (local)", true),
        },
        ["google"] = new[]
        {
            new ModelInfoDto("gemini-2.5-flash", "Gemini 2.5 Flash", true),
            new ModelInfoDto("gemini-2.5-flash-lite", "Gemini 2.5 Flash-Lite", true),
            new ModelInfoDto("gemini-2.5-pro", "Gemini 2.5 Pro", true),
        },
        ["antigravity"] = new[]
        {
            new ModelInfoDto("gemini-2.5-flash", "Gemini 2.5 Flash", true),
            new ModelInfoDto("gemini-2.5-pro", "Gemini 2.5 Pro", true),
            new ModelInfoDto("gemini-3.5-flash", "Gemini 3.5 Flash", true),
        },
        ["groq"] = new[]
        {
            new ModelInfoDto("openai/gpt-oss-120b", "GPT-OSS 120B", true),
            new ModelInfoDto("openai/gpt-oss-20b", "GPT-OSS 20B", true),
            new ModelInfoDto("qwen/qwen3.6-27b", "Qwen3.6 27B", true),
        },
        ["mistral"] = new[]
        {
            new ModelInfoDto("mistral-small-latest", "Mistral Small 4", true),
            new ModelInfoDto("codestral-latest", "Codestral", true),
            new ModelInfoDto("mistral-medium-latest", "Mistral Medium 3.5", true),
        },
        ["cerebras"] = new[]

        {
            new ModelInfoDto("llama3.3-70b", "Llama 3.3 70B", true),
            new ModelInfoDto("qwen-3-coder-480b", "Qwen3 Coder 480B", true),
        },
        ["github-models"] = new[]
        {
            new ModelInfoDto("openai/gpt-4o-mini", "GPT-4o mini", true),
            new ModelInfoDto("meta/Llama-3.3-70B-Instruct", "Llama 3.3 70B", true),
            new ModelInfoDto("microsoft/Phi-4", "Phi-4", true),
        },
        ["openrouter"] = new[]
        {
            new ModelInfoDto("openai/gpt-oss-20b:free", "GPT-OSS 20B (free)", true),
            new ModelInfoDto("google/gemma-4-31b-it:free", "Gemma 4 31B (free)", true),
            new ModelInfoDto("cohere/north-mini-code:free", "North Mini Code (free)", true),
        },
        ["9router"] = new[]
        {
            new ModelInfoDto("ag/gemini-3.8-flash-medium", "Gemini 3.8 Flash (med)", true),
            new ModelInfoDto("ag/gemini-3.8-flash-high", "Gemini 3.8 Flash (high)", true),
            new ModelInfoDto("ag/gemini-3.8-flash", "Gemini 3.8 Flash", true),
            new ModelInfoDto("ag/claude-sonnet-4-6", "Claude Sonnet 4.6", true),
            new ModelInfoDto("ag/claude-opus-4-6-thinking", "Claude Opus 4.6 (thinking)", true),
            new ModelInfoDto("kimchi/kimi-k3", "Kimi K3", true),
            new ModelInfoDto("kimchi/glm-5.3-flash", "GLM 5.3 Flash", true),
            new ModelInfoDto("kr/claude-haiku-4.5", "Claude Haiku 4.5", true),
        },
    };

    /// <summary>Where to send the user to get an API key for a provider.</summary>
    public static string? GetKeyUrl(string providerId) => providerId switch
    {
        "google" or "antigravity" => "https://aistudio.google.com/apikey",
        "groq" => "https://console.groq.com/keys",
        "mistral" => "https://console.mistral.ai/api-keys",
        "cerebras" => "https://cloud.cerebras.ai",
        "github-models" => "https://github.com/settings/personal-access-tokens/new",
        "openrouter" => "https://openrouter.ai/keys",
        "9router" => "http://localhost:20128/dashboard",
        "openai" => "https://platform.openai.com/api-keys",
        "anthropic" => "https://console.anthropic.com/settings/keys",
        _ => null,
    };
    public static object AuthMethods() => new Dictionary<string, object>
    {
        ["dotcode-gateway"] = new[] { new { type = "oauth", label = "Sign in with browser" } },
        ["github-copilot"] = new[] { new { type = "oauth", label = "Device flow" } },
        ["azure"] = new[] { new { type = "api", label = "API key + resource name" } },
        ["ollama"] = new[] { new { type = "none", label = "No auth (local)" } },
    };
}

public static class BuiltinAgents
{
    public static readonly object[] All = new object[]
    {
        new { name = "build", mode = "primary", description = "Default agent, all tools enabled" },
        new { name = "plan", mode = "primary", description = "Read-only planning agent" },
        new { name = "general", mode = "subagent", description = "Multi-step tasks" },
        new { name = "explore", mode = "subagent", description = "Fast read-only codebase search" },
    };
}
