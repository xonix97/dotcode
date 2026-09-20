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
            sb.AppendLine($"{start + i + 1}: {lines[start + i]}");
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

    [Description("Execute a shell command in the workspace (PowerShell on Windows, bash on Unix). Returns exit code + output.")]
    public async Task<string> Bash(
        [Description("Shell command to run.")] string command,
        [Description("Timeout in milliseconds.")] int timeoutMs = 120000)
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
        return $"[exit {proc.ExitCode}]\n{outSb}".TrimEnd();
    }

    [Description("Modify a file by replacing exact text. Fails when oldString is missing or ambiguous.")]
    public string Edit(
        [Description("Path relative to workspace root.")] string path,
        [Description("Exact text to replace.")] string oldString,
        [Description("Replacement text.")] string newString,
        [Description("Replace all occurrences instead of exactly one.")] bool replaceAll = false)
    {
        var full = _workspace.Resolve(path);
        if (!File.Exists(full)) return $"Error: file not found: {path}";
        var content = File.ReadAllText(full);
        int count = CountOccurrences(content, oldString);
        if (count == 0) return $"Error: oldString not found in {path}.";
        if (count > 1 && !replaceAll) return $"Error: oldString matches {count} times in {path}; use replaceAll or add context.";
        File.WriteAllText(full, replaceAll ? content.Replace(oldString, newString) : ReplaceFirst(content, oldString, newString));
        return $"OK: edited {path} ({(replaceAll ? count : 1)} replacement(s)).";
    }

    [Description("Create a new file or overwrite an existing one.")]
    public string Write(
        [Description("Path relative to workspace root.")] string path,
        [Description("Full file content.")] string content)
    {
        var full = _workspace.Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return $"OK: wrote {path} ({content.Length} chars).";
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
