using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using ChronoIsle.App.Services.Knowledge;

namespace ChronoIsle.Tests;

public sealed class RemoteKnowledgeTests
{
    const string Key = "project-key-0000000000000000000000000000";
    static readonly RemoteKnowledgeSettings Settings = new("https://kb.example.test/knowledge/", Key);

    [Fact]
    public void Credentials_are_encrypted_and_remote_settings_do_not_change_the_local_vault()
    {
        var root = Path.Combine(Path.GetTempPath(), "chronoisle-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new KnowledgeBaseSettingsService(root); settings.SavePath(root);
            settings.SaveRemote(true, Settings.BaseUrl, Key);
            Assert.DoesNotContain(Key, File.ReadAllText(Path.Combine(root, "ChronoIsle", "knowledge-remote.json")));
            Assert.Equal(Settings, settings.LoadRemote()); Assert.Equal(root, settings.LoadPath());
            settings.SaveRemote(false, "", ""); Assert.Null(settings.LoadRemote());
            File.WriteAllText(Path.Combine(root, "ChronoIsle", "knowledge-remote.json"), "{}");
            Assert.Throws<KnowledgeBaseException>(settings.LoadRemote);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("http://server/")]
    [InlineData("https://user:password@server/")]
    [InlineData("https://server/?token=private")]
    [InlineData("https://server/#private")]
    public void Unsafe_service_urls_are_rejected(string address) =>
        Assert.Throws<KnowledgeBaseException>(() => KnowledgeBaseSettingsService.ValidateRemote(address, Key));

    [Fact]
    public async Task Remote_search_uses_only_configured_origin_and_rejects_server_injected_file_paths()
    {
        var source = new KnowledgeSource("S1", "sMES/备份.md", "备份", 1, 2, "恢复数据库前停止应用。", "", DateTime.UtcNow, new string('A', 64));
        var result = new KnowledgeSearchResult("/private/server/path", [source], 1, 0, false, new string('a', 32), "https://evil.test/");
        var handler = new Handler(request =>
        {
            Assert.Equal("https://kb.example.test/knowledge/v1/search", request.RequestUri!.AbsoluteUri);
            Assert.Equal(Key, request.Headers.Authorization!.Parameter);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        });
        var remote = new RemoteKnowledgeClient(Settings, new HttpClient(handler));
        var found = await remote.SearchAsync("数据库恢复", default);
        Assert.StartsWith(Settings.BaseUrl + "v1/files/", found.DocumentBaseUri);
        Assert.DoesNotContain("/private", found.VaultPath);
        result = result with { Sources = [source with { RelativePath = "../secrets.md" }] };
        await Assert.ThrowsAsync<KnowledgeBaseException>(() => remote.SearchAsync("数据库恢复", default));
        result = result with { Sources = [null!] };
        await Assert.ThrowsAsync<KnowledgeBaseException>(() => remote.SearchAsync("数据库恢复", default));
    }

    [Fact]
    public async Task Document_keys_are_never_sent_to_external_hosts_or_unrelated_paths_and_redirects_fail()
    {
        var calls = 0;
        var remote = new RemoteKnowledgeClient(Settings, new HttpClient(new Handler(request =>
        {
            calls++; Assert.Equal(Key, request.Headers.Authorization!.Parameter);
            return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://elsewhere.test/private.md") } };
        })));
        Assert.Null(await remote.ReadDocumentAsync(new Uri("https://elsewhere.test/private.md")));
        Assert.Null(await remote.ReadDocumentAsync(new Uri("https://kb.example.test/other.md")));
        Assert.Null(await remote.ReadDocumentAsync(new Uri("https://kb.example.test/knowledge/v1/files-other/a.md")));
        Assert.Equal(0, calls);
        await Assert.ThrowsAsync<IOException>(() => remote.ReadDocumentAsync(new Uri(Settings.BaseUrl + "v1/files/a/b.md")));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Remote_failures_are_explicit(int status)
    {
        var remote = new RemoteKnowledgeClient(Settings, new HttpClient(new Handler(_ => new((HttpStatusCode)status))));
        var error = await Assert.ThrowsAsync<KnowledgeBaseException>(() => remote.SearchAsync("数据库恢复", default));
        Assert.DoesNotContain(Key, error.Message);
    }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
    }
}
