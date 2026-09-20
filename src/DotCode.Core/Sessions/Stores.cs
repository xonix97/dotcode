using System.Text.Json;
using DotCode.Core.Models;

namespace DotCode.Core.Sessions;

public interface ISessionStore
{
    IReadOnlyList<Session> List();
    Session? Get(string id);
    Session Create(string? title, string? parentId, string projectId);
    bool Delete(string id);
    bool UpdateTitle(string id, string title);
    void Touch(string id);
}

public sealed class FileSessionStore : ISessionStore
{
    private readonly string _dir;
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public FileSessionStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(DataDir(), "sessions");
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.GetFiles(_dir, "*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<Session>(File.ReadAllText(f));
                if (s != null) _sessions[s.Id] = s;
            }
            catch { }
        }
    }

    public static string DataDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = !string.IsNullOrWhiteSpace(xdg) ? xdg! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(baseDir, "dotcode");
    }

    public IReadOnlyList<Session> List()
    {
        lock (_lock) return _sessions.Values.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public Session? Get(string id)
    {
        lock (_lock) return _sessions.TryGetValue(id, out var s) ? s : null;
    }

    public Session Create(string? title, string? parentId, string projectId)
    {
        var now = DateTimeOffset.UtcNow;
        var s = new Session(Guid.NewGuid().ToString("n")[..12], parentId, title ?? "New session", projectId, now, now);
        lock (_lock) { _sessions[s.Id] = s; Persist(s); }
        return s;
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (!_sessions.Remove(id)) return false;
            try { File.Delete(Path.Combine(_dir, id + ".json")); } catch { }
            return true;
        }
    }

    public bool UpdateTitle(string id, string title)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(id, out var s)) return false;
            var u = s with { Title = title, UpdatedAt = DateTimeOffset.UtcNow };
            _sessions[id] = u; Persist(u);
            return true;
        }
    }

    public void Touch(string id)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(id, out var s)) return;
            var u = s with { UpdatedAt = DateTimeOffset.UtcNow };
            _sessions[id] = u; Persist(u);
        }
    }

    private void Persist(Session s) => File.WriteAllText(Path.Combine(_dir, s.Id + ".json"), JsonSerializer.Serialize(s, Opts));
}

public interface IMessageStore
{
    IReadOnlyList<(Message Info, IReadOnlyList<Part> Parts)> List(string sessionId, int? limit = null);
    (Message Info, IReadOnlyList<Part> Parts) AppendUserText(string sessionId, string text, string? agent = null, string? model = null);
    void AppendAssistantText(string sessionId, string messageId, string text);
    bool Revert(string sessionId, string messageId);
    string BeginAssistantTurn(string sessionId, string? agent = null, string? model = null);
    void AppendAssistantTextPart(string assistantMessageId, string text);
    ToolCallPart AppendToolCall(string assistantMessageId, string tool, string inputJson, string? callId = null);
    void AppendToolResult(string assistantMessageId, string callId, string tool, string output, bool isError = false);
}

/// <summary>Shared in-memory index + append logic. Subclasses decide how/when parts are persisted.</summary>
public abstract class MessageStoreBase : IMessageStore
{
    /// <summary>Raised on every append/revert: (sessionId, partId — "" when only the message index changed).
    /// The server fans this out over SSE so UIs can refresh live.</summary>
    public static event Action<string, string>? StoreChanged;

    protected readonly List<Message> _messages = new();
    protected readonly List<Part> _parts = new();
    protected readonly object _lock = new();

    public abstract bool Revert(string sessionId, string messageId);

    public IReadOnlyList<(Message Info, IReadOnlyList<Part> Parts)> List(string sessionId, int? limit = null)
    {
        lock (_lock)
        {
            var q = _messages.Where(m => m.SessionId == sessionId).OrderBy(m => m.CreatedAt);
            if (limit.HasValue) q = q.TakeLast(limit.Value).OrderBy(m => m.CreatedAt);
            return q.Select(m => (m, (IReadOnlyList<Part>)_parts.Where(p => p.MessageId == m.Id).ToList())).ToList();
        }
    }

    public (Message Info, IReadOnlyList<Part> Parts) AppendUserText(string sessionId, string text, string? agent = null, string? model = null)
    {
        var m = new Message(NewId(), sessionId, MessageRole.User, agent, model, DateTimeOffset.UtcNow);
        var p = new TextPart(NewId(), m.Id, text);
        lock (_lock) { _messages.Add(m); _parts.Add(p); OnPartAppended(p); NotifyChanged(sessionId, p); }
        return (m, new[] { p });
    }

    public void AppendAssistantText(string sessionId, string messageId, string text)
    {
        var m = new Message(messageId, sessionId, MessageRole.Assistant, null, null, DateTimeOffset.UtcNow);
        var p = new TextPart(NewId(), messageId, text);
        lock (_lock) { _messages.Add(m); _parts.Add(p); OnPartAppended(p); NotifyChanged(sessionId, p); }
    }

    public string BeginAssistantTurn(string sessionId, string? agent = null, string? model = null)
    {
        var m = new Message(NewId(), sessionId, MessageRole.Assistant, agent, model, DateTimeOffset.UtcNow);
        lock (_lock) { _messages.Add(m); OnPartAppended(null); NotifyChanged(sessionId, null); }
        return m.Id;
    }

    public void AppendAssistantTextPart(string assistantMessageId, string text)
    {
        lock (_lock)
        {
            var p = new TextPart(NewId(), assistantMessageId, text);
            _parts.Add(p); OnPartAppended(p);
            NotifyChanged(OwnerSession(assistantMessageId), p);
        }
    }

    public ToolCallPart AppendToolCall(string assistantMessageId, string tool, string inputJson, string? callId = null)
    {
        var p = new ToolCallPart(NewId(), assistantMessageId, tool, inputJson, callId ?? NewId());
        lock (_lock) { _parts.Add(p); OnPartAppended(p); NotifyChanged(OwnerSession(assistantMessageId), p); }
        return p;
    }

    public void AppendToolResult(string assistantMessageId, string callId, string tool, string output, bool isError = false)
    {
        lock (_lock)
        {
            var p = new ToolResultPart(NewId(), assistantMessageId, tool, callId, output, isError);
            _parts.Add(p); OnPartAppended(p);
            NotifyChanged(OwnerSession(assistantMessageId), p);
        }
    }

    /// <summary>Called after each mutation; null means only the message index changed.</summary>
    protected abstract void OnPartAppended(Part? part);

    /// <summary>Raise StoreChanged. Must be called under _lock. Null/empty sessionId resolves from the message list.</summary>
    protected void NotifyChanged(string? sessionId, Part? part)
    {
        if (string.IsNullOrEmpty(sessionId))
            sessionId = _messages.Count > 0 ? _messages[^1].SessionId : null;
        if (string.IsNullOrEmpty(sessionId)) return;
        StoreChanged?.Invoke(sessionId, part?.Id ?? "");
    }

    private string? OwnerSession(string messageId)
        => _messages.FirstOrDefault(m => m.Id == messageId)?.SessionId;

    protected static string NewId() => Guid.NewGuid().ToString("n")[..12];

    protected static (Message Info, IReadOnlyList<Part> Parts) BuildEntry(Message m, IEnumerable<Part> parts)
        => (m, parts.ToList());
}

/// <summary>Session-scoped JSON files under {dataDir}/messages/{sessionId}.json. Survives restarts.</summary>
public sealed class FileMessageStore : MessageStoreBase
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public FileMessageStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(FileSessionStore.DataDir(), "messages");
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.GetFiles(_dir, "*.json"))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(f));
                var root = doc.RootElement;
                if (root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Array)
                    foreach (var m in info.EnumerateArray())
                    {
                        var msg = m.Deserialize<Message>(Opts);
                        if (msg is not null) _messages.Add(msg);
                    }
                if (root.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    foreach (var p in parts.EnumerateArray())
                    {
                        var part = p.Deserialize<Part>(Opts);
                        if (part is not null) _parts.Add(part);
                    }
            }
            catch { }
        }
    }

    public override bool Revert(string sessionId, string messageId)
    {
        lock (_lock)
        {
            var m = _messages.FirstOrDefault(x => x.SessionId == sessionId && x.Id == messageId);
            if (m is null) return false;
            // Drop this message and everything after it (opencode revert semantics).
            var doomed = _messages.Where(x => x.SessionId == sessionId && x.CreatedAt >= m.CreatedAt).Select(x => x.Id).ToHashSet();
            _messages.RemoveAll(x => doomed.Contains(x.Id));
            _parts.RemoveAll(p => doomed.Contains(p.MessageId));
            Persist(sessionId);
            NotifyChanged(sessionId, null);
            return true;
        }
    }

    protected override void OnPartAppended(Part? part)
    {
        // Called under _lock. Persist the owning session; null part = only the message index changed.
        var sessionId = part is not null
            ? _messages.FirstOrDefault(m => m.Id == part.MessageId)?.SessionId
            : _messages.Count > 0 ? _messages[^1].SessionId : null;
        if (sessionId is not null) Persist(sessionId);
    }

    private void Persist(string sessionId)
    {
        try
        {
            var info = _messages.Where(m => m.SessionId == sessionId).OrderBy(m => m.CreatedAt).ToList();
            var parts = info.Select(m => m.Id).ToHashSet();
            var payload = new
            {
                info = info,
                parts = _parts.Where(p => parts.Contains(p.MessageId)).ToList(),
            };
            File.WriteAllText(Path.Combine(_dir, sessionId + ".json"), JsonSerializer.Serialize(payload, Opts));
        }
        catch { }
    }
}

/// <summary>Pure in-memory store (tests, ephemeral runs).</summary>
public sealed class InMemoryMessageStore : MessageStoreBase
{
    public override bool Revert(string sessionId, string messageId) => true;
    protected override void OnPartAppended(Part? part) { }
}

public interface ITodoStore
{
    IReadOnlyList<Todo> List(string sessionId);
    void ReplaceAll(string sessionId, IEnumerable<Todo> todos);
    void Toggle(string sessionId, string todoId);
}

/// <summary>File-backed todos: {dataDir}/todos/{sessionId}.json.</summary>
public sealed class FileTodoStore : ITodoStore
{
    private readonly string _dir;
    private readonly Dictionary<string, List<Todo>> _todos = new();
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public FileTodoStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(FileSessionStore.DataDir(), "todos");
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.GetFiles(_dir, "*.json"))
        {
            try
            {
                var id = Path.GetFileNameWithoutExtension(f);
                var list = JsonSerializer.Deserialize<List<Todo>>(File.ReadAllText(f), Opts);
                if (list is not null) _todos[id] = list;
            }
            catch { }
        }
    }

    public IReadOnlyList<Todo> List(string sessionId)
    {
        lock (_lock) return _todos.TryGetValue(sessionId, out var l) ? l.ToList() : [];
    }

    public void ReplaceAll(string sessionId, IEnumerable<Todo> todos)
    {
        lock (_lock)
        {
            var list = todos.ToList();
            _todos[sessionId] = list;
            Persist(sessionId, list);
        }
    }

    public void Toggle(string sessionId, string todoId)
    {
        lock (_lock)
        {
            if (!_todos.TryGetValue(sessionId, out var list)) return;
            var idx = list.FindIndex(t => t.Id == todoId);
            if (idx < 0) return;
            list[idx] = list[idx] with { Done = !list[idx].Done };
            Persist(sessionId, list);
        }
    }

    private void Persist(string sessionId, List<Todo> list)
    {
        try { File.WriteAllText(Path.Combine(_dir, sessionId + ".json"), JsonSerializer.Serialize(list, Opts)); } catch { }
    }
}
