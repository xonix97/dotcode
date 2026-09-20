using DotCode.Tools;

namespace DotCode.Tools.Tests;

public sealed class ToolsetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotcode-test-" + Guid.NewGuid().ToString("n")[..8]);
    private readonly DotCodeToolset _tools;

    public ToolsetTests()
    {
        Directory.CreateDirectory(_root);
        _tools = new DotCodeToolset(new Workspace(_root));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Write_Read_Roundtrip()
    {
        Assert.StartsWith("OK", _tools.Write("a.txt", "hello\nworld\n"));
        var content = _tools.Read("a.txt");
        Assert.Contains("1: hello", content);
        Assert.Contains("2: world", content);
    }

    [Fact]
    public void Edit_ReplacesExactlyOnce()
    {
        _tools.Write("b.txt", "foo bar foo");
        _tools.BeginTurn();
        // read-before-edit guard: editing without a fresh Read must fail
        Assert.StartsWith("Error: read", _tools.Edit("b.txt", "foo", "baz"));
        Assert.StartsWith("1:", _tools.Read("b.txt"));
        Assert.StartsWith("Error", _tools.Edit("b.txt", "foo", "baz")); // ambiguous: 2 matches
        Assert.StartsWith("OK", _tools.Edit("b.txt", "foo", "baz", replaceAll: true));
        Assert.Contains("baz bar baz", _tools.Read("b.txt", limit: 5));
    }

    [Fact]
    public void Write_ExistingFile_RequiresReadFirst()
    {
        Assert.StartsWith("OK", _tools.Write("c.txt", "v1"));
        _tools.BeginTurn();
        Assert.StartsWith("Error:", _tools.Write("c.txt", "v2")); // exists, not read this turn
        _tools.Read("c.txt");
        Assert.StartsWith("OK", _tools.Write("c.txt", "v2"));
        Assert.Contains("v2", _tools.Read("c.txt"));
    }

    [Fact]
    public void Tree_ListsWorkspaceEntries()
    {
        _tools.Write("src/deep/code.cs", "class C {}");
        var tree = _tools.Tree(".", depth: 3);
        Assert.Contains("src/", tree);
        Assert.Contains(Path.Combine("src", "deep") + "/", tree);
        Assert.Contains(Path.Combine("src", "deep", "code.cs"), tree);
    }

    [Fact]
    public void Paths_OutsideWorkspace_AreBlocked()
    {
        Assert.Throws<UnauthorizedAccessException>(() => _tools.Read("../escape.txt"));
        Assert.StartsWith("OK", _tools.Write("ok.txt", "x"));
    }

    [Fact]
    public void Glob_FindsWrittenFile()
    {
        _tools.Write("src/code.cs", "class C {}");
        Assert.Contains(Path.Combine("src", "code.cs"), _tools.Glob("src/**/*.cs"));
    }

    [Fact]
    public void Grep_FindsPattern()
    {
        _tools.Write("g.txt", "needle in haystack\nnothing here");
        var result = _tools.Grep("needle");
        Assert.Contains("g.txt:1", result);
    }

    [Fact]
    public async Task Bash_RunsAndCapturesExitCode()
    {
        var result = await _tools.Bash("echo hi && exit 3");
        Assert.Contains("[exit 3]", result);
        Assert.Contains("hi", result);
    }

    [Fact]
    public void AsAITools_ExposesTenFunctions()
    {
        var tools = _tools.AsAITools();
        Assert.Equal(9, tools.Count); // MarkPlanned and HasPlan are harness-only, never exposed
        Assert.DoesNotContain(tools, t => t.Name.Contains("MarkPlanned") || t.Name.Contains("HasPlan"));
        Assert.Contains(tools, t => t.Name.Equals("Read", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tools, t => t.Name.Equals("Bash", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tools, t => t.Name.Equals("Tree", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tools, t => t.Name.Equals("TodoWrite", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tools, t => t.Name.Equals("TodoRead", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TodoWrite_WithoutSessionContext_ReturnsError()
    {
        var result = _tools.TodoWrite(new[] { new DotCodeToolset.TodoInput("step one") });
        Assert.StartsWith("Error", result);
    }

    [Fact]
    public void TodoWrite_TodoRead_Roundtrip()
    {
        var dir = Path.Combine(_root, "todos");
        var store = new DotCode.Core.Sessions.FileTodoStore(dir);
        _tools.WithSession(store, "sess-1");

        Assert.StartsWith("OK", _tools.TodoWrite(new[]
        {
            new DotCodeToolset.TodoInput("first"),
            new DotCodeToolset.TodoInput("second", done: true),
        }));
        var read = _tools.TodoRead();
        Assert.Contains("[ ] first", read);
        Assert.Contains("[x] second", read);

        // A new store instance reloads from disk.
        var reloaded = new DotCode.Core.Sessions.FileTodoStore(dir);
        Assert.Equal(2, reloaded.List("sess-1").Count);
    }
}
