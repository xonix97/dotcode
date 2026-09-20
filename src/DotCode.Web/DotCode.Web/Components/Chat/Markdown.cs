using Markdig;

namespace DotCode.Web.Components.Chat;

public static class Markdown
{
    private static readonly Markdig.MarkdownPipeline Pipe = new Markdig.MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static string ToHtml(string markdown) => Markdig.Markdown.ToHtml(markdown ?? "", Pipe);
}
