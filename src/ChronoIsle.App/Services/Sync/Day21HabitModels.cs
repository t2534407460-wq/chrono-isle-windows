using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChronoIsle.App.Services.Sync;

public sealed record Day21HabitCard(string SourceProject, string SourceId, long SourceRevision, string Day,
    string Name, string Unit, string Input, bool Scheduled, int? Target, JsonElement? Entry, JsonElement? Timer,
    JsonElement Data, string[] Actions)
{
    public int? Value => Entry is { ValueKind: JsonValueKind.Object } entry ? entry.GetProperty("value").GetInt32() : null;
    public string Summary => (Value is { } value ? $"已记录 {value} {Unit}" : "今日尚未记录") + (Target is { } target ? $" · 目标 {target} {Unit}" : "") +
        (Timer is { ValueKind: JsonValueKind.Object } ? " · 计时中" : !Scheduled ? " · 今日无需打卡" : "");
}
public sealed record Day21HabitOperation(Guid OperationId, string EntityType, string EntityId, long BaseRevision, int SchemaVersion,
    JsonElement Data, bool Deleted, JsonElement Baseline, string Kind);
public sealed record Day21HabitRequest(Day21HabitOperation Operation, string Action, string Day, string TimeZoneId, string DeviceId);
public sealed record Day21PendingRequest(Guid OperationId, string SourceId, string Action, string Status, string Message);

public static class Day21HabitCommandBuilder
{
    public static Day21HabitRequest Build(Day21HabitCard card, string action, Guid device, string timeZoneId,
        DateTimeOffset now, int? total = null, string? note = null, bool takeoverConfirmed = false)
    {
        if (device == Guid.Empty || !card.Actions.Contains(action, StringComparer.Ordinal)) throw new InvalidOperationException("来源不允许该操作，请刷新21day记录。");
        var original = JsonNode.Parse(card.Data.GetRawText())!.AsObject(); var requested = original.DeepClone();
        var timerAction = action is "timer_finish" or "timer_cancel" or "timer_takeover";
        var day = timerAction ? original["timer"]?["day"]?.GetValue<string>() ?? throw new InvalidOperationException("计时已发生变化，请刷新来源。") : card.Day;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).DateTime).ToString("yyyy-MM-dd");
        if (!timerAction && action != "archive" && (day != today || !card.Scheduled)) throw new InvalidOperationException("来源日期已变化或今日无需打卡，请刷新来源。");
        var entries = requested["entries"]!.AsObject(); var prior = original["entries"]?[day];
        JsonNode Entry() => prior?.DeepClone() ?? JsonSerializer.SerializeToNode(new { habitId = card.SourceId, date = day, value = 0, note = "", recordedAt = now.ToUnixTimeMilliseconds(), remainderSeconds = 0, sessions = Array.Empty<object>() })!;
        var stamp = now.ToUnixTimeMilliseconds();
        if (action == "count")
        {
            var row = Entry(); row["value"] = checked((prior?["value"]?.GetValue<int>() ?? 0) + 1); row["recordedAt"] = stamp; entries[day] = row;
        }
        else if (action == "confirm")
        {
            var row = Entry(); row["value"] = original["plan"]!["smoking"]?.GetValue<bool>() == true ? 0 : 1; row["recordedAt"] = stamp; entries[day] = row;
        }
        else if (action == "set_total")
        {
            if (total is null or < 0 or > 100000 || note?.Length > 300) throw new InvalidOperationException("总量需为0至100000的整数，备注不能超过300字。");
            var row = Entry(); row["value"] = total.Value; row["note"] = note ?? prior?["note"]?.GetValue<string>() ?? ""; row["recordedAt"] = stamp;
            if (total != prior?["value"]?.GetValue<int>()) { row["sessions"] = new JsonArray(); row["remainderSeconds"] = 0; }
            entries[day] = row;
        }
        else if (action == "timer_start")
            requested["timer"] = JsonSerializer.SerializeToNode(new { at = stamp, day, id = Guid.NewGuid(), device });
        else if (action == "timer_takeover")
        {
            if (!takeoverConfirmed) throw new InvalidOperationException("接管计时需要在线刷新并确认。");
            requested["timer"]!["device"] = device.ToString();
        }
        else if (action is "timer_finish" or "timer_cancel")
        {
            var timer = original["timer"] ?? throw new InvalidOperationException("计时已结束，请刷新来源。");
            if (timer["device"]!.GetValue<string>() != device.ToString()) throw new InvalidOperationException("此计时属于另一设备，请先在线接管。");
            requested["timer"] = null;
            if (action == "timer_finish")
            {
                var start = timer["at"]!.GetValue<long>(); if (stamp < start) throw new InvalidOperationException("系统时间早于计时开始时间，请校准时间。");
                var seconds = (prior?["value"]?.GetValue<int>() ?? 0) * 60L + (prior?["remainderSeconds"]?.GetValue<int>() ?? 0) + (stamp - start) / 1000;
                var row = Entry(); row["value"] = checked((int)(seconds / 60)); row["remainderSeconds"] = (int)(seconds % 60); row["recordedAt"] = stamp;
                var sessions = row["sessions"]!.AsArray();
                if (sessions.Count >= 200) throw new InvalidOperationException("当日计时明细已达到200段，请先在来源中核对总量。");
                sessions.Add(JsonSerializer.SerializeToNode(new { start, end = stamp })); entries[day] = row;
            }
        }
        else if (action == "archive") requested["plan"]!["archivedOn"] = today;
        else throw new InvalidOperationException("当前时屿不支持该来源操作。");
        if (entries[day]?["value"]?.GetValue<int>() > 100000) throw new InvalidOperationException("来源记录已达到总量上限。");
        if (original["plan"]!["mode"]?.GetValue<string>() == "CHECK" && entries[day]?["value"]?.GetValue<int>() > 1) throw new InvalidOperationException("每日确认的总量只能为0或1。");
        var kind = action is "count" or "timer_finish" or "timer_takeover" ? action : "replace";
        return new(new(Guid.NewGuid(), "habits", card.SourceId, card.SourceRevision, 1, JsonSerializer.SerializeToElement(requested), false, card.Data.Clone(), kind), action, day, timeZoneId, device.ToString());
    }
}
