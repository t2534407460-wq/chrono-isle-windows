using System.Text.Json;
using System.Text.Json.Nodes;
using ChronoIsle.App.Services.Sync;
using Day21.Server;
using ProjectInterop.Sync;

public sealed class CommandContractTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(8));
    static readonly Guid Device = Guid.NewGuid();
    [Theory]
    [InlineData("count", "COUNT")][InlineData("confirm", "DAILY")][InlineData("set_total", "MANUAL")]
    [InlineData("timer_start", "TIMER")][InlineData("timer_finish", "TIMER")][InlineData("timer_cancel", "TIMER")]
    [InlineData("timer_takeover", "TIMER")][InlineData("archive", "COUNT")]
    public void DesktopRequestIsAcceptedByTheActualPhoneBackend(string action, string input)
    {
        var card = Card(input, action);
        var request = Day21HabitCommandBuilder.Build(card, action, Device, "Asia/Shanghai", Now, total: 0, note: "更正备注", takeoverConfirmed: true);
        var command = JsonSerializer.Deserialize<HabitCommand>(JsonSerializer.Serialize(request, Json), Json)!;
        HabitEndpoints.ValidateCommand(command, Now);
        var current = new SyncEntity("habits", card.SourceId, card.SourceRevision, 1, card.Data, false);
        var result = new Day21Policy().Resolve(command.Operation, current);
        Assert.Equal("applied", result.Status);
        var data = JsonNode.Parse(result.Data!.Value.GetRawText())!;
        if (action == "count") { Assert.Equal(3, data["entries"]![card.Day]!["value"]!.GetValue<int>()); Assert.Equal("原备注", data["entries"]![card.Day]!["note"]!.GetValue<string>()); }
        if (action == "confirm") Assert.Equal(1, data["entries"]![card.Day]!["value"]!.GetValue<int>());
        if (action == "set_total") Assert.Equal(0, data["entries"]![card.Day]!["value"]!.GetValue<int>());
        if (action == "timer_finish") { Assert.Null(data["timer"]); Assert.Equal(4, data["entries"]![card.Day]!["value"]!.GetValue<int>()); Assert.Equal(0, data["entries"]![card.Day]!["remainderSeconds"]!.GetValue<int>()); }
        if (action == "timer_takeover") { Assert.Equal(Device.ToString(), data["timer"]!["device"]!.GetValue<string>()); Assert.Equal(card.Timer!.Value.GetProperty("at").GetInt64(), data["timer"]!["at"]!.GetValue<long>()); }
        if (action == "archive") Assert.Equal(card.Day, data["plan"]!["archivedOn"]!.GetValue<string>());
    }
    [Fact] public void CrossMidnightFinishUsesTheStartingDayAndPreservesPriorNotesAndSeconds()
    {
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 20, TimeSpan.FromHours(8));
        var card = Card("TIMER", "timer_finish", timerDay: "2026-10-04", at: now.AddSeconds(-70));
        var request = Day21HabitCommandBuilder.Build(card, "timer_finish", Device, "Asia/Shanghai", now);
        var command = JsonSerializer.Deserialize<HabitCommand>(JsonSerializer.Serialize(request, Json), Json)!;
        HabitEndpoints.ValidateCommand(command, now);
        var result = new Day21Policy().Resolve(command.Operation, new("habits", card.SourceId, 1, 1, card.Data, false));
        Assert.Equal("applied", result.Status); Assert.Equal("2026-10-04", command.Day);
        var entry = result.Data!.Value.GetProperty("entries").GetProperty(command.Day);
        Assert.Equal(4, entry.GetProperty("value").GetInt32()); Assert.Equal("原备注", entry.GetProperty("note").GetString());
        Assert.False(result.Data.Value.GetProperty("entries").TryGetProperty("2026-10-05", out _));
    }
    [Fact] public void AtomicCountPreservesAConcurrentCountInsteadOfReplacingIt()
    {
        var card = Card("COUNT", "count"); var current = JsonNode.Parse(card.Data.GetRawText())!;
        current["entries"]![card.Day]!["value"] = 5;
        var request = Day21HabitCommandBuilder.Build(card, "count", Device, "Asia/Shanghai", Now);
        var command = JsonSerializer.Deserialize<HabitCommand>(JsonSerializer.Serialize(request, Json), Json)!;
        HabitEndpoints.ValidateCommand(command, Now);
        var result = new Day21Policy().Resolve(command.Operation, new("habits", card.SourceId, 2, 1, JsonSerializer.SerializeToElement(current), false));
        Assert.Equal("applied", result.Status); Assert.Equal(6, result.Data!.Value.GetProperty("entries").GetProperty(card.Day).GetProperty("value").GetInt32());
    }
    [Fact] public void ManualTotalCorrectionConflictsWithAnotherDevicesDifferentCorrection()
    {
        var card = Card("MANUAL", "set_total"); var current = JsonNode.Parse(card.Data.GetRawText())!; current["entries"]![card.Day]!["value"] = 5;
        var request = Day21HabitCommandBuilder.Build(card, "set_total", Device, "Asia/Shanghai", Now, total: 0);
        var command = JsonSerializer.Deserialize<HabitCommand>(JsonSerializer.Serialize(request, Json), Json)!;
        HabitEndpoints.ValidateCommand(command, Now);
        Assert.Equal("conflict", new Day21Policy().Resolve(command.Operation, new("habits", card.SourceId, 2, 1, JsonSerializer.SerializeToElement(current), false)).Status);
    }
    static Day21HabitCard Card(string input, string action, string? timerDay = null, DateTimeOffset? at = null)
    {
        var id = Guid.NewGuid().ToString(); var day = "2026-10-05"; var recordDay = timerDay ?? day;
        var entries = new JsonObject();
        if (input != "DAILY") entries[recordDay] = JsonSerializer.SerializeToNode(new { habitId = id, date = recordDay, value = 2, note = "原备注", recordedAt = Now.AddMinutes(-3).ToUnixTimeMilliseconds(), remainderSeconds = input == "TIMER" ? 50 : 0, sessions = Array.Empty<object>() });
        var timer = action is "timer_finish" or "timer_cancel" or "timer_takeover" ? JsonSerializer.SerializeToNode(new { at = (at ?? Now.AddSeconds(-70)).ToUnixTimeMilliseconds(), day = recordDay, id = Guid.NewGuid(), device = action == "timer_takeover" ? Guid.NewGuid() : Device }) : null;
        var data = new JsonObject { ["plan"] = JsonSerializer.SerializeToNode(new { id, name = "合同验证", start = "2026-10-04", mode = input == "DAILY" ? "CHECK" : "AT_LEAST", unit = input == "TIMER" ? "分钟" : "次", input, rules = new[] { new { from = "2026-10-04", days = new[] { 1, 2, 3, 4, 5, 6, 7 }, target = input == "DAILY" ? 1 : 20, reminder = 1260 } }, archivedOn = (string?)null, smoking = false, visual = "READING" }), ["entries"] = entries, ["timer"] = timer };
        return new("21day", id, 1, day, "合同验证", input == "TIMER" ? "分钟" : "次", input, true, 20,
            entries[day] is { } entry ? JsonSerializer.SerializeToElement(entry) : null, timer is null ? null : JsonSerializer.SerializeToElement(timer), JsonSerializer.SerializeToElement(data), [action]);
    }
}
