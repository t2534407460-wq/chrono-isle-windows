using System.Globalization;
using System.Text.Json.Serialization;

namespace ChronoIsle.App.Services.Domain;

public sealed record ReminderTimeWindow(TimeOnly Start, TimeOnly End)
{
    // 18:00–00:00 ends at the boundary of this day; it does not run into the next day.
    [JsonIgnore] public double EndMinute => End == TimeOnly.MinValue && Start > TimeOnly.MinValue
        ? 1440 : End.ToTimeSpan().TotalMinutes;
    [JsonIgnore] public string? ValidationError => EndMinute > Start.ToTimeSpan().TotalMinutes ? null
        : End == Start ? "开始和结束时刻不能相同，请填写明确的时间段。"
        : "结束时刻需晚于开始；到当天午夜可填写 00:00，其他跨午夜时段请分开安排。";
    public bool Contains(TimeOnly time) => time >= Start && time.ToTimeSpan().TotalMinutes < EndMinute;
}

/// <summary>A single reminder series with multiple daily occurrences. Preview and delivery share these times.</summary>
public sealed record ReminderDailySchedule(
    IReadOnlyList<TimeOnly> Times, string DayPattern, IReadOnlyList<DayOfWeek> Weekdays,
    DateOnly StartsOn, DateOnly? Until, string Description,
    int? IntervalMinutes = null, ReminderTimeWindow? Window = null,
    IReadOnlyList<ReminderTimeWindow>? Exclusions = null, string? FirstTrigger = null, string? Rhythm = null)
{
    public void Validate()
    {
        if (Times is null || Times.Count is < 1 or > 96 || Times.Distinct().Count() != Times.Count ||
            !Times.SequenceEqual(Times.OrderBy(t => t)))
            throw new ArgumentException("每日需有 1–96 个不重复、按顺序排列的提醒时刻。");
        if (DayPattern is not ("daily" or "weekdays" or "official" or "custom"))
            throw new ArgumentException("请选择执行日期规则。");
        if (Weekdays is null || DayPattern == "custom" && (Weekdays.Count == 0 || Weekdays.Any(d => !Enum.IsDefined(d))))
            throw new ArgumentException("至少选择一个星期。");
        if (Until < StartsOn) throw new ArgumentException("结束日期不能早于开始日期。");
        if (IntervalMinutes is { } interval)
        {
            if (Window is null || !Times.SequenceEqual(GenerateTimes(Window, interval, Exclusions ?? [], FirstTrigger!, Rhythm!)))
                throw new ArgumentException("实际提醒时刻与时间窗口、间隔或排除条件不一致。");
        }
    }

    public bool Matches(DateOnly day)
    {
        if (day < StartsOn || Until is { } end && day > end) return false;
        return DayPattern switch
        {
            "daily" => true,
            "weekdays" => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
            "custom" => Weekdays.Contains(day.DayOfWeek),
            "official" => !new ChinaStatutoryHolidayCalendar().IsRestDay(day.ToDateTime(TimeOnly.MinValue)),
            _ => false
        };
    }

    public IEnumerable<DateTime> Occurrences(DateOnly day) => Matches(day)
        ? Times.Select(t => day.ToDateTime(t)) : [];

    public bool CanDeliver(DateTime occurrence, DateTime now)
    {
        var clock = TimeOnly.FromDateTime(now);
        if (Window is { } window && (!window.Contains(clock) ||
            (Exclusions ?? []).Any(p => p.Contains(clock)))) return false;
        // Suppress yesterday's queued notifications and older missed intervals.
        return Occurrences(DateOnly.FromDateTime(now)).Where(t => t <= now).LastOrDefault() == occurrence;
    }

    public IReadOnlyList<DateTime> Next(DateTime after, int count = 6)
    {
        var found = new List<DateTime>();
        var start = DateOnly.FromDateTime(after) > StartsOn ? DateOnly.FromDateTime(after) : StartsOn;
        for (var d = start; d <= start.AddDays(550) && found.Count < count; d = d.AddDays(1))
        {
            if (Until is { } end && d > end) break;
            found.AddRange(Occurrences(d).Where(t => t > after).Take(count - found.Count));
        }
        return found;
    }

    public static IReadOnlyList<TimeOnly> GenerateTimes(ReminderTimeWindow window, int interval,
        IReadOnlyList<ReminderTimeWindow> exclusions, string first, string rhythm)
    {
        if (interval is < 5 or > 1440) throw new ArgumentException("间隔需为 5–1440 分钟。");
        if (window.ValidationError is { } error) throw new ArgumentException(error);
        if (first is not ("start" or "after_interval") || rhythm is not ("skip" or "restart"))
            throw new ArgumentException("请明确首次提醒与休息后计时方式。");
        var pauses = exclusions.OrderBy(w => w.Start).ToArray();
        for (var i = 0; i < pauses.Length; i++)
            if (pauses[i].ValidationError is not null || pauses[i].Start < window.Start || pauses[i].EndMinute > window.EndMinute ||
                i > 0 && pauses[i].Start.ToTimeSpan().TotalMinutes < pauses[i - 1].EndMinute)
                throw new ArgumentException("排除时段必须位于工作时段内，且不能相互重叠。");
        double Minute(TimeOnly t) => t.ToTimeSpan().TotalMinutes;
        var times = new List<TimeOnly>();
        var cursor = Minute(window.Start) + (first == "start" ? 0 : interval);
        var pauseIndex = 0;
        while (cursor < window.EndMinute)
        {
            if (rhythm == "restart" && pauseIndex < pauses.Length && cursor >= Minute(pauses[pauseIndex].Start))
            {
                cursor = pauses[pauseIndex++].EndMinute + interval;
                continue;
            }
            var time = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(cursor));
            if (!pauses.Any(p => p.Contains(time))) times.Add(time);
            cursor += interval;
        }
        if (times.Count == 0) throw new ArgumentException("当前条件下没有提醒时刻，请扩大时段或缩短间隔。");
        if (times.Count > 96) throw new ArgumentException("每日提醒不能超过 96 次，请增大间隔。");
        return times;
    }

    public static string? WindowInputError(string? text) => ParseWindow(text) is { } window
        ? window.ValidationError : "请按 24 小时制填写开始和结束时刻，例如 18:00–00:00（HH:mm）。";

    public static ReminderTimeWindow? ParseWindow(string? text)
    {
        var parts = text?.Split('-', StringSplitOptions.TrimEntries);
        return parts?.Length == 2 && TimeOnly.TryParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) &&
            TimeOnly.TryParseExact(parts[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            ? new(start, end) : null;
    }
}
