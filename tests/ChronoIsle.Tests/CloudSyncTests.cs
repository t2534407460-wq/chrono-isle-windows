using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Sync;
using Microsoft.Data.Sqlite;

namespace ChronoIsle.Tests;

public sealed class CloudSyncTests
{
    [Fact]
    public async Task TwoDevicesRoundTripItemsArchivesChatsAndPortablePreferencesWithoutEchoingOrCopyingDevicePermissions()
    {
        var server = new Server(); using var a = await Device.Create(server); using var b = await Device.Create(server);
        a.Preferences.Save(LifePreferences.Default with { ThemeMode = "Light", IslandShowClock = false, WindowsNotifications = false, IslandTaskbarMonitor = "private-device-monitor" });
        var todo = a.Data.Save("跨设备阅读", "notes", DateTime.Today.AddDays(3), null);
        a.Data.SaveReminder("喝水", null, DateTime.Today.AddDays(2));
        a.Data.SaveRecurringReminder("晨间计划", null, new TimeOnly(9, 0), RecurrenceKind.Weekdays, []);
        a.Data.SaveEvent("设计评审", null, DateTime.Today.AddDays(1).AddHours(10), DateTime.Today.AddDays(1).AddHours(11), null);
        var old = a.Data.Save("归档事项", null, null, null, completed: true); a.Data.ArchiveCompletedAndOverdue();
        var session = a.Data.NewSession(); a.Data.Message(session.Id, "user", "保留完整聊天");
        Assert.True((await a.Sync.SyncAsync()).Complete);
        var restored = await b.Sync.SyncAsync(); Assert.True(restored.Complete); Assert.True(restored.Downloaded >= 8);
        Assert.Equal(todo.Title, Assert.Single(b.Data.Todos()).Title); Assert.Single(b.Data.ArchivedTodos());
        Assert.Single(b.Data.RecurringReminders()); Assert.Contains(b.Data.AgendaFor(DateTime.Today.AddDays(1)), i => i.Title == "设计评审");
        Assert.Equal("保留完整聊天", Assert.Single(b.Data.Messages(session.Id)).Content);
        Assert.Equal("Light", b.Preferences.Load().ThemeMode); Assert.False(b.Preferences.Load().IslandShowClock);
        Assert.True(b.Preferences.Load().WindowsNotifications); Assert.Null(b.Preferences.Load().IslandTaskbarMonitor);
        var count = server.Applied; var again = await b.Sync.SyncAsync(); Assert.True(again.Complete); Assert.Equal(count, server.Applied);
        a.Data.Save("更新标题", null, todo.DueAt, null, todo.Id); await a.Sync.SyncAsync();
        Assert.True((await b.Sync.SyncAsync()).Complete); Assert.Equal("更新标题", Assert.Single(b.Data.Todos()).Title);
        Assert.DoesNotContain("private-device-monitor", server.Wire);
    }
    [Fact]
    public async Task MultiplePullPagesKeepEveryEntityAndStopAtTheReturnedWatermark()
    {
        var server = new Server(); using var a = await Device.Create(server); using var b = await Device.Create(server);
        for (var i = 0; i < 35; i++) a.Data.Save("分页事项 " + i, null, null, null);
        Assert.Equal(35, (await a.Sync.SyncAsync()).Uploaded);
        var result = await b.Sync.SyncAsync(); Assert.True(result.Complete); Assert.Equal(35, result.Downloaded); Assert.Equal(35, b.Data.Todos().Count);
        Assert.True((await b.Sync.SyncAsync()).Complete); Assert.Equal(35, server.Applied);
    }
    [Fact]
    public async Task LostPushResponseReplaysTheOriginalOperationExactlyOnceAndPersistsAcrossClientRecreation()
    {
        var server = new Server { LoseResponse = true }; using var a = await Device.Create(server);
        a.Data.Save("离线重试", null, null, null);
        await Assert.ThrowsAsync<HttpRequestException>(() => a.Sync.SyncAsync()); var applied = server.Applied;
        a.Sync.Dispose(); a.Sync = new(a.Account, a.Data, a.Preferences, server);
        Assert.True((await a.Sync.SyncAsync()).Complete); Assert.Equal(applied, server.Applied); Assert.Equal(1, server.Replays);
    }
    [Fact]
    public async Task ConcurrentChangesRemainLocalUntilAnExplicitCloudOrLocalDecision()
    {
        var server = new Server(); using var a = await Device.Create(server); using var b = await Device.Create(server);
        var item = a.Data.Save("原始", null, null, null); await a.Sync.SyncAsync(); await b.Sync.SyncAsync();
        a.Data.Save("云端修改", null, null, null, item.Id); await a.Sync.SyncAsync();
        b.Data.Save("本机修改", null, null, null, item.Id);
        var outcome = await b.Sync.SyncAsync(); Assert.False(outcome.Complete); Assert.Equal(1, outcome.Conflicts);
        Assert.Equal("本机修改", Assert.Single(b.Data.Todos()).Title);
        b.Sync.Resolve(Assert.Single(b.Sync.Conflicts), false); Assert.Equal("云端修改", Assert.Single(b.Data.Todos()).Title);
        Assert.True((await b.Sync.SyncAsync()).Complete);
        a.Data.Save("云端再次修改", null, null, null, item.Id); await a.Sync.SyncAsync();
        b.Data.Save("保留本机", null, null, null, item.Id); await b.Sync.SyncAsync();
        b.Sync.Resolve(Assert.Single(b.Sync.Conflicts), true); Assert.True((await b.Sync.SyncAsync()).Complete);
        Assert.True((await a.Sync.SyncAsync()).Complete); Assert.Equal("保留本机", Assert.Single(a.Data.Todos()).Title);
    }
    [Fact]
    public async Task DeleteTombstonesDoNotResurrectAnItemOrGenerateEndlessLocalChanges()
    {
        var server = new Server(); using var a = await Device.Create(server); using var b = await Device.Create(server);
        var item = a.Data.Save("删除测试", null, null, null); await a.Sync.SyncAsync(); await b.Sync.SyncAsync();
        a.Data.Delete(item.Id); Assert.True((await a.Sync.SyncAsync()).Complete);
        Assert.True((await b.Sync.SyncAsync()).Complete); Assert.Empty(b.Data.Todos());
        var count = server.Applied; Assert.True((await b.Sync.SyncAsync()).Complete); Assert.Equal(count, server.Applied);
    }
    [Fact]
    public async Task NotificationStateStaysOnTheDeviceButAChangedReminderCanNotifyAgain()
    {
        var server = new Server(); using var a = await Device.Create(server); using var b = await Device.Create(server);
        var at = DateTime.Today.AddDays(1).AddHours(9);
        var item = a.Data.Save("通知测试", null, at, at); await a.Sync.SyncAsync(); await b.Sync.SyncAsync();
        b.MarkNotified(item.Id);
        a.Data.Save("只改标题", null, at, at, item.Id); await a.Sync.SyncAsync(); await b.Sync.SyncAsync();
        Assert.NotNull(Assert.Single(b.Data.Todos()).NotifiedAt);
        a.Data.Save("移动提醒", null, at.AddHours(1), at.AddHours(1), item.Id); await a.Sync.SyncAsync(); await b.Sync.SyncAsync();
        Assert.Null(Assert.Single(b.Data.Todos()).NotifiedAt);
    }
    [Fact]
    public async Task SimultaneousDeletesConvergeWithoutAnUnresolvableConflict()
    {
        var server = new Server(); using var a = await Device.Create(server); using var b = await Device.Create(server);
        var item = a.Data.Save("同时删除", null, null, null); await a.Sync.SyncAsync(); await b.Sync.SyncAsync();
        a.Data.Delete(item.Id); b.Data.Delete(item.Id); await a.Sync.SyncAsync();
        Assert.True((await b.Sync.SyncAsync()).Complete); Assert.Empty(b.Sync.Conflicts); Assert.Empty(b.Data.Todos());
    }
    [Fact]
    public async Task InvalidRemoteAggregateRollsBackDataAndCursor()
    {
        var server = new Server(); using var a = await Device.Create(server); using var b = await Device.Create(server);
        a.Data.Save("不能部分导入", null, null, null); await a.Sync.SyncAsync(); server.CorruptPull = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Sync.SyncAsync()); Assert.Empty(b.Data.Todos()); Assert.Null(b.Sync.LastSuccess);
        server.CorruptPull = false; Assert.True((await b.Sync.SyncAsync()).Complete); Assert.Single(b.Data.Todos());
    }
    [Fact]
    public async Task LocalEditWhileUploadingIsKeptAndReportedAsPendingInsteadOfClaimingCompletion()
    {
        var server = new Server(); using var a = await Device.Create(server);
        var item = a.Data.Save("上传前", null, null, null);
        server.BeforePushReply = () => a.Data.Save("上传途中修改", null, null, null, item.Id);
        var result = await a.Sync.SyncAsync(); Assert.False(result.Complete); Assert.Equal(1, result.Pending); Assert.Null(a.Sync.LastSuccess);
        Assert.Equal("上传途中修改", Assert.Single(a.Data.Todos()).Title);
        Assert.True((await a.Sync.SyncAsync()).Complete);
    }
    [Fact]
    public async Task UnauthenticatedAndRedirectedSyncNeverReportsSuccessOrLeaksBearerToAnotherOrigin()
    {
        var server = new Server(); using var a = await Device.Create(server);
        await a.Account.LogoutAsync(); await Assert.ThrowsAsync<InvalidOperationException>(() => a.Sync.SyncAsync()); Assert.Equal(0, server.Applied);
        await a.Account.LoginAsync("qa@example.test", "synthetic-password"); a.Data.Save("重定向检查", null, null, null); server.Redirect = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Sync.SyncAsync()); Assert.Null(a.Sync.LastSuccess); Assert.Equal(0, server.Applied);
    }
    sealed class Device : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), "island-cloud-sync-" + Guid.NewGuid().ToString("N"));
        public CloudAccountClient Account = null!;
        public LifeDataService Data = null!;
        public LifePreferencesService Preferences = null!;
        public CloudSyncService Sync = null!;
        public void MarkNotified(string id)
        {
            using var db = new SqliteConnection($"Data Source={Path.Combine(path, "life.db")}"); db.Open();
            using var c = db.CreateCommand(); c.CommandText = "UPDATE todos SET notified_at=$at WHERE id=$id";
            c.Parameters.AddWithValue("$at", DateTime.Now.ToString("O")); c.Parameters.AddWithValue("$id", id); c.ExecuteNonQuery();
        }
        public static async Task<Device> Create(Server server)
        {
            var value = new Device(); Directory.CreateDirectory(value.path);
            value.Account = new(Path.Combine(value.path, "account"), server); await value.Account.LoginAsync("qa@example.test", "synthetic-password");
            value.Data = new(Path.Combine(value.path, "life.db")); value.Preferences = new(value.path); value.Sync = new(value.Account, value.Data, value.Preferences, server); return value;
        }
        public void Dispose() { Sync.Dispose(); Account.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
    }
    sealed class Server : HttpMessageHandler
    {
        static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        readonly Guid owner = Guid.NewGuid();
        readonly Dictionary<string, JsonObject> entities = [];
        readonly Dictionary<Guid, JsonObject> receipts = [];
        readonly List<JsonObject> changes = [];
        public bool LoseResponse, CorruptPull, Redirect;
        public int Applied, Replays;
        public Action? BeforePushReply;
        public string Wire = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https", request.RequestUri!.Scheme); Assert.Equal("zhuisu.leadjet.com.cn", request.RequestUri.Host);
            if (request.RequestUri.AbsolutePath.Contains("/identity/")) return Ok(new CloudTokens("synthetic-access", "synthetic-refresh", 900, owner));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (Redirect) return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://invalid.test/") } };
            if (request.Method == HttpMethod.Post)
            {
                var raw = await request.Content!.ReadAsStringAsync(ct); Wire += raw;
                var operations = JsonNode.Parse(raw)!.AsArray(); var results = new JsonArray();
                foreach (var node in operations)
                {
                    var op = node!.AsObject(); var id = op["operationId"]!.GetValue<Guid>();
                    if (receipts.TryGetValue(id, out var receipt)) { results.Add(receipt.DeepClone()); Replays++; continue; }
                    var key = op["entityType"]!.GetValue<string>() + ":" + op["entityId"]!.GetValue<string>(); entities.TryGetValue(key, out var current);
                    var conflict = op["baseRevision"]!.GetValue<long>() != (current?["revision"]?.GetValue<long>() ?? 0);
                    var deleted = current?["deleted"]?.GetValue<bool>() == true && op["deleted"]!.GetValue<bool>() == false;
                    if (!conflict && !deleted)
                    {
                        Applied++; current = new JsonObject { ["entityType"] = op["entityType"]!.DeepClone(), ["entityId"] = op["entityId"]!.DeepClone(), ["revision"] = (long)Applied, ["schemaVersion"] = 1,
                            ["data"] = op["data"]?.DeepClone(), ["deleted"] = op["deleted"]!.DeepClone() }; entities[key] = current;
                        changes.Add(new JsonObject { ["sequence"] = (long)Applied, ["entity"] = current.DeepClone() });
                    }
                    receipt = new JsonObject { ["operationId"] = id, ["status"] = deleted ? "deleted" : conflict ? "conflict" : "applied", ["revision"] = current!["revision"]!.DeepClone(), ["current"] = current["data"]?.DeepClone() };
                    receipts[id] = receipt; results.Add(receipt.DeepClone());
                }
                var callback = BeforePushReply; BeforePushReply = null; callback?.Invoke();
                if (LoseResponse) { LoseResponse = false; throw new HttpRequestException("Synthetic lost response"); }
                return Ok(results);
            }
            var after = long.Parse(request.RequestUri.Query.Split('&')[0].Split('=')[1]);
            var maximum = int.Parse(request.RequestUri.Query.Split('&')[1].Split('=')[1]);
            var rows = changes.Where(c => c["sequence"]!.GetValue<long>() > after).Take(maximum).Select(c => c.DeepClone()).ToArray();
            if (CorruptPull && rows.Length > 0)
            {
                var entity = rows[0]!["entity"]!;
                entity["data"]!["tables"]!["todos"]![0]!["id"] = "wrong-item-owner";
            }
            var cursor = rows.Length > 0 ? rows[^1]!["sequence"]!.GetValue<long>() : Applied;
            return Ok(new { cursor, highWatermark = Applied, changes = rows });
        }
        static HttpResponseMessage Ok(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: Json) };
    }
}
