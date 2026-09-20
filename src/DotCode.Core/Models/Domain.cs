using System.Text.Json.Serialization;

namespace DotCode.Core.Models;

public enum MessageRole { User, Assistant, System }

public sealed record Session(
    string Id,
    string? ParentId,
    string Title,
    string ProjectId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record Message(
    string Id,
    string SessionId,
    MessageRole Role,
    string? Agent,
    string? Model,
    DateTimeOffset CreatedAt,
    string? Error = null,
    object? StructuredOutput = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ToolCallPart), "tool_call")]
[JsonDerivedType(typeof(ToolResultPart), "tool_result")]
public abstract record Part(string Id, string MessageId, string Type);
public sealed record TextPart(string Id, string MessageId, string Text) : Part(Id, MessageId, "text");
public sealed record ToolCallPart(string Id, string MessageId, string Tool, string InputJson, string? CallId = null) : Part(Id, MessageId, "tool_call");
public sealed record ToolResultPart(string Id, string MessageId, string Tool, string CallId, string Output, bool IsError = false) : Part(Id, MessageId, "tool_result");

public sealed record Todo(string Id, string SessionId, string Content, bool Done);

public sealed record AgentDefinition(
    string Name,
    string Description,
    string Mode, // primary | subagent | all
    string? Model = null,
    string? Prompt = null,
    double? Temperature = null,
    int? Steps = null,
    Dictionary<string, object>? Permission = null,
    bool Hidden = false);

public sealed record ProviderDefinition(
    string Id,
    string Name,
    Dictionary<string, string>? Env = null,
    ProviderOptions? Options = null,
    Dictionary<string, ProviderModel>? Models = null,
    string[]? Blacklist = null,
    string[]? Whitelist = null);

public sealed record ProviderOptions(string? BaseUrl = null, string? ApiKey = null, string? ResourceName = null, string? Region = null, string? Profile = null);

public sealed record ProviderModel(string? Name = null, string? Id = null, Dictionary<string, object>? Options = null);

public sealed record Project(string Id, string Name, string Root);
public sealed record FileDiff(string Path, string Before, string After);
