using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChronoIsle.App.Services.Knowledge;

public sealed class RemoteKnowledgeClient
{
    static readonly HttpClient Shared = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly RemoteKnowledgeSettings settings;
    readonly HttpClient client;

    public RemoteKnowledgeClient(RemoteKnowledgeSettings settings, HttpClient? client = null)
    {
        this.settings = KnowledgeBaseSettingsService.ValidateRemote(settings.BaseUrl, settings.ApiKey);
        this.client = client ?? Shared;
    }

    public async Task<KnowledgeSearchResult> SearchAsync(string question, CancellationToken token)
    {
        var result = await PostAsync<KnowledgeSearchResult>("v1/search", new KnowledgeQuery(question), token);
        if (result.SnapshotId is not { Length: 32 } || !result.SnapshotId.All(char.IsAsciiHexDigit) || result.Sources is null ||
            result.Sources.Count > 6 || result.NoteCount < 0 || result.SkippedCount < 0 ||
            result.Sources.Any(s => s is null || !VaultPaths.IsRelativeDocumentPath(s.RelativePath) ||
                s.Id is not { Length: 2 } || s.Id[0] != 'S' || s.Id[1] is < '1' or > '6' ||
                s.Text is null || s.Text.Length > 4000 || s.Metadata is null || s.Metadata.Length > 1000 ||
                s.Heading is null || s.Heading.Length > 1000 || s.StartLine < 1 || s.EndLine < s.StartLine ||
                s.ContentHash is not { Length: 64 } || !s.ContentHash.All(char.IsAsciiHexDigit)) ||
            result.Sources.Select(s => s.Id).Distinct().Count() != result.Sources.Count)
            throw new KnowledgeBaseException("远程知识库返回的资料格式无效，未进行 AI 回答。");
        return result with { VaultPath = "远程项目知识库（" + new Uri(settings.BaseUrl).Authority + "）",
            DocumentBaseUri = new Uri(new Uri(settings.BaseUrl), "v1/files/" + result.SnapshotId + "/").AbsoluteUri };
    }

    public Task<VerificationResult> VerifyAsync(KnowledgeSearchResult result, CancellationToken token) =>
        PostAsync<VerificationResult>("v1/verify", new KnowledgeVerification(result.SnapshotId!,
            result.Sources.Select(s => new SourceVersion(s.RelativePath, s.ContentHash)).ToArray()), token);

    public async Task<bool> TestAsync(CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        using var request = Request(HttpMethod.Get, new Uri(new Uri(settings.BaseUrl), "v1/status"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        EnsureSuccess(response);
        var status = await ReadJson<RemoteStatus>(response, token);
        if (status.ApiVersion != 1) throw new KnowledgeBaseException("远程知识库接口版本不兼容。");
        return status.Ready;
    }

    async Task<T> PostAsync<T>(string path, object body, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        using var request = Request(HttpMethod.Post, new Uri(new Uri(settings.BaseUrl), path));
        request.Content = JsonContent.Create(body, options: Json);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        EnsureSuccess(response);
        return await ReadJson<T>(response, token);
    }

    HttpRequestMessage Request(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        return request;
    }

    static async Task<T> ReadJson<T>(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (data.Length + count > 256 * 1024) throw new KnowledgeBaseException("远程知识库响应过大。");
            data.Write(buffer, 0, count);
        }
        try { return JsonSerializer.Deserialize<T>(data.ToArray(), Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new KnowledgeBaseException("远程知识库响应格式无效。"); }
    }

    static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new KnowledgeBaseException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "远程知识库访问密钥无效或无权访问，请检查设置。",
            HttpStatusCode.TooManyRequests => "远程知识库当前繁忙，请稍后重试。",
            HttpStatusCode.NotFound => "远程原文或接口不存在，请检查地址及资料版本。",
            _ => "远程知识库暂时不可用，未改用本地或旧缓存作答。"
        });
    }

    // Only this service's document route receives its key. Never follow redirects with credentials.
    public static async Task<HttpResponseMessage?> TryReadDocumentAsync(Uri uri, CancellationToken token)
    {
        try
        {
            var saved = new KnowledgeBaseSettingsService().LoadRemote();
            return saved is null ? null : await new RemoteKnowledgeClient(saved).ReadDocumentAsync(uri, token);
        }
        catch (KnowledgeBaseException error) { throw new IOException(error.Message); }
    }

    public async Task<HttpResponseMessage?> ReadDocumentAsync(Uri uri, CancellationToken token = default)
    {
        if (!new Uri(new Uri(settings.BaseUrl), "v1/files/").IsBaseOf(uri)) return null;
        using var request = Request(HttpMethod.Get, uri);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        try { EnsureSuccess(response); return response; }
        catch (KnowledgeBaseException error) { response.Dispose(); throw new IOException(error.Message); }
    }

    sealed record RemoteStatus(bool Ready, int ApiVersion);
}
