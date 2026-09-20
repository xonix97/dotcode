using DotCode.Core.Config;
using DotCode.Core.Permissions;

namespace DotCode.Core.Tests;

public class PermissionParserTests
{
    [Fact]
    public void Parse_NullOrNonObject_ReturnsNoRules()
    {
        Assert.Empty(PermissionParser.Parse(null));
        Assert.Empty(PermissionParser.Parse(System.Text.Json.JsonSerializer.SerializeToElement(new { })));
    }

    [Fact]
    public void Parse_SimpleForm_MapsActions()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["edit"] = "allow",
            ["bash"] = "ask",
            ["*"] = "deny",
        });
        var rules = PermissionParser.Parse(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json));

        Assert.Equal(PermissionAction.Allow, PermissionEngine.Resolve(rules, "edit:any/path"));
        Assert.Equal(PermissionAction.Ask, PermissionEngine.Resolve(rules, "bash:anything"));
        Assert.Equal(PermissionAction.Deny, PermissionEngine.Resolve(rules, "grep:whatever"));
    }

    [Fact]
    public void Parse_ObjectForm_KeepsPatternSpecificity()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["bash"] = new Dictionary<string, object>
            {
                ["git *"] = "allow",
                ["*"] = "ask",
            },
        });
        var rules = PermissionParser.Parse(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json));

        Assert.Equal(PermissionAction.Allow, PermissionEngine.Resolve(rules, "bash:git status"));
        Assert.Equal(PermissionAction.Ask, PermissionEngine.Resolve(rules, "bash:rm -rf /"));
    }

    [Fact]
    public void Parse_ArrayForm_ExpandsAllEntries()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["edit"] = new object[] { "allow", new Dictionary<string, object> { ["~/.env"] = "deny" } },
        });
        var rules = PermissionParser.Parse(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json));

        Assert.Equal(PermissionAction.Deny, PermissionEngine.Resolve(rules, "edit:~/.env"));
        Assert.Equal(PermissionAction.Allow, PermissionEngine.Resolve(rules, "edit:src/main.cs"));
    }
}

public class ToolGateTests
{
    private static IReadOnlyList<PermissionRule> Rules(params (string, PermissionAction)[] tuples)
        => tuples.Select(t => new PermissionRule(t.Item1, t.Item2)).ToList();

    [Fact]
    public async Task ReadOnlyTools_AlwaysAllowed()
    {
        var gate = new ToolGate(autoApprove: false);
        Assert.True(await gate.CheckAsync("read", "{}", null));
        Assert.True(await gate.CheckAsync("grep", "{}", Rules(("*", PermissionAction.Deny))));
        Assert.True(await gate.CheckAsync("todowrite", "{}", null));
    }

    [Fact]
    public async Task AutoApprove_AllowsEverything()
    {
        var gate = new ToolGate(autoApprove: true);
        Assert.True(await gate.CheckAsync("bash", "{\"command\":\"rm -rf /\"}", Rules(("*", PermissionAction.Deny))));
    }

    [Fact]
    public async Task DenyRule_BlocksEvenWithoutAutoApprove()
    {
        var gate = new ToolGate(autoApprove: false);
        Assert.False(await gate.CheckAsync("bash", "{\"command\":\"git push\"}", Rules(("bash:git push*", PermissionAction.Deny))));
    }

    [Fact]
    public async Task UnmatchedMutatingTool_AsksByDefault()
    {
        var prompted = false;
        var gate = new ToolGate(false, _ => { prompted = true; return Task.FromResult(true); });
        Assert.True(await gate.CheckAsync("edit", "{\"path\":\"a.txt\"}", null));
        Assert.True(prompted);

        var gateNoPrompt = new ToolGate(false);
        Assert.False(await gateNoPrompt.CheckAsync("edit", "{\"path\":\"a.txt\"}", null));
    }

    [Fact]
    public async Task AllowRule_SkipsPrompt()
    {
        var gate = new ToolGate(false, _ => throw new InvalidOperationException("should not prompt"));
        Assert.True(await gate.CheckAsync("bash", "{\"command\":\"git status\"}", Rules(("bash:git status*", PermissionAction.Allow))));
    }
}
