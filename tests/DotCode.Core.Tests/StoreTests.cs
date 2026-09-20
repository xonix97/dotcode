using DotCode.Core.Models;
using DotCode.Core.Sessions;

namespace DotCode.Core.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotcode-test-" + Guid.NewGuid().ToString("n")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void FileMessageStore_PersistsAcrossInstances()
    {
        var dir = Path.Combine(_root, "messages");
        var s1 = new FileMessageStore(dir);
        s1.AppendUserText("sess-a", "hello");
        var aid = s1.BeginAssistantTurn("sess-a", "build", "ollama/m");
        s1.AppendAssistantTextPart(aid, "hi there");
        s1.AppendToolCall(aid, "Read", "{\"path\":\"a.txt\"}", "call-1");
        s1.AppendToolResult(aid, "call-1", "Read", "1: contents", isError: false);

        var s2 = new FileMessageStore(dir);
        var entries = s2.List("sess-a");
        Assert.Equal(2, entries.Count);
        Assert.Equal(MessageRole.User, entries[0].Info.Role);
        var parts = entries[1].Parts;
        Assert.Equal(3, parts.Count);
        Assert.IsType<ToolResultPart>(parts[2]);
        Assert.Empty(s2.List("sess-b"));
    }

    [Fact]
    public void FileMessageStore_Revert_RemovesMessageAndEverythingAfter()
    {
        var dir = Path.Combine(_root, "messages-revert");
        var s = new FileMessageStore(dir);
        s.AppendUserText("sess", "one");
        var aid = s.BeginAssistantTurn("sess");
        s.AppendAssistantTextPart(aid, "reply one");
        s.AppendUserText("sess", "two");
        s.AppendUserText("sess", "three");

        var entries = s.List("sess");
        Assert.Equal(4, entries.Count);
        var mid = entries[1].Info.Id;

        Assert.True(s.Revert("sess", mid));
        var after = s.List("sess");
        Assert.Single(after);
        Assert.Equal("one", Assert.IsType<TextPart>(after[0].Parts[0]).Text);

        // Reload from disk: revert persisted.
        var reloaded = new FileMessageStore(dir);
        Assert.Single(reloaded.List("sess"));
    }

    [Fact]
    public void InMemoryMessageStore_ListAndLimit()
    {
        var s = new InMemoryMessageStore();
        s.AppendUserText("sess", "a");
        s.AppendUserText("sess", "b");
        s.AppendUserText("sess", "c");
        Assert.Equal(3, s.List("sess").Count);
        Assert.Equal(2, s.List("sess", limit: 2).Count);
        Assert.Equal("b", Assert.IsType<TextPart>(s.List("sess", limit: 2)[0].Parts[0]).Text);
    }

    [Fact]
    public void FileTodoStore_ListReplaceTogglePersist()
    {
        var dir = Path.Combine(_root, "todos");
        var store = new FileTodoStore(dir);

        store.ReplaceAll("s1", new[]
        {
            new Models.Todo("t1", "s1", "do a thing", false),
            new Models.Todo("t2", "s1", "already done", true),
        });
        Assert.Equal(2, store.List("s1").Count);
        Assert.Empty(store.List("s2"));

        store.Toggle("s1", "t1");
        Assert.True(store.List("s1").First(t => t.Id == "t1").Done);

        var reloaded = new FileTodoStore(dir);
        Assert.True(reloaded.List("s1").First(t => t.Id == "t1").Done);
    }
}
