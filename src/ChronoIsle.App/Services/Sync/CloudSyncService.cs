using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.App.Services.Sync;

public sealed record CloudSyncOutcome(int Uploaded, int Downloaded, int Conflicts, int Pending)
{
    public bool Complete => Conflicts == 0 && Pending == 0;
    public string Message => Complete ? $"同步完成 · 上传 {Uploaded} 条，下载 {Downloaded} 条" :
        $"已上传 {Uploaded} 条、下载 {Downloaded} 条；" + (Conflicts > 0 ? $"{Conflicts} 项冲突需要处理。" : "还有新的本机变更，请再次同步。");
}

public sealed class CloudSyncService : IDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly CloudAccountClient account;
    readonly CloudSyncStore store;
    readonly LifeDataService data;
    readonly HttpClient http;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly SqliteOnlineBackupService backups;
    readonly string backupDirectory;
    public DateTimeOffset? LastSuccess => store.LastSuccess;
    public IReadOnlyList<CloudSyncConflict> Conflicts => store.Conflicts;
    public CloudSyncService(CloudAccountClient account, LifeDataService data, LifePreferencesService preferences, HttpMessageHandler? handler = null)
    {
        this.account = account; this.data = data; store = new(data, preferences);
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri("https://zhuisu.leadjet.com.cn/island-api/v1/sync/"), Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(data.DatabasePath);
        backups = new(runtime.ConnectionFactory, runtime.WriteQueue);
        backupDirectory = Path.Combine(Path.GetDirectoryName(data.DatabasePath)!, "CloudSyncBackups");
    }
    public async Task<CloudSyncOutcome> SyncAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("正在同步，请等待本次同步完成。");
        var uploaded = 0; var downloaded = 0;
        try
        {
            var owner = RequireAccount(); store.Bind(owner);
            progress?.Report("正在准备本机数据…");
            var pending = store.Stage();
            foreach (var batch in pending.Chunk(12))
            {
                progress?.Report($"正在上传 · {uploaded} / {pending.Count}…");
                using var response = await Send(owner, HttpMethod.Post, "push", batch, ct);
                var results = await response.Content.ReadFromJsonAsync<PushResult[]>(Json, ct) ?? throw new InvalidDataException("同步回执为空。");
                if (results.Length != batch.Length || results.Select(r => r.OperationId).Distinct().Count() != batch.Length || results.Any(r => !batch.Any(o => o.OperationId == r.OperationId) || r.Revision < 1 || r.Status is not ("applied" or "conflict" or "deleted")))
                    throw new InvalidDataException("同步回执无效，本机队列已保留。");
                foreach (var result in results)
                {
                    var op = batch.Single(o => o.OperationId == result.OperationId);
                    if (result.Status == "applied") { store.Acknowledge(op, result.Revision); uploaded++; }
                    else store.Reject(op, new(op.EntityType, op.EntityId, result.Revision, 1, result.Current, result.Status == "deleted" || result.Current is null));
                }
            }
            var backedUp = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested(); progress?.Report("正在获取云端变更…");
                var cursor = store.Cursor;
                using var response = await Send(owner, HttpMethod.Get, $"pull?after={cursor}&maximum=12", null, ct);
                var page = await response.Content.ReadFromJsonAsync<PullPage>(Json, ct) ?? throw new InvalidDataException("同步分页为空。");
                if (page.Cursor < cursor || page.HighWatermark < page.Cursor || page.Changes.Count > 12 ||
                    page.Changes.Select(c => c.Sequence).Distinct().Count() != page.Changes.Count ||
                    page.Changes.Where((c, i) => c.Sequence <= cursor || c.Sequence > page.Cursor || c.Entity.Revision < 1 ||
                        i > 0 && c.Sequence <= page.Changes[i - 1].Sequence).Any() || page.Cursor == cursor && page.HighWatermark > cursor)
                    throw new InvalidDataException("云端同步水位无效，本机数据已保留。");
                if (page.Changes.Count > 0 && !backedUp)
                {
                    Directory.CreateDirectory(backupDirectory);
                    backups.Create(Path.Combine(backupDirectory, $"before-sync-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db"), LocalBackupKind.UserPortable);
                    backedUp = true;
                }
                progress?.Report("正在合并云端数据…");
                downloaded += store.ApplyPage(page.Changes.Select(c => c.Entity).ToArray(), page.Cursor);
                if (page.Cursor >= page.HighWatermark) break;
            }
            var remaining = store.Stage().Count;
            var outcome = new CloudSyncOutcome(uploaded, downloaded, store.Conflicts.Count, remaining);
            if (outcome.Complete) store.Finish();
            return outcome;
        }
        finally { gate.Release(); data.NotifyCloudDataChanged(); }
    }
    public void Resolve(CloudSyncConflict conflict, bool useLocal)
    {
        if (!gate.Wait(0)) throw new InvalidOperationException("请等待同步完成后处理冲突。");
        try
        {
            store.Bind(RequireAccount());
            Directory.CreateDirectory(backupDirectory);
            backups.Create(Path.Combine(backupDirectory, $"before-conflict-{Guid.NewGuid():N}.db"), LocalBackupKind.UserPortable);
            store.Resolve(conflict, useLocal);
        }
        finally { gate.Release(); data.NotifyCloudDataChanged(); }
    }
    Guid RequireAccount() => account.Account?.UserId ?? throw new InvalidOperationException("请先登录账号。");
    async Task<HttpResponseMessage> Send(Guid owner, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (RequireAccount() != owner) throw new InvalidOperationException("账号已发生变化，已停止同步。");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await account.AccessTokenAsync(ct));
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        var response = await http.SendAsync(request, ct);
        if (RequireAccount() != owner) { response.Dispose(); throw new InvalidOperationException("账号已发生变化，已停止同步。"); }
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode; response.Dispose();
        throw new InvalidOperationException(status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "登录已失效或没有同步权限，请退出后重新登录。",
            HttpStatusCode.TooManyRequests => "同步过于频繁，请稍后重试。",
            _ when (int)status is >= 300 and < 400 => "同步服务返回了重定向，已停止发送数据。",
            _ => "云端同步暂时不可用，本机数据和待同步队列已保留，请稍后重试。"
        });
    }
    public void Dispose() => http.Dispose();
    sealed record PushResult(Guid OperationId, string Status, long Revision, JsonElement? Current);
    sealed record PullChange(long Sequence, CloudSyncEntity Entity);
    sealed record PullPage(long Cursor, long HighWatermark, IReadOnlyList<PullChange> Changes);
}
