using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using ChronoIsle.App.Services.Knowledge;
using ChronoIsle.Knowledge.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

if (args is ["--healthcheck"])
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync("http://127.0.0.1:8080/health");
        Environment.ExitCode = response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { Environment.ExitCode = 1; }
    return;
}

if (args is ["--import", var archive, "--data", var dataRoot])
{
    await using var input = File.OpenRead(archive);
    var published = await new SnapshotStore(dataRoot).PublishAsync(input, CancellationToken.None);
    Console.WriteLine($"Published {published.Files.Count} files; snapshot {published.SnapshotId}.");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = SnapshotStore.MaxArchiveBytes);
builder.Services.AddSingleton(services => new SnapshotStore(services.GetRequiredService<IConfiguration>()["Knowledge:DataRoot"] ?? "/data"));
builder.Services.AddSingleton<ObsidianKnowledgeIndex>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
        (string?)context.Items["client"] ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
var store = app.Services.GetRequiredService<SnapshotStore>();
var clients = app.Configuration.GetSection("Knowledge:Clients").GetChildren().Select(section => new ApiClient(
    section.Key, section["KeyHash"] ?? "", section.GetValue<bool>("CanSync"))).ToArray();
if (clients.Length == 0 || clients.Any(c => !SnapshotStore.IsHash(c.KeyHash)))
    throw new InvalidOperationException("Configure a SHA-256 key hash for every Knowledge:Clients entry before starting.");
var readers = new SemaphoreSlim(4, 4);
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
    try
    {
        if (context.Request.Path != "/health")
        {
            var auth = context.Request.Headers.Authorization.ToString();
            ApiClient? client = null;
            if (auth.StartsWith("Bearer ", StringComparison.Ordinal) && auth.Length is >= 39 and <= 263)
            {
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(auth[7..]));
                client = clients.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(c.KeyHash)));
            }
            if (client is null) { context.Response.StatusCode = 401; return; }
            context.Items["client"] = client.Name;
            var syncing = context.Request.Path.StartsWithSegments("/v1/sync");
            if (syncing && !client.CanSync) { context.Response.StatusCode = 403; return; }
            var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = syncing ? SnapshotStore.MaxArchiveBytes : 32768;
        }
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception error) when (error is KnowledgeBaseException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or SyncConflictException)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = error is SyncConflictException ? 409 : error is InvalidDataException or JsonException ? 400 : 503;
        await context.Response.WriteAsJsonAsync(new { error = error is SyncConflictException ? "资料版本冲突或正在同步，请稍后重试。" :
            error is InvalidDataException or JsonException ? "请求或同步包校验失败。" : "知识库暂时无法读取。" });
    }
});
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "project-knowledge", apiVersion = 1 }));
var api = app.MapGroup("/v1").RequireRateLimiting("api");
api.MapGet("/status", () => Results.Ok(new { ready = store.CurrentId() is not null, apiVersion = 1 }));
api.MapPost("/search", async (KnowledgeQuery query, ObsidianKnowledgeIndex index, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(query.Question) || query.Question.Length > 2000) return Results.BadRequest(new { error = "问题长度必须为 1–2000 字符。" });
    if (!await readers.WaitAsync(0, token)) return Results.StatusCode(429);
    try
    {
        var id = store.CurrentId() ?? throw new KnowledgeBaseException("尚未同步知识库。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await index.SearchAsync(store.VaultRoot(id), query.Question, deadline.Token);
        return Results.Ok(result with { VaultPath = "项目知识库", SnapshotId = id });
    }
    catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Results.StatusCode(504); }
    finally { readers.Release(); }
});
api.MapPost("/verify", async (KnowledgeVerification request, ObsidianKnowledgeIndex index, CancellationToken token) =>
{
    if (!SnapshotStore.IsId(request.SnapshotId) || request.Sources is null || request.Sources.Count is < 1 or > 6 ||
        request.Sources.Any(s => s is null || !VaultPaths.IsRelativeDocumentPath(s.RelativePath) || !SnapshotStore.IsHash(s.ContentHash)))
        return Results.BadRequest();
    if (store.CurrentId() != request.SnapshotId) return Results.Ok(new VerificationResult(false));
    if (!await readers.WaitAsync(0, token)) return Results.StatusCode(429);
    try
    {
        var sources = request.Sources.Select((s, i) => new KnowledgeSource($"S{i + 1}", s.RelativePath, "", 0, 0, "", "", default, s.ContentHash)).ToArray();
        var unchanged = await index.SourcesUnchangedAsync(new(store.VaultRoot(request.SnapshotId), sources, 0, 0, false), token);
        return Results.Ok(new VerificationResult(unchanged && store.CurrentId() == request.SnapshotId));
    }
    finally { readers.Release(); }
});
api.MapGet("/files/{snapshot}/{**path}", (string snapshot, string path) =>
{
    if (!SnapshotStore.IsId(snapshot) || !VaultPaths.IsRelativeDocumentPath(path)) return Results.BadRequest();
    var mime = Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" or ".markdown" => "text/plain; charset=utf-8", ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
        ".bmp" => "image/bmp", ".tif" or ".tiff" => "image/tiff", ".ico" => "image/x-icon", ".pdf" => "application/pdf", _ => null
    };
    if (mime is null) return Results.NotFound();
    string file;
    try { file = VaultPaths.ResolveFile(store.VaultRoot(snapshot), path); }
    catch (Exception e) when (e is KnowledgeBaseException or IOException) { return Results.NotFound(); }
    if (new FileInfo(file).Length > 16 * 1024 * 1024) return Results.StatusCode(413);
    return Results.File(file, mime, enableRangeProcessing: false);
});
api.MapGet("/sync/manifest", () => store.CurrentId() is { } id ? Results.Ok(store.ReadManifest(id)) : Results.Ok(new PublishedVault("", [])));
api.MapPost("/sync", async (HttpContext context) =>
{
    if (context.Request.ContentType != "application/zip") return Results.StatusCode(415);
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    deadline.CancelAfter(TimeSpan.FromMinutes(30));
    try { return Results.Ok(await store.PublishAsync(context.Request.Body, deadline.Token)); }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { return Results.StatusCode(504); }
});
app.Run();

public sealed record ApiClient(string Name, string KeyHash, bool CanSync);
public partial class Program;
