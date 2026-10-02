using System.Reflection;
using System.Text.Json;
using ChronoIsle.App.Services.Knowledge;
using ChronoIsle.App.Services.Markdown;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ChronoIsle.Tests;

public sealed class KnowledgeAnswerImageTests
{
    const string BaseUri = "https://kb.example.test/knowledge/v1/files/0123456789abcdef0123456789abcdef/";
    const string Quote = "需要先核对资料中的流程。";

    [Fact]
    public void Fallback_keeps_complete_image_markup_and_resolves_it_from_its_own_note()
    {
        var text = Quote + "\n\n" + new string('文', 560) + "\n\n![image.png](../附件/long-image-name%20with%20spaces.png)\n\n图片后的完整上下文。";
        var answer = Render("invalid-json", Source(text));
        Assert.True(answer.IsFailure);
        Assert.Contains("图片后的完整上下文。", answer.Reply);
        var image = Assert.Single(Images(answer.Reply));
        Assert.Equal(BaseUri + "sMES/%E9%99%84%E4%BB%B6/long-image-name%20with%20spaces.png", image.Url);
    }

    [Fact]
    public void Verified_answer_includes_only_images_from_cited_evidence_and_deduplicates_them()
    {
        var source = Source(Quote + "\n![原图](../附件/image.png)\n![重复](../附件/image.png)");
        var answer = Render(ValidAnswer(), source);
        Assert.False(answer.IsFailure);
        Assert.Equal(BaseUri + "sMES/%E9%99%84%E4%BB%B6/image.png", Assert.Single(Images(answer.Reply)).Url);
        Assert.Contains("原文配图", answer.Reply);
    }

    [Fact]
    public void Model_invented_image_is_rejected_while_real_source_image_remains_available()
    {
        var answer = Render(ValidAnswer("![伪造](https://tracking.invalid/fake.png)"), Source(Quote + "\n![原图](../附件/image.png)"));
        Assert.True(answer.IsFailure);
        Assert.DoesNotContain("tracking.invalid", answer.Reply);
        Assert.StartsWith(BaseUri, Assert.Single(Images(answer.Reply)).Url);
    }

    [Fact]
    public void Source_images_cannot_escape_a_remote_snapshot_and_code_examples_are_not_images()
    {
        var answer = Render(ValidAnswer(), Source(Quote + "\n![越界](../../../private.png)\n```md\n![示例](example.png)\n```"));
        Assert.False(answer.IsFailure);
        Assert.Empty(Images(answer.Reply));
    }

    static KnowledgeSearchResult Source(string text) => new("远程项目知识库", [new("S1", "sMES/说明/note.md", "说明", 1, 10,
        text, "", DateTime.UtcNow, new string('A', 64))], 1, 0, false, "0123456789abcdef0123456789abcdef", BaseUri);

    static string ValidAnswer(string text = "先核对资料中的流程。") => JsonSerializer.Serialize(new
    { insufficient = false, points = new[] { new { text, evidence = new[] { new { sourceId = "S1", quote = Quote } } } } });

    static (string Reply, bool IsFailure) Render(string response, KnowledgeSearchResult source) =>
        ((string, bool))typeof(KnowledgeQuestionService).GetMethod("ValidateAndRender", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [response, source])!;

    static LinkInline[] Images(string markdown) => Markdown.Parse(markdown, MarkdownDocuments.Pipeline).Descendants<LinkInline>().Where(l => l.IsImage).ToArray();
}
