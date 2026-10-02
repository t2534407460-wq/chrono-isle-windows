using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Knowledge;
using ChronoIsle.App.Services.Markdown;
using Xunit.Abstractions;

namespace ChronoIsle.Tests;

public sealed class RemoteKnowledgeDeploymentTests(ITestOutputHelper output)
{
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
