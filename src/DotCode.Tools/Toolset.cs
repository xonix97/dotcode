using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileSystemGlobbing;

namespace DotCode.Tools;

/// <summary>Workspace root guard shared by all file tools (mirrors external_directory permission).</summary>
public sealed class Workspace(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(Root, path));
        if (!full.Equals(Root, StringComparison.Ordinal) && !full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Path '{path}' is outside the workspace. Allow it via external_directory permission.");
        return full;
    }
}

/// <summary>File and shell tools for the dotcode agent.</summary>
public sealed class DotCodeToolset(Workspace workspace)
{
    private readonly Workspace _workspace = workspace;
    private DotCode.Core.Sessions.ITodoStore? _todoStore;
    private string? _sessionId;

    // ---- per-turn state (OpenCode-style guards) ----
    private readonly HashSet<string> _readFiles = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxBashOutputChars = 30_000;
    private const int MaxBashTimeoutMs = 300_000;

    /// <summary>Resets per-turn state (call at the start of each agent turn).</summary>
    public void BeginTurn() => _readFiles.Clear();

    /// <summary>Sets the session context so TodoWrite/TodoRead hit the right list. Returns this for chaining.</summary>
    public DotCodeToolset WithSession(DotCode.Core.Sessions.ITodoStore? todoStore, string? sessionId)
    {
        _todoStore = todoStore;
        _sessionId = sessionId;
        return this;
    }

    [Description("Read a file's contents. Supports offset/limit for large files (1-indexed offset).")]
    public string Read(
        [Description("Path relative to the workspace root.")] string path,
        [Description("1-indexed first line to return.")] int offset = 1,
        [Description("Max lines to return (0 = all).")] int limit = 0)
    {
        var full = _workspace.Resolve(path);
        if (!File.Exists(full)) return $"Error: file not found: {path}";
        var lines = File.ReadAllLines(full);
        int start = Math.Clamp(offset - 1, 0, lines.Length);
        int count = limit <= 0 ? lines.Length - start : Math.Min(limit, lines.Length - start);
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            var line = lines[start + i];
            if (line.Length > 2_000) line = line[..2_000] + "…";
            sb.AppendLine($"{start + i + 1}: {line}");
        }
        _readFiles.Add(full); // edit guard: Write/Edit require a fresh Read this turn
        return sb.ToString();
    }

    [Description("Find files by glob pattern (e.g. **/*.cs, src/**/*.ts). Sorted by modification time.")]
    public string Glob([Description("Glob pattern relative to workspace root.")] string pattern)
    {
        var matcher = new Matcher();
        matcher.AddInclude(pattern.TrimStart('/'));
        var hits = matcher.GetResultsInFullPath(_workspace.Root)
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .Take(100)
            .Select(f => Path.GetRelativePath(_workspace.Root, f));
        return string.Join("\n", hits);
    }

    [Description("Search file contents with a regular expression. Returns path:line matches (max 50).")]
    public string Grep(
        [Description("Regex pattern to search for.")] string pattern,
        [Description("Optional glob to filter files (e.g. *.cs).")] string? include = null)
    {
        Regex rx;
        try { rx = new Regex(pattern, RegexOptions.Compiled); }
        catch (Exception ex) { return $"Error: invalid regex: {ex.Message}"; }
        var sb = new StringBuilder();
        int found = 0;
        foreach (var file in Directory.EnumerateFiles(_workspace.Root, "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")) continue;
            if (include != null && !MatchesGlob(Path.GetFileName(file), include)) continue;
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch { continue; }
            for (int i = 0; i < lines.Length; i++)
            {
                if (rx.IsMatch(lines[i]))
                {
                    sb.AppendLine($"{Path.GetRelativePath(_workspace.Root, file)}:{i + 1}: {lines[i].Trim()}");
                    if (++found >= 50) return sb.ToString();
                }
            }
        }
        return found == 0 ? "No matches." : sb.ToString();
    }

    [Description("Execute a shell command in the workspace (PowerShell on Windows, bash on Unix). For terminal work only (git, npm, dotnet, python) — use Read/Grep/Glob/Edit for file operations. Returns exit code + output; oversized output is saved to a file you can Read/Grep.")]
    public async Task<string> Bash(
        [Description("Shell command to run. Chain dependent commands with &&; do not use newlines.")] string command,
        [Description("Timeout in milliseconds (1000–300000).")] int timeoutMs = 120000)
    {
        // Windows: prefer PowerShell (pwsh 7, fallback powershell 5.1). Unix: $SHELL or /bin/bash.
        string shell, shellArg;
        if (OperatingSystem.IsWindows())
        {
            var pwsh = Environment.GetEnvironmentVariable("DOTCODE_SHELL");
            if (string.IsNullOrWhiteSpace(pwsh))
            {
                var pwsh7 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
                pwsh = File.Exists(pwsh7) ? pwsh7
                    : WhereInPath("pwsh.exe") ?? WhereInPath("powershell.exe") ?? "powershell.exe";
            }
            shell = pwsh;
            shellArg = "-Command";
        }
        else
        {
            var sh = Environment.GetEnvironmentVariable("SHELL");
            shell = string.IsNullOrWhiteSpace(sh) || !File.Exists(sh) ? "/bin/bash" : sh!;
            shellArg = "-c";
        }
        timeoutMs = Math.Clamp(timeoutMs, 1_000, 300_000);
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shell,
                WorkingDirectory = _workspace.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        proc.StartInfo.ArgumentList.Add(shellArg);
        proc.StartInfo.ArgumentList.Add(command);
        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return $"Error: timed out after {timeoutMs}ms.\n{outSb}";
        }
        if (errSb.Length > 0) outSb.AppendLine($"[stderr]\n{errSb}");
        var text = $"[exit {proc.ExitCode}]\n{outSb}".TrimEnd();
        if (text.Length <= MaxBashOutputChars) return text;
        // Persist overflow to a scratch file so nothing is lost; point Read/Grep at it.
        var overflow = Path.Combine(Path.GetTempPath(), "dotcode", $"bash-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(overflow)!);
        File.WriteAllText(overflow, text);
        return text[..MaxBashOutputChars]
            + $"\n… (output truncated; FULL output saved to {overflow} — use Read with offset or Grep it)";
    }

    [Description("Modify a file by replacing exact text. You MUST Read the file in this turn before editing. Fails when oldString is missing or ambiguous.")]
    public string Edit(
        [Description("Path relative to workspace root.")] string path,
        [Description("Exact text to replace — copy it verbatim from a fresh Read, including indentation.")] string oldString,
        [Description("Replacement text.")] string newString,
        [Description("Replace all occurrences instead of exactly one.")] bool replaceAll = false)
    {
        var full = _workspace.Resolve(path);
        if (!_readFiles.Contains(full))
            return $"Error: read {path} with the Read tool before editing it.";
        if (!File.Exists(full)) return $"Error: file not found: {path}";
        var content = File.ReadAllText(full);
        int count = CountOccurrences(content, oldString);
        if (count == 0) return $"Error: oldString not found in {path}.";
        if (count > 1 && !replaceAll) return $"Error: oldString matches {count} times in {path}; use replaceAll or add context.";
        File.WriteAllText(full, replaceAll ? content.Replace(oldString, newString) : ReplaceFirst(content, oldString, newString));
        return $"OK: edited {path} ({(replaceAll ? count : 1)} replacement(s)).";
    }

    [Description("Create a new file or overwrite an existing one. If the file already exists, you MUST Read it first this turn.")]
    public string Write(
        [Description("Path relative to workspace root.")] string path,
        [Description("Full file content.")] string content)
    {
        var full = _workspace.Resolve(path);
        if (File.Exists(full) && !_readFiles.Contains(full))
            return $"Error: {path} already exists — Read it before overwriting.";
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return $"OK: wrote {path} ({content.Length} chars).";
    }

    [Description("List the directory tree (files + folders, depth-limited). Faster than many Glob calls for getting oriented.")]
    public string Tree(
        [Description("Directory relative to workspace root ('.' for the root).")] string path = ".",
        [Description("Max depth to descend (default 3).")] int depth = 3)
    {
        var full = _workspace.Resolve(path);
        if (!Directory.Exists(full)) return $"Error: not a directory: {path}";
        var sb = new StringBuilder();
        int shown = 0;
        const int MaxEntries = 500;
        void Walk(string dir, int level)
        {
            if (level > depth || shown >= MaxEntries) return;
            foreach (var entry in Directory.GetFileSystemEntries(dir))
            {
                if (shown >= MaxEntries) { sb.AppendLine("… (limit reached)"); return; }
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj") continue;
                var rel = Path.GetRelativePath(full, entry);
                sb.AppendLine(Directory.Exists(entry) ? rel + "/" : rel);
                shown++;
                if (Directory.Exists(entry)) Walk(entry, level + 1);
            }
        }
        Walk(full, 1);
        return sb.Length == 0 ? "(empty)" : sb.ToString().TrimEnd();
    }

    [Description("Replace the session's todo list. Use to track multi-step work; mark items done as you go.")]
    public string TodoWrite(
        [Description("Full todo list replacing the previous one, in order.")] TodoInput[] todos)
    {
        if (_todoStore is null || _sessionId is null)
            return "Error: todo store unavailable (no session context).";
        var list = todos.Select(t => new DotCode.Core.Models.Todo(
            Guid.NewGuid().ToString("n")[..8], _sessionId, t.content, t.done)).ToList();
        _todoStore.ReplaceAll(_sessionId, list);
        return $"OK: {list.Count} todo(s) saved.";
    }

    [Description("Read the current todo list for this session.")]
    public string TodoRead()
    {
        if (_todoStore is null || _sessionId is null)
            return "Error: todo store unavailable (no session context).";
        var list = _todoStore.List(_sessionId);
        if (list.Count == 0) return "(no todos)";
        var sb = new StringBuilder();
        foreach (var t in list)
            sb.AppendLine($"{(t.Done ? "[x]" : "[ ]")} {t.Content}");
        return sb.ToString();
    }

    public sealed record TodoInput(string content, bool done = false);

    public IReadOnlyList<AITool> AsAITools() =>
        GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => m.Name is not nameof(AsAITools) and not nameof(WithSession))
            .Select(m => (AITool)AIFunctionFactory.Create(m, this))
            .ToList();

    private static bool MatchesGlob(string fileName, string pattern)
    {
        var m = new Matcher();
        m.AddInclude(pattern);
        return m.Match(fileName).HasMatches;
    }

    private static int CountOccurrences(string text, string sub)
    {
        if (string.IsNullOrEmpty(sub)) return 0;
        int n = 0, i = 0;
        while ((i = text.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        int i = text.IndexOf(oldValue, StringComparison.Ordinal);
        return text[..i] + newValue + text[(i + oldValue.Length)..];
    }

    private static string? WhereInPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            try { if (dir.Length > 0 && File.Exists(Path.Combine(dir, fileName))) return Path.Combine(dir, fileName); }
            catch { }
        }
        return null;
    }
}
