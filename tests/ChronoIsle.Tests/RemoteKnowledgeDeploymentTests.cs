using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Knowledge;
using ChronoIsle.App.Services.Markdown;
using Xunit.Abstractions;
using ChronoIsle.App;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ChronoIsle.Tests;

public sealed class RemoteKnowledgeDeploymentTests(ITestOutputHelper output)
{
    [RemoteImageFact]
    public async Task Remote_question_reproduces_complete_source_images_in_both_answer_paths()
    {
        const string question = "无限链表 页面关联关系 ET10 自定义数据集";
        var settings = new KnowledgeBaseSettingsService();
        var saved = settings.LoadRemote();
        Assert.NotNull(saved);
        var provider = new ProviderSettings("https://unused.invalid", "test", "not-used");
        foreach (var fallback in new[] { true, false })
        {
            var service = new KnowledgeQuestionService(settings, new ObsidianKnowledgeIndex(), new ImageEvidenceModel(fallback));
            var answer = await service.AskAsync(provider, question);
            Assert.Equal(fallback, answer.IsFailure);
            var images = Markdown.Parse(answer.Reply, MarkdownDocuments.Pipeline).Descendants<LinkInline>().Where(l => l.IsImage).ToArray();
            var actual = Assert.Single(images.Where(i => i.Url!.EndsWith("b18de828c78083e0ca5c.png", StringComparison.Ordinal)));
            var uri = new Uri(actual.Url!);
            Assert.True(RemoteKnowledgeClient.IsConfiguredDocumentUri(uri));
            Assert.False(RemoteKnowledgeClient.IsConfiguredDocumentUri(new Uri("https://external.invalid/image.png")));
            Assert.DoesNotContain("Bearer", answer.Reply);
            var bitmap = await MarkdownImages.ReadAsync(uri, thumbnail: true);
            Assert.True(bitmap.IsFrozen);
            Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0);
            output.WriteLine($"{(fallback ? "Fallback" : "Verified answer")}: screenshot source image decoded {bitmap.PixelWidth} x {bitmap.PixelHeight}; images={images.Length}.");
            var folder = Environment.GetEnvironmentVariable("CHRONOISLE_IMAGE_QA_OUTPUT");
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, fallback ? "fallback-answer.md" : "verified-answer.md"), answer.Reply);
            }
        }
    }

    sealed class ImageEvidenceModel(bool fallback) : IChatCompletionClient
    {
        public Task<string> Complete(ProviderSettings provider, IEnumerable<ModelMessage> messages, bool jsonObject = false)
        {
            if (fallback) return Task.FromResult("invalid-json");
            const string quote = "页面关联关系就更复杂了";
            using var request = JsonDocument.Parse(messages.Last().Content);
            var source = request.RootElement.GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("text").GetString()!.Contains(quote, StringComparison.Ordinal));
            return Task.FromResult(JsonSerializer.Serialize(new { insufficient = false, points = new[] { new {
                text = "原文指出，无限链表场景会使页面关联关系更复杂。", evidence = new[] { new { sourceId = source.GetProperty("id").GetString(), quote } }
            } } }));
        }
        public Task<string> Reply(ProviderSettings provider, IEnumerable<ChatMessage> history, string input) => throw new NotSupportedException();
        public Task Test(ProviderSettings provider) => throw new NotSupportedException();
    }

    [RemoteImageFact]
    public async Task Remote_image_uses_the_saved_application_credentials()
    {
        var uri = new Uri(Environment.GetEnvironmentVariable("CHRONOISLE_REMOTE_IMAGE")!);
        var saved = new KnowledgeBaseSettingsService().LoadRemote();
        Assert.NotNull(saved);
        Assert.True(new Uri(saved.BaseUrl).IsBaseOf(uri));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var image = await MarkdownImages.ReadAsync(uri, thumbnail: true, token: budget.Token);
        Assert.True(image.IsFrozen);
        Assert.True(image.PixelWidth > 0 && image.PixelHeight > 0);
        output.WriteLine($"Authenticated remote image decoded: {image.PixelWidth} x {image.PixelHeight}.");
    }

    [RemoteDeploymentFact]
    public async Task Deployed_service_reads_project_notes_and_answers_with_verified_remote_sources()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("CHRONOISLE_REMOTE_CONFIG")!));
        var endpoint = config.RootElement.GetProperty("BaseUrl").GetString()!;
        var key = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(
            config.RootElement.GetProperty("ProtectedKey").GetString()!), null, DataProtectionScope.CurrentUser));
        var client = new RemoteKnowledgeClient(new(endpoint, key));
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Assert.True(await client.TestAsync(budget.Token));
        const string question = "sMES 7.0 如何通过 openDialog 打开自定义页面？";
        var found = await client.SearchAsync(question, budget.Token);
        Assert.NotEmpty(found.Sources);
        Assert.True(found.NoteCount > 0);
        Assert.All(found.Sources, source => Assert.DoesNotContain("CodexDebug", source.RelativePath));
        Assert.True((await client.VerifyAsync(found, budget.Token)).Unchanged);
        var path = string.Join('/', found.Sources[0].RelativePath.Split('/').Select(Uri.EscapeDataString));
        using var document = await client.ReadDocumentAsync(new Uri(new Uri(found.DocumentBaseUri!), path), budget.Token);
        Assert.NotNull(document);
        Assert.True(document.IsSuccessStatusCode);
        Assert.True((await document.Content.ReadAsByteArrayAsync(budget.Token)).Length > 0);
        output.WriteLine($"Remote snapshot={found.SnapshotId}; notes={found.NoteCount}; skipped={found.SkippedCount}; limited={found.Limited}; sources={found.Sources.Count}.");

        var temporary = Path.Combine(Path.GetTempPath(), "chronoisle-remote-eval", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new KnowledgeBaseSettingsService(temporary);
            settings.SaveRemote(true, endpoint, key);
            var provider = new ProviderSettingsService().Load();
            Assert.False(string.IsNullOrWhiteSpace(provider.ApiKey));
            var service = new KnowledgeQuestionService(settings, new ObsidianKnowledgeIndex(), new OpenAiChatService());
            var answer = await service.AskAsync(provider, question, budget.Token);
            Assert.False(answer.IsFailure, answer.Reply);
            Assert.Contains("openDialog", answer.Reply);
            Assert.Contains(endpoint + "v1/files/", answer.Reply);
            Assert.Contains("#L", answer.Reply);
            Assert.Contains("远程资料快照", answer.Reply);
            output.WriteLine("Configured model produced a response with validated quotations and authenticated remote source links.");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
}

public sealed class RemoteImageFactAttribute : FactAttribute
{
    public RemoteImageFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHRONOISLE_REMOTE_IMAGE")))
            Skip = "Explicit deployment check only: reads one selected remote attachment using installed settings.";
    }
}

public sealed class RemoteDeploymentFactAttribute : FactAttribute
{
    public RemoteDeploymentFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHRONOISLE_REMOTE_CONFIG")))
            Skip = "Explicit deployment check only: reads the selected remote project vault and calls the configured model.";
    }
}
