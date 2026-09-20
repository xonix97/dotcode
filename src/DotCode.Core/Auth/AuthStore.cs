using System.Text.Json;

namespace DotCode.Core.Auth;

/// <summary>Secrets store: ~/.config/dotcode/auth.json (0600). Never in project config.</summary>
public sealed record AuthStore(
    string? GatewayJwt = null,
    string? GatewayRefresh = null,
    Dictionary<string, string>? ProviderKeys = null,
    Dictionary<string, string>? OAuthTokens = null)
{
    public static string Path =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "dotcode", "auth.json");

    public static AuthStore Load()
    {
        try
        {
            if (!File.Exists(Path)) return new AuthStore();
            var json = File.ReadAllText(Path);
            return JsonSerializer.Deserialize<AuthStore>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AuthStore();
        }
        catch { return new AuthStore(); }
    }

    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }

    public string? ResolveProviderKey(string providerId, IEnumerable<string>? envNames = null)
    {
        if (ProviderKeys?.TryGetValue(providerId, out var k) == true && !string.IsNullOrWhiteSpace(k)) return k;
        if (envNames != null)
            foreach (var e in envNames)
            {
                var v = Environment.GetEnvironmentVariable(e);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        // Conventional fallback: PROVIDER_API_KEY
        return Environment.GetEnvironmentVariable($"{providerId.ToUpperInvariant()}_API_KEY");
    }
}
