using System.Net.Http.Json;
using System.Text.Json;

namespace DotCode.Web.Services;

public sealed record SessionDto(string Id, string? ParentId, string Title, string ProjectId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record PartDto(string Id, string MessageId, string Type, string? Text, string? Tool, string? Input, string? CallId, string? Output, bool IsError);
public sealed record MessageDto(string Id, string SessionId, string Role, string? Agent, string? Model, DateTimeOffset CreatedAt);
public sealed record ThreadDto(MessageDto Info, List<PartDto> Parts);
public sealed record TodoDto(string Id, string SessionId, string Content, bool Done);
public sealed record ProviderDto(string Id, string Name, string[] Env);
public sealed record ModelDto(string Id, string Name, bool Free);
public sealed record ProviderInfo(ProviderDto[] All, string[] Connected, Dictionary<string, string?>? KeyUrls = null, Dictionary<string, ModelDto[]>? Models = null);
public sealed record AgentDto(string Name, string Mode, string Description);
public sealed record HealthDto(bool Healthy, string Version);
public sealed record ProjectDto(string Id, string Name, string Root);
public sealed record FsEntryDto(string Name, string Path);
public sealed record FsBrowseDto(string Path, string? Parent, List<FsEntryDto> Dirs);
public sealed record SessionSettings(double? Temperature, int? MaxSteps, string? Instructions);
public sealed record SelectResultDto(string Root, string Name);

public sealed class DotCodeApi(HttpClient http)
{
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = http;

    public async Task<HealthDto?> HealthAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<HealthDto>("global/health", Opts, ct);
    public async Task<List<SessionDto>?> SessionsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<SessionDto>>("session", Opts, ct);
    public async Task<SessionDto?> GetSessionAsync(string id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<SessionDto>($"session/{id}", Opts, ct);
    public async Task<SessionDto?> CreateSessionAsync(string? title, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync("session", new { title }, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<SessionDto>(Opts, ct);
    }
    public async Task DeleteSessionAsync(string id, CancellationToken ct = default)
        => await _http.DeleteAsync($"session/{id}", ct);
    public async Task<List<ThreadDto>?> MessagesAsync(string id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<ThreadDto>>($"session/{id}/message", Opts, ct);
    public async Task<List<TodoDto>?> TodosAsync(string id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<TodoDto>>($"session/{id}/todo", Opts, ct);
    public async Task AbortAsync(string id, CancellationToken ct = default)
        => (await _http.PostAsync($"session/{id}/abort", null, ct)).EnsureSuccessStatusCode();
    public async Task<ProviderInfo?> ProvidersAsync(CancellationToken ct = default)
    {
        var doc = await _http.GetFromJsonAsync<JsonDocument>("provider", Opts, ct);
        if (doc is null) return null;
        var all = doc.RootElement.GetProperty("all").Deserialize<ProviderDto[]>(Opts) ?? [];
        var connected = doc.RootElement.GetProperty("connected").Deserialize<string[]>(Opts) ?? [];
        return new ProviderInfo(all, connected);
    }
    public async Task<List<AgentDto>?> AgentsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<AgentDto>>("agent", Opts, ct);
    public async Task<(List<ModelDto> Models, string Source)> ProviderModelsAsync(string id, CancellationToken ct = default)
    {
        var doc = await _http.GetFromJsonAsync<JsonDocument>($"provider/{Uri.EscapeDataString(id)}/models", Opts, ct);
        if (doc is null) return (new(), "none");
        var source = doc.RootElement.TryGetProperty("source", out var s) ? s.GetString() ?? "curated" : "curated";
        var models = doc.RootElement.GetProperty("live").Deserialize<List<ModelDto>>(Opts) ?? new();
        return (models, source);
    }
    public sealed record OAuthInfo(string Url, string Instructions);
    public async Task<OAuthInfo?> OAuthAuthorizeAsync(string id, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync($"provider/{Uri.EscapeDataString(id)}/oauth/authorize", new { }, ct);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadFromJsonAsync<OAuthInfo>(Opts, ct);
    }
    public async Task<bool> OAuthCallbackAsync(string id, string codeOrKey, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync($"provider/{Uri.EscapeDataString(id)}/oauth/callback", new { code = codeOrKey }, ct);
        return res.IsSuccessStatusCode;
    }

    public sealed record CustomProviderDto(string Id, string? Name, string? BaseUrl);
    public async Task<List<CustomProviderDto>?> GetCustomProvidersAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<CustomProviderDto>>("provider/custom", Opts, ct);
    public async Task<CustomProviderDto?> AddCustomProviderAsync(string name, string baseUrl, string? apiKey, string? model, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync("provider/custom", new { name, baseUrl, apiKey, model }, ct);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadFromJsonAsync<CustomProviderDto>(Opts, ct);
    }
    public async Task<ProjectDto?> CurrentProjectAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<ProjectDto>("project/current", Opts, ct);
    public async Task<FsBrowseDto?> BrowseFsAsync(string? path = null, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<FsBrowseDto>($"project/fs{(string.IsNullOrWhiteSpace(path) ? "" : $"?path={Uri.EscapeDataString(path)}")}", Opts, ct);
    public async Task<SelectResultDto?> SelectProjectAsync(string path, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync("project/select", new { path }, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<SelectResultDto>(Opts, ct);
    }
    public async Task RenameSessionAsync(string id, string title, CancellationToken ct = default)
        => await _http.PatchAsJsonAsync($"session/{id}", new { title }, ct);
    public async Task<bool> RevertAsync(string id, string messageId, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync($"session/{id}/revert", new { messageID = messageId }, ct);
        return res.IsSuccessStatusCode;
    }
    public async Task<bool> ShareAsync(string id, CancellationToken ct = default)
    {
        var res = await _http.PostAsync($"session/{id}/share", null, ct);
        return res.IsSuccessStatusCode;
    }

    /// <summary>Subscribe to the server's SSE event stream; onEvent fires per event (opencode-style live updates).</summary>
    public async Task StreamEventsAsync(Func<string, string, Task> onEvent, CancellationToken ct)
    {
        try
        {
            using var stream = await _http.GetStreamAsync("event", ct);
            using var reader = new StreamReader(stream);
            string? line;
            string evtType = "message";
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (line.StartsWith("event: ")) evtType = line[7..].Trim();
                else if (line.StartsWith("data: "))
                {
                    var data = line[6..].Trim();
                    var sid = "";
                    try
                    {
                        var doc = JsonDocument.Parse(data);
                        if (doc.RootElement.TryGetProperty("sessionID", out var s)) sid = s.GetString() ?? "";
                    }
                    catch { }
                    await onEvent(evtType, sid);
                    evtType = "message";
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* transient network errors: caller reconnects via finally */ }
    }
    public async Task<ThreadDto?> PostMessageAsync(string id, string text, string agent, string providerId, string modelId, bool autoApprove = false, SessionSettings? settings = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["agent"] = agent,
            ["autoApprove"] = autoApprove,
            ["model"] = new { providerID = providerId, modelID = modelId },
            ["parts"] = new[] { new { type = "text", text } },
        };
        if (settings is not null)
            body["settings"] = new { temperature = settings.Temperature, maxSteps = settings.MaxSteps, instructions = settings.Instructions };
        var res = await _http.PostAsJsonAsync($"session/{id}/message", body, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<ThreadDto>(Opts, ct);
    }
    public async Task<List<string>?> FindFilesAsync(string query, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<string>>($"find/file?query={Uri.EscapeDataString(query)}", Opts, ct);
    public async Task<bool> PutAuthAsync(string providerId, string key, CancellationToken ct = default)
    {
        var res = await _http.PutAsJsonAsync($"auth/{providerId}", new { key }, ct);
        return res.IsSuccessStatusCode;
    }
}
