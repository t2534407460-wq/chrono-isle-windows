using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Sync;
using Microsoft.Data.Sqlite;

namespace ChronoIsle.Tests;

public sealed class Day21SourceClientTests
{
    [Fact] public async Task SourceCacheIsSeparateFromLocalItemsAndDistinguishesZeroFromUnrecorded()
    {
        using var f = await Fixture.Create(); await f.Client.RefreshAsync();
        Assert.Empty(f.Data.Todos()); Assert.Empty(f.Data.ManagedItems()); Assert.NotNull(f.Client.LastFetched);
        Assert.Null(Assert.Single(f.Client.Cached()).Value);
        await f.Client.ExecuteAsync(Assert.Single(f.Client.Cached()), "set_total", 0, "真实零记录");
        Assert.Equal(0, Assert.Single(f.Client.Cached()).Value); Assert.Empty(f.Data.ManagedItems());
    }
    [Fact] public async Task LostResponseSurvivesClientRecreationAndReplaysIdenticalBodyOnlyOnce()
    {
        using var f = await Fixture.Create(); await f.Client.RefreshAsync(); f.Server.LoseResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Client.ExecuteAsync(Assert.Single(f.Client.Cached()), "count"));
        Assert.Equal("pending", Assert.Single(f.Client.Requests()).Status);
        f.Client.Dispose(); f.Client = f.NewClient();
        await f.Client.RefreshAsync(true); Assert.Empty(f.Client.Requests()); Assert.Equal(1, Assert.Single(f.Client.Cached()).Value);
        Assert.Equal(1, f.Server.Applied); Assert.Equal(1, f.Server.Replays);
    }
    [Fact] public async Task ConflictRetainsRequestAndCannotAutomaticallyOverwriteOrIssueAnotherAction()
    {
        using var f = await Fixture.Create(); await f.Client.RefreshAsync(); f.Server.Conflict = true;
        Assert.Contains("未接受", await f.Client.ExecuteAsync(Assert.Single(f.Client.Cached()), "count"));
        var conflict = Assert.Single(f.Client.Requests()); Assert.Equal("conflict", conflict.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Client.ExecuteAsync(Assert.Single(f.Client.Cached()), "count"));
        await f.Client.RefreshAsync(); f.Client.DismissFailed(conflict); Assert.Empty(f.Client.Requests()); Assert.Equal(0, f.Server.Applied);
    }
    [Fact] public async Task TimerTakeoverRequiresConfirmationAndConnectivityBeforeStaging()
    {
        using var f = await Fixture.Create(); f.Server.MakeForeignTimer(); await f.Client.RefreshAsync(); var card = Assert.Single(f.Client.Cached());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Client.ExecuteAsync(card, "timer_takeover"));
        f.Server.FailGet = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Client.ExecuteAsync(card, "timer_takeover", takeoverConfirmed: true));
        Assert.Empty(f.Client.Requests()); Assert.Equal(0, f.Server.Posts);
        f.Server.FailGet = false; await f.Client.ExecuteAsync(card, "timer_takeover", takeoverConfirmed: true);
        Assert.Equal(f.Account.Account!.DeviceId.ToString(), f.Server.Data["timer"]!["device"]!.GetValue<string>());
    }
    [Fact] public async Task AppliedReceiptUpdatesCacheEvenIfTheFollowingRefreshFails()
    {
        using var f = await Fixture.Create(); await f.Client.RefreshAsync(); f.Server.FailGetAfterPost = true;
        Assert.Contains("已确认", await f.Client.ExecuteAsync(Assert.Single(f.Client.Cached()), "count"));
        Assert.Equal(1, Assert.Single(f.Client.Cached()).Value); Assert.Empty(Assert.Single(f.Client.Cached()).Actions); Assert.Empty(f.Client.Requests());
    }
    [Fact] public async Task InvalidSnapshotDoesNotPartiallyReplaceThePreviousCache()
    {
        using var f = await Fixture.Create(); await f.Client.RefreshAsync(); f.Server.InvalidSnapshot = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Client.RefreshAsync()); Assert.Single(f.Client.Cached()); Assert.Empty(f.Data.ManagedItems());
    }
    [Fact] public async Task BadRequestIsKeptForExplicitSourceReviewInsteadOfBeingAutomaticallyReissued()
    {
        using var f = await Fixture.Create(); await f.Client.RefreshAsync(); f.Server.BadRequest = true;
        await f.Client.ExecuteAsync(Assert.Single(f.Client.Cached()), "count"); var rejected = Assert.Single(f.Client.Requests()); Assert.Equal("invalid", rejected.Status);
        await f.Client.RefreshAsync(true); Assert.Equal(1, f.Server.Posts); f.Client.DismissFailed(rejected); Assert.Empty(f.Client.Requests());
    }
    [Fact] public async Task LogoutHidesCachedSourceAndRedirectStopsRequestsAtTheConfiguredOrigin()
    {
        using var f = await Fixture.Create(); await f.Client.RefreshAsync(); f.Server.Redirect = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Client.RefreshAsync()); Assert.Equal(2, f.Server.Gets);
        await f.Account.LogoutAsync(); Assert.Empty(f.Client.Cached()); Assert.Empty(f.Client.Requests());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Client.RefreshAsync());
    }
    sealed class Fixture : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), "island-day21-source-" + Guid.NewGuid().ToString("N"));
        public readonly Server Server = new(); public CloudAccountClient Account = null!; public LifeDataService Data = null!; public Day21HabitClient Client = null!;
        public static async Task<Fixture> Create()
        { var f = new Fixture(); Directory.CreateDirectory(f.path); f.Account = new(Path.Combine(f.path, "account"), f.Server); await f.Account.LoginAsync("qa@example.test", "synthetic-password"); f.Data = new(Path.Combine(f.path, "life.db")); f.Client = f.NewClient(); return f; }
        public Day21HabitClient NewClient() => new(Account, Data, Server, () => Server.Now, "Asia/Shanghai");
        public void Dispose() { Client.Dispose(); Account.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
    }
    sealed class Server : HttpMessageHandler
    {
        static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        readonly Guid owner = Guid.NewGuid(); readonly Dictionary<Guid, (string Body, object Result)> receipts = [];
        public readonly string Id = Guid.NewGuid().ToString(); public readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(8));
        public JsonObject Data;
        public bool LoseResponse, Conflict, FailGet, FailGetAfterPost, InvalidSnapshot, Redirect, BadRequest;
        public int Posts, Gets, Applied, Replays; long revision = 1;
        public Server() => Data = JsonSerializer.SerializeToNode(new { plan = new { id = Id, name = "阅读次数", start = "2026-10-05", mode = "AT_LEAST", unit = "次", input = "COUNT", archivedOn = (string?)null, smoking = false }, entries = new Dictionary<string, object>(), timer = (object?)null })!.AsObject();
        public void MakeForeignTimer() { Data["plan"]!["input"] = "TIMER"; Data["timer"] = JsonSerializer.SerializeToNode(new { at = Now.AddMinutes(-1).ToUnixTimeMilliseconds(), day = "2026-10-05", id = Guid.NewGuid(), device = Guid.NewGuid() }); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https", request.RequestUri!.Scheme); Assert.Equal("zhuisu.leadjet.com.cn", request.RequestUri.Host);
            if (request.RequestUri.AbsolutePath.Contains("/identity/")) return Ok(new CloudTokens("synthetic-access", "synthetic-refresh", 900, owner));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (request.Method == HttpMethod.Get)
            {
                Gets++; if (Redirect) return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://invalid.test/") } };
                if (FailGet) throw new HttpRequestException("Synthetic unavailable source");
                var card = new Day21HabitCard("21day", Id, revision, "2026-10-05", "阅读次数", "次", Data["plan"]!["input"]!.GetValue<string>(), true, 10,
                    Data["entries"]!["2026-10-05"] is { } entry ? JsonSerializer.SerializeToElement(entry) : null,
                    Data["timer"] is { } timer ? JsonSerializer.SerializeToElement(timer) : null, JsonSerializer.SerializeToElement(Data),
                    Data["timer"] is not null ? ["timer_takeover", "timer_finish", "timer_cancel"] : ["count", "set_total", "archive"]);
                if (InvalidSnapshot) card = card with { SourceProject = "wrong-project" };
                return Ok(new { sourceProject = "21day", cursor = revision, cards = new[] { card } });
            }
            Posts++; var body = await request.Content!.ReadAsStringAsync(ct); var command = JsonSerializer.Deserialize<Day21HabitRequest>(body, Json)!;
            if (BadRequest) return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { message = "Synthetic rule rejection" }) };
            if (receipts.TryGetValue(command.Operation.OperationId, out var prior)) { Assert.Equal(prior.Body, body); Replays++; return Ok(prior.Result); }
            if (!Conflict) { Applied++; revision++; Data = JsonNode.Parse(command.Operation.Data.GetRawText())!.AsObject(); }
            var result = new { operationId = command.Operation.OperationId, status = Conflict ? "conflict" : "applied", revision, current = Data };
            receipts[command.Operation.OperationId] = (body, result);
            if (LoseResponse) { LoseResponse = false; throw new HttpRequestException("Synthetic lost response"); }
            if (FailGetAfterPost) FailGet = true;
            return Ok(result);
        }
        static HttpResponseMessage Ok(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: Json) };
    }
}
