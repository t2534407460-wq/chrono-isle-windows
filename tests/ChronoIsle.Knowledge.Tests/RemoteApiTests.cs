using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChronoIsle.App.Services.Knowledge;
using ChronoIsle.Knowledge.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

public sealed class RemoteApiTests : IDisposable
{
    const string ReadKey = "read-only-test-key-000000000000000000000000";
    const string SyncKey = "sync-only-test-key-000000000000000000000000";
    readonly string root = Path.Combine(Path.GetTempPath(), "chronoisle-api-tests", Guid.NewGuid().ToString("N"));
    readonly WebApplicationFactory<Program> factory;
    readonly DateTime modified = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

    public RemoteApiTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:DataRoot"] = root,
                ["Knowledge:Clients:read:KeyHash"] = Hash(Encoding.UTF8.GetBytes(ReadKey)),
                ["Knowledge:Clients:sync:KeyHash"] = Hash(Encoding.UTF8.GetBytes(SyncKey)),
                ["Knowledge:Clients:sync:CanSync"] = "true"
            })));
    }

    [Fact]
    public async Task Authentication_and_read_write_permissions_are_enforced()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/status")).StatusCode);
        using var reader = Client(ReadKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/v1/sync/manifest")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/v1/sync", new StringContent("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.PostAsJsonAsync("/v1/search", new KnowledgeQuery(""))).StatusCode);
    }

    [Fact]
    public async Task Publish_search_citations_assets_refresh_and_conflict_work_end_to_end()
    {
        var original = new Dictionary<string, byte[]> { ["sMES/备份.md"] = Encoding.UTF8.GetBytes("# 恢复数据库\n恢复数据库前必须停止应用。"), ["sMES/附件/a.png"] = [1, 2, 3] };
        using var sync = Client(SyncKey);
        var first = await Publish(sync, Bundle(original));
        using var reader = Client(ReadKey);
        var result = await reader.PostAsJsonAsync("/v1/search", new KnowledgeQuery("数据库恢复"));
        result.EnsureSuccessStatusCode();
        var found = (await result.Content.ReadFromJsonAsync<KnowledgeSearchResult>())!;
        Assert.Equal(first.SnapshotId, found.SnapshotId);
        Assert.Equal("项目知识库", found.VaultPath);
        Assert.DoesNotContain(root, await result.Content.ReadAsStringAsync());
        var source = Assert.Single(found.Sources);
        Assert.Contains("必须停止应用", source.Text);
        var verify = new KnowledgeVerification(found.SnapshotId!, [new(source.RelativePath, source.ContentHash)]);
        Assert.True((await (await reader.PostAsJsonAsync("/v1/verify", verify)).Content.ReadFromJsonAsync<VerificationResult>())!.Unchanged);
        Assert.Equal(original["sMES/附件/a.png"], await reader.GetByteArrayAsync($"/v1/files/{first.SnapshotId}/sMES/附件/a.png"));
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/v1/files/{first.SnapshotId}/secrets.json")).StatusCode);
        var updated = new Dictionary<string, byte[]>(original) { ["sMES/备份.md"] = Encoding.UTF8.GetBytes("# 恢复数据库\n恢复数据库前先导出备份。") };
        var second = await Publish(sync, Bundle(updated, first, omitUnchanged: true));
        Assert.NotEqual(first.SnapshotId, second.SnapshotId);
        Assert.False((await (await reader.PostAsJsonAsync("/v1/verify", verify)).Content.ReadFromJsonAsync<VerificationResult>())!.Unchanged);
        Assert.Contains("必须停止应用", await reader.GetStringAsync($"/v1/files/{first.SnapshotId}/sMES/备份.md"));
        Assert.Contains("先导出备份", await reader.GetStringAsync($"/v1/files/{second.SnapshotId}/sMES/备份.md"));
        Assert.Equal(HttpStatusCode.Conflict, (await sync.PostAsync("/v1/sync", Bundle(updated, first))).StatusCode);
        Assert.Equal(second.SnapshotId, (await sync.GetFromJsonAsync<PublishedVault>("/v1/sync/manifest"))!.SnapshotId);
    }

    [Theory]
    [InlineData("../private.md")]
    [InlineData(".obsidian/secrets.md")]
    [InlineData("/absolute.md")]
    [InlineData("project\\escape.md")]
    [InlineData("C:/private.md")]
    public async Task Invalid_archive_paths_never_change_the_published_version(string path)
    {
        using var sync = Client(SyncKey);
        var first = await Publish(sync, Bundle(new() { ["safe.md"] = Encoding.UTF8.GetBytes("原始安全资料") }));
        Assert.Equal(HttpStatusCode.BadRequest, (await sync.PostAsync("/v1/sync", Bundle(new() { [path] = [1] }, first))).StatusCode);
        Assert.Equal(first.SnapshotId, (await sync.GetFromJsonAsync<PublishedVault>("/v1/sync/manifest"))!.SnapshotId);
    }

    [Fact]
    public async Task Corrupt_content_is_rejected_and_files_omitted_from_new_manifest_disappear_from_search()
    {
        using var sync = Client(SyncKey);
        var first = await Publish(sync, Bundle(new() { ["old.md"] = Encoding.UTF8.GetBytes("数据库恢复停止应用"), ["keep.md"] = Encoding.UTF8.GetBytes("天气晴朗") }));
        Assert.Equal(HttpStatusCode.BadRequest, (await sync.PostAsync("/v1/sync", Bundle(new() { ["bad.md"] = [1, 2, 3] }, first, corrupt: true))).StatusCode);
        await Publish(sync, Bundle(new() { ["keep.md"] = Encoding.UTF8.GetBytes("天气晴朗") }, first, omitUnchanged: true));
        using var reader = Client(ReadKey);
        var result = await (await reader.PostAsJsonAsync("/v1/search", new KnowledgeQuery("数据库恢复"))).Content.ReadFromJsonAsync<KnowledgeSearchResult>();
        Assert.Empty(result!.Sources);
        Assert.Equal(1, result.NoteCount);
    }

    HttpClient Client(string key)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    async Task<PublishedVault> Publish(HttpClient client, HttpContent content)
    {
        using var result = await client.PostAsync("/v1/sync", content);
        result.EnsureSuccessStatusCode();
        return (await result.Content.ReadFromJsonAsync<PublishedVault>())!;
    }

    HttpContent Bundle(Dictionary<string, byte[]> files, PublishedVault? previous = null, bool omitUnchanged = false, bool corrupt = false)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var manifest = new VaultManifest(previous?.SnapshotId, files.Select(f => new VaultFile(f.Key, f.Value.Length, Hash(f.Value), modified)).ToArray());
            using (var output = archive.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(output, manifest, SnapshotStore.Json);
            var hashes = new HashSet<string>();
            foreach (var file in manifest.Files)
            {
                if (omitUnchanged && previous!.Files.Contains(file)) continue;
                if (!hashes.Add(file.Hash)) continue;
                using var output = archive.CreateEntry("files/" + file.Hash).Open();
                var bytes = files[file.Path].ToArray();
                if (corrupt) bytes[0] ^= 0xff;
                output.Write(bytes);
            }
        }
        var content = new ByteArrayContent(stream.ToArray()); content.Headers.ContentType = new MediaTypeHeaderValue("application/zip"); return content;
    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public void Dispose() { factory.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
