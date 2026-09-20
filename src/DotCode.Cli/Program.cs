using DotCode.Core.Auth;
using DotCode.Providers;

var argv = args.ToList();
if (argv.Count == 0 || argv[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        dotcode — AI coding agent (.NET port of OpenCode)

        Usage:
          dotcode auth login [--token <jwt>]   Browser sign-in for DotCode Gateway (paste token)
          dotcode auth logout                  Clear gateway credentials
          dotcode auth list                    Show connection status for all providers
          dotcode auth set-key <provider> <key>  Save a BYOK API key
          dotcode provider list                List known providers
          dotcode serve                        Run the HTTP server (port 4096)
        """);
    return 0;
}

switch (argv[0])
{
    case "auth" when argv.Count > 1 && argv[1] == "list":
    {
        var auth = AuthStore.Load();
        Console.WriteLine($"Gateway JWT: {(string.IsNullOrWhiteSpace(auth.GatewayJwt) ? "not connected" : "connected")}");
        foreach (var p in ProviderCatalog.All)
        {
            var key = auth.ResolveProviderKey(p.Id, p.Env);
            var status = p.Auth == ProviderAuthType.None ? "local (no key needed)"
                : string.IsNullOrWhiteSpace(key) ? "not connected" : "connected";
            Console.WriteLine($"  {p.Id,-16} {p.Name,-32} [{status}]");
        }
        return 0;
    }
    case "auth" when argv.Count > 1 && argv[1] == "login":
    {
        var tokenIdx = argv.IndexOf("--token");
        var token = tokenIdx >= 0 && tokenIdx + 1 < argv.Count ? argv[tokenIdx + 1] : null;
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("Open https://gateway.dotcode.dev/oauth/device in your browser, sign in,");
            Console.WriteLine("then re-run: dotcode auth login --token <jwt>");
            return 0;
        }
        var auth = AuthStore.Load() with { GatewayJwt = token };
        auth.Save();
        Console.WriteLine("Gateway connected. Credentials saved to ~/.config/dotcode/auth.json");
        return 0;
    }
    case "auth" when argv.Count > 1 && argv[1] == "logout":
    {
        var auth = AuthStore.Load() with { GatewayJwt = null, GatewayRefresh = null };
        auth.Save();
        Console.WriteLine("Gateway credentials cleared.");
        return 0;
    }
    case "auth" when argv.Count > 2 && argv[1] == "set-key":
    {
        var provider = argv[2];
        var entry = ProviderCatalog.Find(provider);
        if (entry is null) { Console.Error.WriteLine($"Unknown provider '{provider}'. See: dotcode provider list"); return 1; }
        if (argv.Count < 4) { Console.Error.WriteLine("Usage: dotcode auth set-key <provider> <key>"); return 1; }
        var auth = AuthStore.Load();
        var keys = new Dictionary<string, string>(auth.ProviderKeys ?? new()) { [entry.Id] = argv[3] };
        (auth with { ProviderKeys = keys }).Save();
        Console.WriteLine($"Saved key for {entry.Id}.");
        return 0;
    }
    case "provider" when argv.Count > 1 && argv[1] == "list":
    {
        foreach (var p in ProviderCatalog.All)
            Console.WriteLine($"{p.Id,-16} {p.Name,-32} auth={p.Auth} openai_compat={p.OpenAiCompatible}");
        return 0;
    }
    case "serve":
        Console.WriteLine("Run the server with: dotnet run --project src/DotCode.Server");
        Console.WriteLine("(dotnet publish single-file + zstd self-update lands in a later phase.)");
        return 0;
    default:
        Console.Error.WriteLine($"Unknown command '{argv[0]}'. See: dotcode --help");
        return 1;
}
