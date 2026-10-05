using System.Globalization;
using System.Text.RegularExpressions;
using ChronoIsle.App.Services.Domain;

namespace ChronoIsle.App.Services.Commanding;

public sealed record AssistantSchedulePlan(AssistantTaskDraft Draft, AssistantCommandEnvelope? Command,
    IReadOnlyList<AssistantInputField> Fields, string Summary, IReadOnlyList<string> Facts,
    IReadOnlyList<string> Preview, string? Blocked = null);

/// <summary>Local interaction planning; model language is never an executable schedule.</summary>
public static class AssistantScenarioPlanner
{
    static readonly Regex IntervalPattern = new(@"(?:每隔|间隔|每)\s*(?<n>\d+|[一二两三四五六七八九十半]+)\s*(?<u>小时|分钟)");
    static readonly Regex ClockPattern = new(@"(?:凌晨|早上|早晨|上午|中午|下午|晚上|晚间)?[0-9零〇一二两三四五六七八九十]{1,3}(?:[:：]\d{2}|点(?:半|一刻|三刻|[0-9零〇一二三四五六七八九十]{1,3}分?)?)");
    static readonly Regex RangePattern = new(@"(?<a>(?:上午|下午|晚上)?\d{1,2}(?:[:：]\d{2}|点))\s*(?:-|—|~|～|到|至)\s*(?<b>(?:上午|下午|晚上)?\d{1,2}(?:[:：]\d{2}|点))");

    public static AssistantTaskDraft Enrich(AssistantTaskDraft task, string source)
    {
        if (task.Operation is not ("create_reminder" or "create_recurring_task")) return task;
        var text = source + "\n" + task.Evidence + "\n" + task.TimeText;
        if (string.IsNullOrWhiteSpace(task.RepeatText))
        {
            var repeat = Regex.Match(text, @"每天|每日|每周[一二三四五六日天](?:[、和][一二三四五六日天])*|每月\d{1,2}[日号]|工作日");
            if (repeat.Success) task = task with { RepeatText = repeat.Value };
        }
        var interval = IntervalPattern.Match(text);
        var repeating = task.RepeatText is not null || Regex.IsMatch(text, "每天|每日|每周|工作日|工作时间");
        var multiple = repeating && (Regex.IsMatch(text, "早晚|早中晚") ||
            ClockPattern.Matches(task.TimeText ?? task.Evidence).Count > 1 && !RangePattern.IsMatch(task.TimeText ?? task.Evidence));
        var restricted = Regex.IsMatch(text, "工作时间|工作时段|上班时间|排除|除午休|除了午休");
        if (task.Schedule is null && !interval.Success && !multiple && !restricted) return task;
        var schedule = task.Schedule ?? new AssistantScheduleDraft();
        var range = RangePattern.Match(text);
        var exclusion = Regex.Match(text, @"(?:午休|排除|除外|除了)(?<text>[^，,。；;\n]*)");
        var workWindow = Regex.IsMatch(text, "工作时间|工作时段|上班时间");
        var until = Regex.Match(text, @"(?:直到|截止到?|截至|持续到)\s*(?<date>\d{4}-\d{2}-\d{2})");
        var explicitStart = Regex.Match(text, @"从\s*(?<date>\d{4}-\d{2}-\d{2}|今天|明天)\s*(?:开始|起)");
        AssistantDraftCompiler.TryRecurrence(task.RepeatText ?? "", out var recurrence);
        var daily = text.Contains("每天") && !workWindow ? "daily" : null;
        var days = text.Contains("法定工作日") ? "official" : text.Contains("周一至周五") ? "weekdays" : daily;
        schedule = schedule with
        {
            IntervalText = schedule.IntervalText ?? (interval.Success ? interval.Groups["n"].Value + interval.Groups["u"].Value : null),
            WindowText = schedule.WindowText ?? (range.Success && !text[..range.Index].EndsWith("午休") ? range.Value : null),
            ExclusionText = schedule.ExclusionText ?? (exclusion.Success ? exclusion.Value : text.Contains("午休") ? "午休" : "none"),
            DaysText = schedule.DaysText ?? days ?? (recurrence?.Frequency == AssistantRecurrenceFrequencyV1.Weekly && task.RepeatText != "工作日" ? "custom" : null),
            WeekdaysText = schedule.WeekdaysText ?? (recurrence?.Weekdays is { Count: > 0 } list ? string.Join(",", list.Select(d => (int)d)) : null),
            UntilText = schedule.UntilText ?? (until.Success ? until.Groups["date"].Value : null),
            StartText = schedule.StartText ?? (explicitStart.Success ? explicitStart.Groups["date"].Value : null),
            TimesText = schedule.TimesText ?? (multiple ? task.TimeText ?? task.Evidence : null)
        };
        return task with { Schedule = schedule };
    }

    public static AssistantTaskDraft MergeExisting(AssistantTaskDraft task, RecurringReminder existing, AssistantDraftTurn turn)
    {
        if (task.Schedule is null && task.TimeText is null && task.ReminderText is null && task.RepeatText is null) return task;
        if (existing.Recurrence == RecurrenceKind.StatutoryHolidays)
            return task.Schedule is null && task.RepeatText is null ? task : task with
            { UnhandledConstraints = ["节假日计划暂不支持更换重复规则；可以单独调整提醒时间"] };
        if (task.RepeatText == "不重复") return task with
        { UnhandledConstraints = ["将周期计划转换为单次事项需要另行明确日期，当前不会新建或删除原计划"] };
        var old = existing.Schedule;
        var next = task.Schedule ?? new AssistantScheduleDraft();
        var days = next.DaysText ?? task.RepeatText;
        var weekdays = next.WeekdaysText;
        if (days is not null && DayMode(days) is null && AssistantDraftCompiler.TryRecurrence(days, out var rule) &&
            rule!.Frequency == AssistantRecurrenceFrequencyV1.Weekly && days != "工作日")
        {
            days = "custom";
            weekdays = string.Join(',', rule.Weekdays!.Select(d => (int)d));
        }
        var newClock = next.TimesText ?? task.TimeText ?? task.ReminderText;
        var switchesToInterval = next.IntervalText is not null && next.TimesText is null;
        var fixedTimes = newClock is not null || !switchesToInterval && old?.IntervalMinutes is null;
        if (newClock is not null && next.IntervalText is not null || fixedTimes && next.WindowText is not null)
            return task with { UnhandledConstraints = (task.UnhandledConstraints ?? [])
                .Append("固定时刻与间隔或有效时段同时变更，需要先明确采用哪种提醒方式").ToArray() };
        var start = next.StartText;
        if (start is null && newClock is { } text && Regex.IsMatch(text, @"\d{4}[-/年]|今天|明天|后天|下周|明早|明晚"))
            start = AssistantDraftCompiler.ParseTime(text, turn.ReferenceTime, turn.TimeZone)?.LocalDate?.ToString("yyyy-MM-dd");
        var schedule = new AssistantScheduleDraft(
            fixedTimes ? null : next.IntervalText ?? old?.IntervalMinutes?.ToString(),
            fixedTimes ? null : next.WindowText ?? (old?.Window is { } window ? Format(window) : null),
            next.ExclusionText ?? (newClock is not null ? "none" : old?.Exclusions is { Count: > 0 } exclusions
                ? string.Join(';', exclusions.Select(Format)) : "none"),
            days ?? old?.DayPattern ?? (existing.Recurrence switch
            { RecurrenceKind.Weekly => "custom", RecurrenceKind.Weekdays => "weekdays", RecurrenceKind.OfficialWorkdays => "official", _ => "daily" }),
            fixedTimes ? newClock ?? string.Join(",", (old?.Times ?? [existing.ReminderTime]).Select(t => t.ToString("HH:mm"))) : null,
            start ?? old?.StartsOn.ToString("yyyy-MM-dd") ?? existing.StartsAt?.ToString("yyyy-MM-dd") ?? turn.ReferenceTime.ToString("yyyy-MM-dd"),
            next.UntilText ?? old?.Until?.ToString("yyyy-MM-dd"),
            fixedTimes ? null : next.FirstTrigger ?? old?.FirstTrigger,
            fixedTimes ? null : next.Rhythm ?? old?.Rhythm,
            weekdays ?? string.Join(',', (old?.Weekdays ?? existing.Weekdays).Select(d => (int)d)),
            next.ReplaceDayOverrides ? next.DayOverrides : MergeOverrides(old?.DayOverrides?.Select(r => new AssistantDayOverrideDraft(r.DayPattern,
                string.Join(",", r.Times.Select(t => t.ToString("HH:mm"))))).ToArray(), next.DayOverrides));
        return task with { Schedule = schedule, ItemKind = "reminder", TimeText = null, ReminderText = null, RepeatText = null };
    }

    public static AssistantSchedulePlan Compile(AssistantTaskDraft draft, AssistantDraftTurn turn, int index, bool edit)
    {
        var s = draft.Schedule!;
        var updating = draft.Operation is "update_todo" or "reschedule_item";
        var target = turn.Bindings.GetValueOrDefault(index);
        if (updating && target is null)
            return new(draft, null, [], draft.Evidence, [], [], "请先选择要修改的已有计划。");
        if (draft.ItemKind is not (null or "reminder"))
            return new(draft, null, [], draft.Evidence, [], [], "目前日内多次计划支持提醒；周期待办或日程请调整需求后再安排。");
        if (draft.EndText is not null || draft.DueText is not null || draft.ReminderText is not null && draft.ReminderText != draft.TimeText)
            return new(draft, null, [], draft.Evidence, [], [], "这个重复计划还包含独立的结束、截止或额外提醒条件，需要修改需求以明确各项时间，当前不会忽略它们。");
        var fields = new List<AssistantInputField>();
        var facts = new List<string> { (updating ? "修改：" : "新增：") + (target?.Title ?? draft.Title ?? "待填写") };
        if (updating && draft.Title is not null) facts.Add("新名称：" + draft.Title);
        void Add(string key, string label, string kind, string? value = null, string? help = null,
            IReadOnlyList<AssistantInputOption>? options = null, bool required = true, string? depends = null, string? dependsValue = null, string? error = null) =>
            fields.Add(new($"{index}.schedule.{key}", label, kind, options ?? [], value, help, required,
                depends is null ? null : $"{index}.schedule.{depends}", dependsValue, error));
        if (!updating && (string.IsNullOrWhiteSpace(draft.Title) || edit))
            fields.Add(new($"{index}.title", "这份计划提醒你做什么？", "text", [], draft.Title));
        var fixedTimes = !string.IsNullOrWhiteSpace(s.TimesText);
        var interval = Minutes(s.IntervalText);
        if (!fixedTimes && (interval is null || edit))
            Add("interval", "每隔多久提醒一次？", "duration", interval?.ToString(), "可选建议，也可输入 5–1440 分钟。", DurationOptions);
        if (!fixedTimes && interval is not null) facts.Add($"频率：每 {interval} 分钟");
        var days = DayMode(s.DaysText);
        var selectedDays = ReadWeekdays(s.WeekdaysText);
        if (days is null || edit)
        {
            Add("days", "哪些日子执行？", "choice", days, "“工作时间”不等于“工作日”；法定工作日包含调休。", DayOptions);
            Add("weekdays", "选择每周执行的日期", "multichoice", s.WeekdaysText, null, WeekdayOptions,
                depends: "days", dependsValue: "custom");
        }
        else if (days == "custom" && selectedDays.Count == 0) Add("weekdays", "选择执行日期", "multichoice", s.WeekdaysText, options: WeekdayOptions);
        if (days is not null) facts.Add("执行日：" + DayOptions.First(o => o.Value == days).Label +
            (days == "custom" ? " " + string.Join("、", selectedDays.Select(d => WeekdayOptions.First(o => o.Value == ((int)d).ToString()).Label)) : ""));
        var overrides = new List<ReminderDayOverride>();
        for (var r = 0; r < (s.DayOverrides?.Count ?? 0); r++)
        {
            var rule = s.DayOverrides![r];
            var mode = OverrideMode(rule.DaysText);
            var clocks = ParseTimes(rule.TimesText ?? "", turn);
            if (mode is null || edit)
                Add($"override{r}days", $"第 {r + 1} 组：哪些日期使用不同时间？", "choice", mode,
                    "法定休息日含周末并考虑调休；仅法定节假日不包含普通周末。", OverrideDayOptions);
            if (clocks.Count == 0 || edit)
                Add($"override{r}times", $"第 {r + 1} 组：新的提醒时刻", "time_list", clocks.Count > 0 ? string.Join(", ", clocks.Select(t => t.ToString("HH:mm"))) : rule.TimesText,
                    "24 小时制，可用逗号分隔多个时刻；其他日期保持原规则。",
                    error: !string.IsNullOrWhiteSpace(rule.TimesText) && clocks.Count == 0 ? "请填写明确时刻，例如 02:00。" : null);
            if (mode is not null && clocks.Count > 0)
            {
                overrides.Add(new(mode, clocks));
                facts.Add(OverrideDayOptions.First(o => o.Value == mode).Label + "：" + string.Join("、", clocks.Select(t => t.ToString("HH:mm"))));
            }
        }
        var window = ParseRange(s.WindowText, turn);
        var pauses = new List<ReminderTimeWindow>();
        var hasExclusion = s.ExclusionText is not (null or "" or "none" or "不排除");
        if (hasExclusion)
        {
            foreach (var part in s.ExclusionText!.Split(';', '；'))
            {
                var p = ParseRange(part, turn);
                if (p is not null) pauses.Add(p);
            }
            facts.Add("排除：" + (pauses.Count == 0 ? s.ExclusionText : string.Join("；", pauses.Select(Format))));
        }
        var first = s.FirstTrigger is "start" or "after_interval" ? s.FirstTrigger : null;
        var rhythm = !hasExclusion ? "skip" : s.Rhythm is "skip" or "restart" ? s.Rhythm : null;
        IReadOnlyList<TimeOnly> times = [];
        if (fixedTimes)
        {
            times = ParseTimes(s.TimesText!, turn);
            if (times.Count == 0 || edit) Add("times", "每天在哪些时刻提醒？", "time_list",
                times.Count == 0 ? s.TimesText : string.Join(", ", times.Select(t => t.ToString("HH:mm"))),
                "可填写多个 24 小时时刻，以逗号分隔，例如 09:00, 14:00, 18:00。");
            if (times.Count > 0) facts.Add((overrides.Count > 0 ? "其余执行日保持：" : "每天时刻：") + string.Join("、", times.Select(t => t.ToString("HH:mm"))));
            if (hasExclusion) return new(draft, null, fields, "固定时刻计划包含排除条件", facts, [],
                "固定时刻与排除时段需要统一安排，请在修改需求中明确保留哪些时刻。");
        }
        else
        {
            if (window is null || window.ValidationError is not null || edit)
                Add("window", "每天在哪个时间段内提醒？", "time_range", window is null ? s.WindowText : Format(window),
                    "24 小时制；00:00 表示当天午夜结束，结束时刻不再触发提醒。",
                    [new("09:00–18:00", "09:00-18:00"), new("08:30–17:30", "08:30-17:30"), new("18:00–00:00（到午夜）", "18:00-00:00")],
                    error: string.IsNullOrWhiteSpace(s.WindowText) ? null :
                        window is null ? ReminderDailySchedule.WindowInputError(s.WindowText) : window.ValidationError);
            if (window is not null) facts.Add("有效时段：" + Format(window));
            if (hasExclusion && (pauses.Count != s.ExclusionText!.Split(';', '；').Length || edit))
                Add("exclusion", "排除哪个休息时段？", "time_range", pauses.Count == 1 ? Format(pauses[0]) : null,
                    "休息开始时停止提醒，结束时恢复。填写实际时间，不会默认采用建议。",
                    [new("12:00–13:00", "12:00-13:00"), new("12:00–13:30", "12:00-13:30")]);
            if (edit && !hasExclusion)
                Add("exclusion", "需要排除的时段（可留空）", "time_range", help: "例如午休；留空表示不排除。", required: false);
            if (first is null || edit)
                Add("first", "第一次什么时候提醒？", "choice", first, "这会改变当天全部提醒时刻。",
                    [new("工作开始时", "start"), new("先工作满一个间隔", "after_interval")]);
            if (hasExclusion && (rhythm is null || edit))
                Add("rhythm", "休息后如何继续计时？", "choice", rhythm,
                    "继续原节奏：跳过落在休息内的提醒。重新计时：休息结束后等待一个完整间隔。",
                    [new("继续原来的节奏", "skip"), new("休息结束重新计时", "restart")]);
            if (first is not null) facts.Add(first == "start" ? "首次：时段开始时" : "首次：开始后满一个间隔");
            if (hasExclusion && rhythm is not null) facts.Add(rhythm == "skip" ? "休息后：继续原节奏" : "休息后：重新计时");
        }
        var start = ParseDate(s.StartText, turn) ?? DateOnly.FromDateTime(turn.ReferenceTime.DateTime);
        var until = ParseDate(s.UntilText, turn);
        if (edit || !string.IsNullOrEmpty(s.StartText) && ParseDate(s.StartText, turn) is null)
            Add("start", "从哪天开始？", "date", start.ToString("yyyy-MM-dd"));
        if (edit || !string.IsNullOrEmpty(s.UntilText) && until is null || until < start)
            Add("until", "在哪天结束？（不限制可留空）", "date", until?.ToString("yyyy-MM-dd"),
                "结束日期当天仍可执行。", required: false);
        facts.Add($"生效：{start:yyyy-MM-dd}" + (until is null ? "起，持续执行" : $" 至 {until:yyyy-MM-dd}"));
        var summary = string.Join("\n", facts);
        if (fields.Count > 0) return new(draft, null, fields, summary, facts, []);
        try
        {
            if (!fixedTimes) times = ReminderDailySchedule.GenerateTimes(window!, interval!.Value, pauses, first!, rhythm!);
            // Hidden custom-day answers must not leak into daily or statutory rules.
            var dayList = days == "weekdays" ? new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
                : days == "custom" ? selectedDays.ToArray() : [];
            var schedule = new ReminderDailySchedule(times, days!, dayList, start, until, summary,
                fixedTimes ? null : interval, window, pauses, first, rhythm, overrides.Count == 0 ? null : overrides);
            schedule.Validate();
            var firstDay = DateOnly.FromDateTime(turn.ReferenceTime.DateTime);
            if (overrides.Any(rule => !Enumerable.Range(0, 551).Select(firstDay.AddDays).Any(day => schedule.Matches(day) && rule.Matches(day))))
                throw new ArgumentException("指定日期类别与原计划的执行日或有效日期不相交，或当前日历没有可预览的日期。请调整日期类别或原执行日。");
            var next = schedule.Next(turn.ReferenceTime.DateTime);
            if (next.Count == 0) throw new ArgumentException("所选日期范围内没有未来提醒，请调整开始或结束日期。");
            var rule = new AssistantRecurrenceRuleV1(days is "weekdays" or "custom" ? AssistantRecurrenceFrequencyV1.Weekly : AssistantRecurrenceFrequencyV1.Daily,
                1, dayList, null, until is null ? null : new(AssistantRecurrenceEndKindV1.Until, null, until));
            var wall = new AssistantTimeExpressionV1(DateOnly.FromDateTime(next[0]), TimeOnly.FromDateTime(next[0]), null,
                turn.TimeZone, next[0].ToString("yyyy-MM-dd HH:mm"));
            var command = updating
                ? new AssistantCommandEnvelope(1, AssistantCommandName.UpdateTodo,
                    new UpdateTodoArgumentsV1(new(target!.Title, target.Kind, null, target.CandidateRef),
                        new(draft.Title, draft.Notes, null, null, null,
                            (draft.ClearFields ?? []).Select(f => Enum.Parse<UpdateTodoClearFieldV1>(f, true)).ToArray(), schedule)), [], [])
                : new AssistantCommandEnvelope(1, AssistantCommandName.CreateRecurringTask,
                    new CreateRecurringTaskArgumentsV1(draft.Title, draft.Notes, AssistantItemKindV1.Reminder, wall, rule, null, schedule), [], []);
            return new(draft, command, fields, summary + (!fixedTimes ? "\n每天时刻：" + string.Join("、", times.Select(t => t.ToString("HH:mm"))) : ""),
                facts, next.Select(t => t.ToString("MM-dd ddd HH:mm", CultureInfo.GetCultureInfo("zh-CN"))).ToArray());
        }
        catch (ArgumentException e)
        {
            var editable = Compile(draft, turn, index, true);
            return editable with { Summary = summary + "\n需要调整：" + e.Message,
                Fields = editable.Fields.Select((field, i) => i == 0 ? field with { Error = e.Message } : field).ToArray() };
        }
    }

    public static AssistantTaskDraft Set(AssistantTaskDraft task, string key, string value)
    {
        var s = task.Schedule ?? new();
        var overrideKey = Regex.Match(key, @"^override(?<index>\d+)(?<field>days|times)$");
        if (overrideKey.Success)
        {
            var index = int.Parse(overrideKey.Groups["index"].Value);
            var rules = (s.DayOverrides ?? []).ToList();
            while (rules.Count <= index) rules.Add(new());
            rules[index] = overrideKey.Groups["field"].Value == "days" ? rules[index] with { DaysText = value } : rules[index] with { TimesText = value };
            return task with { Schedule = s with { DayOverrides = rules } };
        }
        return task with { Schedule = key switch
        {
            "interval" => s with { IntervalText = value }, "window" => s with { WindowText = value },
            "exclusion" => s with { ExclusionText = value.Length == 0 ? "none" : value },
            "days" => s with { DaysText = value }, "weekdays" => s with { WeekdaysText = value },
            "times" => s with { TimesText = value }, "first" => s with { FirstTrigger = value },
            "rhythm" => s with { Rhythm = value }, "start" => s with { StartText = value },
            "until" => s with { UntilText = value }, _ => throw new ArgumentException("未知计划字段。")
        }};
    }

    public static int? Minutes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Replace(" ", "").Replace("两", "二");
        var m = Regex.Match(s, @"^(?<n>\d+(?:\.\d+)?|半|一|二|三|四|五|六|十|十五|三十)(?<u>小时|分钟|分)?$");
        if (!m.Success) return null;
        var numbers = new Dictionary<string, double> { ["半"] = .5, ["一"] = 1, ["二"] = 2, ["三"] = 3, ["四"] = 4, ["五"] = 5, ["六"] = 6, ["十"] = 10, ["十五"] = 15, ["三十"] = 30 };
        var amount = numbers.TryGetValue(m.Groups["n"].Value, out var n) ? n : double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        var minutes = amount * (m.Groups["u"].Value == "小时" ? 60 : 1);
        return minutes == Math.Truncate(minutes) && minutes is >= 5 and <= 1440 ? (int)minutes : null;
    }
    static string? DayMode(string? text) => text switch
    {
        "daily" or "每天" or "每日" => "daily", "weekdays" or "周一至周五" => "weekdays",
        "official" or "法定工作日" => "official", "custom" => "custom", _ => null
    };
    static string? OverrideMode(string? text) => text switch
    {
        "official" or "法定工作日" => "official", "weekdays" or "周一至周五" => "weekdays",
        "weekends" or "周末" or "周六周日" => "weekends", "rest" or "法定休息日" => "rest",
        "holidays" or "法定节假日" or "仅法定节假日" => "holidays", _ => null
    };

    internal static IReadOnlyList<AssistantDayOverrideDraft>? MergeOverrides(
        IReadOnlyList<AssistantDayOverrideDraft>? previous, IReadOnlyList<AssistantDayOverrideDraft>? incoming)
    {
        if (incoming is null) return previous;
        if (incoming.Count == 0) return [];
        var result = incoming.ToList();
        foreach (var rule in previous ?? [])
            if (result.All(r => (OverrideMode(r.DaysText) ?? r.DaysText) != (OverrideMode(rule.DaysText) ?? rule.DaysText))) result.Add(rule);
        return result;
    }
    static IReadOnlyList<DayOfWeek> ReadWeekdays(string? text) => (text ?? "").Split(',').Where(s => int.TryParse(s, out var d) && d is >= 0 and <= 6).Select(s => (DayOfWeek)int.Parse(s)).Distinct().ToArray();
    static DateOnly? ParseDate(string? text, AssistantDraftTurn turn) => text switch
    {
        "今天" => DateOnly.FromDateTime(turn.ReferenceTime.DateTime), "明天" => DateOnly.FromDateTime(turn.ReferenceTime.DateTime).AddDays(1),
        _ => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null
    };
    static TimeOnly? Clock(string text, AssistantDraftTurn turn) =>
        AssistantDraftCompiler.ParseTime(text.Replace("：", ":"), turn.ReferenceTime, turn.TimeZone,
            new(AssistantRecurrenceFrequencyV1.Daily, 1, [], null, null))?.LocalTime;
    static ReminderTimeWindow? ParseRange(string? text, AssistantDraftTurn turn)
    {
        if (ReminderDailySchedule.ParseWindow(text) is { } exact) return exact;
        var match = RangePattern.Match(text ?? "");
        return match.Success && Clock(match.Groups["a"].Value, turn) is { } a && Clock(match.Groups["b"].Value, turn) is { } b ? new(a, b) : null;
    }
    static IReadOnlyList<TimeOnly> ParseTimes(string text, AssistantDraftTurn turn)
    {
        var values = ClockPattern.Matches(text).Select(m => Clock(m.Value, turn)).ToArray();
        return values.Length > 0 && values.All(v => v is not null) ? values.Select(v => v!.Value).Distinct().OrderBy(v => v).ToArray() : [];
    }
    static string Format(ReminderTimeWindow w) => $"{w.Start:HH:mm}-{w.End:HH:mm}";
    public static readonly IReadOnlyList<AssistantInputOption> DayOptions = [new("每天", "daily"), new("周一至周五", "weekdays"), new("法定工作日（含调休）", "official"), new("自选星期", "custom")];
    public static readonly IReadOnlyList<AssistantInputOption> OverrideDayOptions = [new("法定休息日（含周末，考虑调休）", "rest"), new("仅法定节假日", "holidays"), new("法定工作日（含调休）", "official"), new("周一至周五", "weekdays"), new("周六、周日", "weekends")];
    public static readonly IReadOnlyList<AssistantInputOption> WeekdayOptions = Enumerable.Range(1, 7).Select(i => new AssistantInputOption("周" + "一二三四五六日"[i - 1], (i % 7).ToString())).ToArray();
    public static readonly IReadOnlyList<AssistantInputOption> DurationOptions = [new("30 分钟", "30"), new("1 小时", "60"), new("2 小时", "120"), new("自定义", "")];
}
