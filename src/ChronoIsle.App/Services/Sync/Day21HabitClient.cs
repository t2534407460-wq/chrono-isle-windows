using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChronoIsle.App.Services.Persistence;
using Microsoft.Data.Sqlite;

namespace ChronoIsle.App.Services.Sync;

public sealed class Day21HabitClient : IDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly CloudAccountClient account;
    readonly IDbWriteQueue queue;
    readonly HttpClient http;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Func<DateTimeOffset> utcNow;
    public string TimeZoneId { get; }
    public bool LoggedIn => account.Account is not null;
    public Guid DeviceId => account.Account?.DeviceId ?? Guid.Empty;
    public Day21HabitClient(CloudAccountClient account, LifeDataService data, HttpMessageHandler? handler = null,
        Func<DateTimeOffset>? utcNow = null, string? timeZoneId = null)
    {
        this.account = account; this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        TimeZoneId = timeZoneId ?? (TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : TimeZoneInfo.Local.Id);
        queue = LifeDataStoreRuntimeRegistry.GetOrCreate(data.DatabasePath).WriteQueue;
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new("https://zhuisu.leadjet.com.cn/21day-api/v1/habits/"), Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        queue.Execute(u => Execute(u, """
            CREATE TABLE IF NOT EXISTS day21_source_cards(owner TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,fetched_at TEXT NOT NULL,PRIMARY KEY(owner,id));
            CREATE TABLE IF NOT EXISTS day21_source_meta(owner TEXT PRIMARY KEY,cursor INTEGER NOT NULL,fetched_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS day21_source_requests(operation_id TEXT PRIMARY KEY,owner TEXT NOT NULL,source_id TEXT NOT NULL,body TEXT NOT NULL,status TEXT NOT NULL,message TEXT NOT NULL DEFAULT '',response TEXT,created_at TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_day21_source_open_request ON day21_source_requests(owner,source_id) WHERE status IN ('pending','conflict','invalid','deleted','unsupported');
            """));
    }
    public IReadOnlyList<Day21HabitCard> Cached() => !LoggedIn ? [] : queue.Execute(u =>
    {
        using var c = Command(u, "SELECT payload FROM day21_source_cards WHERE owner=$owner ORDER BY id", ("$owner", Owner().ToString()));
        using var r = c.ExecuteReader(); var cards = new List<Day21HabitCard>();
        while (r.Read()) cards.Add(JsonSerializer.Deserialize<Day21HabitCard>(r.GetString(0), Json)!);
        return (IReadOnlyList<Day21HabitCard>)cards;
    });
    public DateTimeOffset? LastFetched => !LoggedIn ? null : queue.Execute(u =>
    {
        using var c = Command(u, "SELECT fetched_at FROM day21_source_meta WHERE owner=$owner", ("$owner", Owner().ToString()));
        return DateTimeOffset.TryParse(c.ExecuteScalar() as string, out var at) ? (DateTimeOffset?)at : null;
    });
    public IReadOnlyList<Day21PendingRequest> Requests() => !LoggedIn ? [] : queue.Execute(u =>
    {
        using var c = Command(u, "SELECT operation_id,source_id,body,status,message FROM day21_source_requests WHERE owner=$owner AND status IN ('pending','conflict','invalid','deleted','unsupported') ORDER BY created_at", ("$owner", Owner().ToString()));
        using var r = c.ExecuteReader(); var rows = new List<Day21PendingRequest>();
        while (r.Read()) rows.Add(new(Guid.Parse(r.GetString(0)), r.GetString(1), JsonSerializer.Deserialize<Day21HabitRequest>(r.GetString(2), Json)!.Action, r.GetString(3), r.GetString(4)));
        return (IReadOnlyList<Day21PendingRequest>)rows;
    });
    public async Task RefreshAsync(bool retryPending = false, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var owner = Owner();
            if (retryPending)
                foreach (var request in Requests().Where(r => r.Status == "pending")) await SendRequest(owner, request.OperationId, ct);
            await Fetch(owner, ct);
        }
        finally { gate.Release(); }
    }
    public async Task<string> ExecuteAsync(Day21HabitCard card, string action, int? total = null, string? note = null, bool takeoverConfirmed = false, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var owner = Owner();
            if (Requests().Any(r => r.SourceId == card.SourceId)) throw new InvalidOperationException("该习惯还有未确认或冲突的操作，请先重试或处理原请求。");
            if (action == "timer_takeover")
            {
                if (!takeoverConfirmed) throw new InvalidOperationException("接管需要在线确认。");
                await Fetch(owner, ct);
                var fresh = Cached().SingleOrDefault(c => c.SourceId == card.SourceId);
                if (fresh is null || fresh.SourceRevision != card.SourceRevision) throw new InvalidOperationException("来源计时已变化，请刷新并重新确认接管。");
                card = fresh;
            }
            var request = Day21HabitCommandBuilder.Build(card, action, DeviceId, TimeZoneId, utcNow(), total, note, takeoverConfirmed);
            var body = JsonSerializer.Serialize(request, Json);
            queue.Execute(u => Execute(u, "INSERT INTO day21_source_requests(operation_id,owner,source_id,body,status,created_at) VALUES($id,$owner,$source,$body,'pending',$at)",
                ("$id", request.Operation.OperationId.ToString()), ("$owner", owner.ToString()), ("$source", card.SourceId), ("$body", body), ("$at", utcNow().ToString("O"))));
            var status = await SendRequest(owner, request.Operation.OperationId, ct);
            if (status == "applied")
            {
                try { await Fetch(owner, ct); }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidOperationException or IOException or JsonException)
                { return "21day已确认此操作，刷新来源暂时失败；可稍后刷新，原请求不会重复执行。"; }
                return "21day已确认并更新来源记录。";
            }
            return "来源未接受此操作；已保留请求，请查看冲突并刷新来源。";
        }
        finally { gate.Release(); }
    }
    public void DismissFailed(Day21PendingRequest request)
    {
        if (request.Status == "pending") throw new InvalidOperationException("该请求结果尚未确认，只能重试原请求。");
        var owner = Owner();
        queue.Execute(u => Execute(u, "UPDATE day21_source_requests SET status='resolved' WHERE operation_id=$id AND owner=$owner AND status IN ('conflict','invalid','deleted','unsupported')", ("$id", request.OperationId.ToString()), ("$owner", owner.ToString())));
    }
    async Task Fetch(Guid owner, CancellationToken ct)
    {
        using var response = await Send(owner, HttpMethod.Get, "cards?timeZoneId=" + Uri.EscapeDataString(TimeZoneId), null, ct);
        var result = await response.Content.ReadFromJsonAsync<CardsResponse>(Json, ct) ?? throw new InvalidDataException("来源响应为空。");
        if (result.SourceProject != "21day" || result.Cursor < 0 || result.Cards is null || result.Cards.Select(c => c.SourceId).Distinct().Count() != result.Cards.Count)
            throw new InvalidDataException("21day来源响应无效，已保留缓存。");
        foreach (var c in result.Cards)
            if (c.SourceProject != "21day" || !Guid.TryParse(c.SourceId, out _) || c.SourceRevision < 1 || c.SourceRevision > result.Cursor || !DateOnly.TryParseExact(c.Day, "yyyy-MM-dd", out _) ||
                string.IsNullOrWhiteSpace(c.Name) || c.Input is not ("COUNT" or "DAILY" or "TIMER" or "MANUAL") || c.Actions is null ||
                c.Data.ValueKind != JsonValueKind.Object || c.Data.GetProperty("plan").GetProperty("id").GetString() != c.SourceId)
                throw new InvalidDataException("21day卡片格式无效，已保留缓存。");
        foreach (var card in result.Cards) ValidateDetails(card);
        queue.Execute(u =>
        {
            Execute(u, "DELETE FROM day21_source_cards WHERE owner=$owner", ("$owner", owner.ToString()));
            Execute(u, "INSERT INTO day21_source_meta(owner,cursor,fetched_at) VALUES($owner,$cursor,$at) ON CONFLICT(owner) DO UPDATE SET cursor=excluded.cursor,fetched_at=excluded.fetched_at", ("$owner", owner.ToString()), ("$cursor", result.Cursor), ("$at", utcNow().ToString("O")));
            foreach (var card in result.Cards)
                Execute(u, "INSERT INTO day21_source_cards(owner,id,payload,fetched_at) VALUES($owner,$id,$payload,$at)", ("$owner", owner.ToString()), ("$id", card.SourceId), ("$payload", JsonSerializer.Serialize(card, Json)), ("$at", utcNow().ToString("O")));
        });
    }
    async Task<string> SendRequest(Guid owner, Guid id, CancellationToken ct)
    {
        var body = queue.Execute(u =>
        { using var c = Command(u, "SELECT body FROM day21_source_requests WHERE operation_id=$id AND owner=$owner AND status='pending'", ("$id", id.ToString()), ("$owner", owner.ToString())); return c.ExecuteScalar() as string ?? throw new InvalidOperationException("原请求不存在或已确认。"); });
        using var response = await Send(owner, HttpMethod.Post, "commands", body, ct, true);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            queue.Execute(u => Execute(u, "UPDATE day21_source_requests SET status='invalid',message=$message WHERE operation_id=$id AND owner=$owner", ("$id", id.ToString()), ("$owner", owner.ToString()), ("$message", "来源校验拒绝此请求，请刷新并核对来源当前总量，再决定结束此请求。")));
            return "invalid";
        }
        var result = await response.Content.ReadFromJsonAsync<CommandResult>(Json, ct) ?? throw new InvalidDataException("来源回执为空。");
        if (result.OperationId != id || result.Revision < 0 || result.Status is not ("applied" or "conflict" or "invalid" or "deleted" or "unsupported") || result.Status == "applied" && result.Current is null)
            throw new InvalidDataException("来源回执无效，原请求已保留。");
        var message = result.Status switch { "conflict" => "两端都修改过此记录，采用来源前请先查看21day当前记录。", "deleted" => "来源已删除该习惯。", "invalid" or "unsupported" => "来源规则不允许此操作，请刷新后重新选择。", _ => "来源已确认。" };
        queue.Execute(u =>
        {
            if (result.Status == "applied")
            {
                var request = JsonSerializer.Deserialize<Day21HabitRequest>(body, Json)!;
                using var read = Command(u, "SELECT payload FROM day21_source_cards WHERE owner=$owner AND id=$source", ("$owner", owner.ToString()), ("$source", request.Operation.EntityId));
                var cached = read.ExecuteScalar() as string;
                if (cached is not null && JsonSerializer.Deserialize<Day21HabitCard>(cached, Json) is { } card && card.SourceRevision <= result.Revision)
                {
                    var data = result.Current!.Value;
                    var plan = data.GetProperty("plan");
                    if (plan.GetProperty("id").GetString() != card.SourceId) throw new InvalidDataException("来源回执标识无效，原请求已保留。");
                    var updated = card with { SourceRevision = result.Revision, Data = data.Clone(),
                        Entry = data.GetProperty("entries").TryGetProperty(card.Day, out var entry) ? entry.Clone() : null,
                        Timer = data.GetProperty("timer").ValueKind == JsonValueKind.Null ? null : data.GetProperty("timer").Clone(),
                        Name = plan.GetProperty("name").GetString()!, Scheduled = card.Scheduled && plan.GetProperty("archivedOn").ValueKind == JsonValueKind.Null, Actions = [] };
                    ValidateDetails(updated);
                    Execute(u, "UPDATE day21_source_cards SET payload=$payload WHERE owner=$owner AND id=$source", ("$owner", owner.ToString()), ("$source", card.SourceId), ("$payload", JsonSerializer.Serialize(updated, Json)));
                }
            }
            Execute(u, "UPDATE day21_source_requests SET status=$status,message=$message,response=$response WHERE operation_id=$id AND owner=$owner", ("$id", id.ToString()), ("$owner", owner.ToString()), ("$status", result.Status), ("$message", message), ("$response", JsonSerializer.Serialize(result, Json)));
        });
        return result.Status;
    }
    Guid Owner() => account.Account?.UserId ?? throw new InvalidOperationException("请先在设置的头像入口登录同一个邮箱账号。");
    async Task<HttpResponseMessage> Send(Guid owner, HttpMethod method, string path, string? body, CancellationToken ct, bool allowBadRequest = false)
    {
        if (Owner() != owner) throw new InvalidOperationException("账号已变化，已停止来源操作。");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await account.AccessTokenAsync(ct));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request, ct);
        if (Owner() != owner) { response.Dispose(); throw new InvalidOperationException("账号已变化，已停止来源操作。"); }
        if (response.IsSuccessStatusCode || allowBadRequest && response.StatusCode == HttpStatusCode.BadRequest) return response;
        var status = response.StatusCode; response.Dispose();
        throw new InvalidOperationException(status switch { HttpStatusCode.NotFound => "21day来源接口尚未部署。", HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "来源登录已失效或没有访问权限，请重新登录。", HttpStatusCode.BadRequest => "来源规则拒绝此操作，原请求已保留，请刷新来源后核对。", _ when (int)status is >= 300 and < 400 => "来源服务返回重定向，已停止发送令牌及数据。", _ => "21day暂时不可用，缓存与原请求已保留。" });
    }
    static SqliteCommand Command(IUnitOfWork u, string sql, params (string Name, object? Value)[] values)
    { var c = u.Connection.CreateCommand(); c.Transaction = u.Transaction; c.CommandText = sql; foreach (var p in values) c.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value); return c; }
    static void Execute(IUnitOfWork u, string sql, params (string Name, object? Value)[] values) { using var c = Command(u, sql, values); c.ExecuteNonQuery(); }
    public void Dispose() => http.Dispose();
    static void ValidateDetails(Day21HabitCard card)
    {
        if (!card.Data.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object || !card.Data.TryGetProperty("timer", out var timer)) throw new InvalidDataException("来源记录结构无效。");
        foreach (var entry in entries.EnumerateObject())
        {
            var value = entry.Value;
            if (!DateOnly.TryParseExact(entry.Name, "yyyy-MM-dd", out _) || value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("value", out var total) || !total.TryGetInt32(out var count) || count is < 0 or > 100000 ||
                !value.TryGetProperty("note", out var note) || note.ValueKind != JsonValueKind.String) throw new InvalidDataException("来源每日记录无效。");
        }
        if (timer.ValueKind != JsonValueKind.Null && (timer.ValueKind != JsonValueKind.Object || !timer.TryGetProperty("device", out var device) || !Guid.TryParse(device.GetString(), out _))) throw new InvalidDataException("来源计时结构无效。");
        var dataEntry = entries.TryGetProperty(card.Day, out var found) ? found : (JsonElement?)null;
        if (!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(dataEntry?.GetRawText() ?? "null"), System.Text.Json.Nodes.JsonNode.Parse(card.Entry?.GetRawText() ?? "null")) ||
            !System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(timer.GetRawText()), System.Text.Json.Nodes.JsonNode.Parse(card.Timer?.GetRawText() ?? "null"))) throw new InvalidDataException("来源快照不一致。");
    }
    sealed record CardsResponse(string SourceProject, long Cursor, IReadOnlyList<Day21HabitCard> Cards);
    sealed record CommandResult(Guid OperationId, string Status, long Revision, JsonElement? Current);
}
