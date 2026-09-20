using DotCode.Core.Permissions;

namespace DotCode.Core.Tests;

public class PermissionEngineTests
{
    [Theory]
    [InlineData("git *", "git status", true)]
    [InlineData("git *", "git", false)]
    [InlineData("*", "rm -rf /", true)]
    [InlineData("*.env", "a.env", true)]
    [InlineData("*.env", "a.env.example", false)]
    public void Matches_Wildcards(string pattern, string input, bool expected)
        => Assert.Equal(expected, PermissionEngine.Matches(pattern, input));

    [Fact]
    public void Resolve_LastWins()
    {
        var rules = new List<PermissionRule>
        {
            new("*", PermissionAction.Ask),
            new("git status*", PermissionAction.Allow),
            new("git push*", PermissionAction.Deny),
        };
        Assert.Equal(PermissionAction.Allow, PermissionEngine.Resolve(rules, "git status --porcelain"));
        Assert.Equal(PermissionAction.Deny, PermissionEngine.Resolve(rules, "git push origin"));
        Assert.Equal(PermissionAction.Ask, PermissionEngine.Resolve(rules, "rm -rf /"));
    }

    [Fact]
    public void Resolve_NoRules_Allows()
        => Assert.Equal(PermissionAction.Allow, PermissionEngine.Resolve(new List<PermissionRule>(), "anything"));
}
