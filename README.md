# dotcode

**A self-hosted, local-first AI coding agent** — a Blazor/.NET port of the OpenCode workflow. Talk to an agent that reads, searches, runs and edits code in *your* chosen directory, through *your* choice of model provider — including **100% free options**.

![.NET](https://img.shields.io/badge/.NET-9.0-512BD4) ![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-blue) ![License](https://img.shields.io/badge/license-MIT-green)

- 🖥️ **Web UI** (terminal-style, dark) at `http://localhost:5131` — streaming responses, live tool calls, thinking blocks
- 📁 **Pick any working directory** — the agent operates only inside it
- ⚙️ **Per-session settings** — temperature, max steps, extra instructions, auto-approve
- 🔌 **Any OpenAI-compatible provider** — free tiers, local models (Ollama, LM Studio, 9router), or your own proxy
- ⏪ **Undo / revert to any message**, `/init` to generate `AGENTS.md`

## Quick start

### Linux / macOS

```bash
curl -fsSL https://raw.githubusercontent.com/xonix97/dotcode/main/install.sh | bash
```

Then:

```bash
dotcode
```

…which starts both the agent server (`:4096`) and the web UI (`http://localhost:5131`), and opens your browser.

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/xonix97/dotcode/main/install.ps1 | iex
```

Then (from a **new** terminal):

```powershell
dotcode
```

### Windows (CMD)

```cmd
curl -fsSL https://raw.githubusercontent.com/xonix97/dotcode/main/install.bat -o dotcode-install.bat && dotcode-install.bat
```

### Manual (any OS)

Requirements: [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```bash
git clone https://github.com/xonix97/dotcode.git
cd dotcode
dotnet build DotCode.sln
# terminal 1:
dotnet run --project src/DotCode.Server --no-launch-profile
# terminal 2:
dotnet run --project src/DotCode.Web/DotCode.Web --no-launch-profile
```

## First run

1. Open **http://localhost:5131**
2. Click the **workspace directory** bar to pick the folder the agent works in
3. Go to **Connect** (or the `⤴ Connect a model →` link) and connect any provider — see below
4. Start a session and type a task. Watch the prompt, thinking, and tool calls stream in live

---

## 🔌 Adding providers — the complete guide

dotcode speaks the **OpenAI Chat Completions** protocol. Any server that exposes `/v1/chat/completions` works. There are three ways to connect one:

### Way 1 — Built-in provider (one key)

Open **Connect** in the web UI, pick a card, paste the key, done. Each card links to where you get a free key.

| Provider | Base URL (built-in) | Get a key | Notes |
|---|---|---|---|
| **Google Gemini (AI Studio)** | `https://generativelanguage.googleapis.com/v1beta/openai` | [aistudio.google.com/apikey](https://aistudio.google.com/apikey) | Generous free tier, no credit card |
| **Groq** | `https://api.groq.com/openai/v1` | [console.groq.com/keys](https://console.groq.com/keys) | Free tier, extremely fast |
| **Mistral** | `https://api.mistral.ai/v1` | [console.mistral.ai/api-keys](https://console.mistral.ai/api-keys) | Free mode by default |
| **Cerebras** | `https://api.cerebras.ai/v1` | [cloud.cerebras.ai](https://cloud.cerebras.ai) | Free tier |
| **GitHub Models** | `https://models.github.ai/inference` | [new PAT](https://github.com/settings/personal-access-tokens/new) | Free with a GitHub account |
| **OpenRouter** | `https://openrouter.ai/api/v1` | [openrouter.ai/keys](https://openrouter.ai/keys) | `:free`-suffixed models cost nothing |
| **Ollama (local)** | `http://localhost:11434/v1` | — no key — | `ollama serve` |
| **LM Studio (local)** | `http://localhost:1234/v1` | — no key — | Start the local server |
| **9router (local)** | `http://localhost:20128/v1` | — auto-detected — | dotcode reads its key from `~/.9router/db/data.sqlite` automatically |

Keys are stored in `~/.config/dotcode/auth.json` (0600 on Unix), never in project files.

### Way 2 — Custom provider via **baseURL + API key + model id**

For anything else — vLLM, llama.cpp server, DeepSeek, xAI, Together, Fireworks, OpenAI itself, a corporate proxy — use the **Custom provider** card on the Connect page:

1. **Display name** — anything, e.g. `My proxy`
2. **Base URL** — the root that `/chat/completions` hangs off, e.g. `https://api.deepseek.com/v1`
3. **API key** — paste it (leave empty for keyless local servers)
4. **Default model id** — the exact id the server reports, e.g. `deepseek-chat`

Click **Add provider**. dotcode:

- saves the key under `~/.config/dotcode/auth.json` as `custom-<yourname>`
- writes the `baseURL` into `dotcode.json` in your workspace:

```json
{
  "provider": {
    "custom-myproxy": {
      "name": "My proxy",
      "options": { "baseURL": "https://api.example.com/v1" }
    }
  }
}
```

5. In a session, click **🤖** → your custom provider → pick a model (the list is fetched live from `<baseURL>/models`; if that fails, type any model id by sending with the default)

> **How to find the right values:** the *base URL* is what you'd put in an OpenAI SDK's `base_url` — it almost always ends in `/v1` (or `/v1beta/openai` for Gemini, `/inference` for GitHub Models). The *model id* is case-sensitive and must match exactly what `GET <baseURL>/models` returns. Test with:
>
> ```bash
> curl <baseURL>/models -H "Authorization: Bearer <key>"
> curl <baseURL>/chat/completions -H "Authorization: Bearer <key>" \
>   -H "Content-Type: application/json" \
>   -d '{"model":"<model-id>","messages":[{"role":"user","content":"ping"}]}'
> ```

### Way 3 — Config file (hand-written)

Create `dotcode.json` in your workspace (or `~/.config/dotcode/dotcode.json` for global):

```json
{
  "model": "custom-myproxy/deepseek-chat",
  "provider": {
    "custom-myproxy": {
      "name": "My proxy",
      "options": {
        "baseURL": "https://api.example.com/v1",
        "apiKey": "{env:MY_PROXY_KEY}"
      }
    }
  }
}
```

`apiKey` supports `{env:VAR}` and `{file:~/path}` expansion. Precedence: global config → `DOTCODE_CONFIG` env → project `dotcode.json[c]` (`opencode.json[c]` also read for compatibility).

### Default model

Set once, applies everywhere:

```bash
export DOTCODE_MODEL="groq/openai/gpt-oss-120b"     # provider/model-id
```

or per-session in the UI with the **🤖** picker.

---

## Settings

| Setting | Where | Effect |
|---|---|---|
| Working directory | Home page → workspace bar | Roots the agent's file/shell tools (picker browses the whole FS) |
| Temperature | ⚙ settings | Sampling temperature (0–2) |
| Max steps | ⚙ settings | Tool-call loop budget per turn (default 25) |
| Extra instructions | ⚙ settings | Appended to the system prompt every turn |
| Auto-approve | ⚙ settings | Skip the permission gate for mutating tools |
| AGENTS.md | project root | Project instructions injected into the system prompt (`/init` generates one) |

## Architecture

```
┌────────────────┐  SSE events   ┌─────────────────┐
│  DotCode.Web   │◀──────────────│  DotCode.Server │
│  Blazor :5131  │  REST :4096   │  agent loop     │
└────────────────┘               └────────┬────────┘
        │                                  │
        ▼                                  ▼
  your browser                    IChatClient (OpenAI protocol)
                                         │
                            ┌────────────┼────────────┐
                            ▼            ▼            ▼
                        Gemini       Groq/Ollama   your proxy
```

- **DotCode.Core** — config loader, session/message/todo stores, permissions
- **DotCode.Providers** — provider catalog + endpoint resolver (+ 9router key auto-discovery)
- **DotCode.Tools** — the agent toolset (read/write/edit/bash/glob/grep/todo)
- **DotCode.Agent** — the manual agentic loop with permission gating
- **DotCode.Server** — minimal API host (opencode-compatible routes, SSE `/event`)
- **DotCode.Web** — Blazor Server UI (Tailwind-free, hand-rolled TUI theme)

Sessions live in `~/.local/share/dotcode/` (`XDG_DATA_HOME` respected) — plain JSON, easy to back up.

## Windows notes

- Fully supported: the Bash tool auto-selects **PowerShell** (pwsh 7 → 5.1) or honors `DOTCODE_SHELL`
- Paths: use normal Windows paths (`C:\Users\you\project`) in the directory picker
- Config: `%USERPROFILE%\.config\dotcode\` (same layout as Unix)

## Troubleshooting

| Symptom | Fix |
|---|---|
| `Provider 'X' is not connected` | Connect page → paste key, or `export X_API_KEY=…` |
| `no models found` in picker | Provider not connected, or `/models` blocked — send anyway with a typed model id |
| Blank/unstyled page | Hard-refresh (Ctrl+Shift+R); web app must run in Development for static assets |
| 9router models fail | Is `9router` running? Check `http://localhost:20128/v1/models` |
| Port 4096/5131 busy | Kill the stale process or pass `--urls` / edit `Program.cs` |

## Contributing

PRs welcome. Build with `dotnet build DotCode.sln`, test with `dotnet test DotCode.sln`.

## License

MIT
