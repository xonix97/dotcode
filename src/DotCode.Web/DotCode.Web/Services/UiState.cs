namespace DotCode.Web.Services;

/// <summary>Per-circuit UI state: selected agent/model per session, auto-approve toggle.</summary>
public sealed class SessionUiState
{
    private readonly Dictionary<string, string> _agents = new();
    private readonly Dictionary<string, (string Provider, string Model)> _models = new();

    public string GetAgent(string sessionId) => _agents.TryGetValue(sessionId, out var a) ? a : "build";
    public void SetAgent(string sessionId, string agent) => _agents[sessionId] = agent;
    public (string Provider, string Model) GetModel(string sessionId) => _models.TryGetValue(sessionId, out var m) ? m : ("ollama", "YuriiFominYoung/fable-5:latest");
    public void SetModel(string sessionId, string provider, string model) => _models[sessionId] = (provider, model);

    public bool AutoApprove { get; set; }

    // --- agent settings (circuit-wide, sent with every prompt) ---
    public double? Temperature { get; set; }
    public int? MaxSteps { get; set; }
    public string Instructions { get; set; } = "";

    public SessionSettings? ToSettings() =>
        Temperature is null && MaxSteps is null && string.IsNullOrWhiteSpace(Instructions)
            ? null
            : new SessionSettings(Temperature, MaxSteps, string.IsNullOrWhiteSpace(Instructions) ? null : Instructions);
}

public sealed class ToastService
{
    public event Action<string, string>? OnToast;
    public void Show(string message, string variant = "info") => OnToast?.Invoke(message, variant);
}
