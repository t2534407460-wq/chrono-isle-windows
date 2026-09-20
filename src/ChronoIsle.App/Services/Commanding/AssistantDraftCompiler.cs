using System.Globalization;
using System.Text.RegularExpressions;

namespace ChronoIsle.App.Services.Commanding;

public sealed record AssistantDraftCompilation(IReadOnlyList<AssistantCommandEnvelope> Commands,
    IReadOnlyList<AssistantInputField> Fields, string Summary,
    IReadOnlyList<string>? Facts = null, IReadOnlyList<string>? Preview = null, string? Blocked = null);

public static class AssistantDraftCompiler
{
    public static AssistantDraftCompilation Compile(AssistantDraftTurn turn, bool edit = false)
    {
        var fields = new List<AssistantInputField>();
        var commands = new List<AssistantCommandEnvelope>();
        var summaries = new List<string>();
        var facts = new List<string>();
        var preview = new List<string>();
        for (var i = 0; i < turn.Tasks.Count; i++)
        {
            var draft = Normalize(turn.Tasks[i], turn);
            if (draft.UnhandledConstraints is { Count: > 0 })
                return new([], [], draft.Evidence, Blocked: "这些条件尚不能可靠执行：" + string.Join("、", draft.UnhandledConstraints) + "。请修改需求后重新规划。");
            if (draft.Operation is "list_items" or "summarize_period") continue;
            if (draft.Schedule is not null)
            {
                var planned = AssistantScenarioPlanner.Compile(draft, turn, i, edit);
                if (planned.Blocked is not null) return new([], [], planned.Summary, planned.Facts, [], planned.Blocked);
                fields.AddRange(planned.Fields); facts.AddRange(planned.Facts); preview.AddRange(planned.Preview);
                summaries.Add(planned.Summary);
                if (planned.Command is not null) commands.Add(planned.Command);
                continue;
            }
            var prefix = turn.Tasks.Count == 1 ? "" : $"第 {i + 1} 项 · ";
            void Add(string key, string label, string kind, string? value = null, IReadOnlyList<AssistantInputOption>? options = null)
            {
                if (fields.All(f => f.Key != $"{i}.{key}"))
                    fields.Add(new($"{i}.{key}", prefix + label, kind, options ?? [], value));
            }
            var creates = draft.Operation.StartsWith("create_", StringComparison.Ordinal);
            if ((creates || draft.Operation == "decompose_goal") && (string.IsNullOrWhiteSpace(draft.Title) || edit))
                Add("title", "事项名称", "text", draft.Title);
            AssistantRecurrenceRuleV1? repeat = null;
            var repeating = !string.IsNullOrWhiteSpace(draft.RepeatText) && draft.RepeatText != "不重复";
            if (repeating && !TryRecurrence(draft.RepeatText!, out repeat) || draft.Operation == "create_recurring_task" && !repeating)
                Add("repeatText", "选择重复方式", "choice", options: RepeatOptions);
            if (edit && creates) Add("repeatText", "重复方式", "choice", draft.RepeatText ?? "不重复", RepeatOptions);

            AssistantTimeExpressionV1? Time(string key, string? text, bool required, DateTimeOffset? reference = null)
            {
                var value = ParseTime(text, reference ?? turn.ReferenceTime, turn.TimeZone, repeat);
                if (value is null && (required || !string.IsNullOrWhiteSpace(text)) || edit && (required || text is not null))
                    Add(key, key switch
                    {
                        "endText" => "结束日期与时间", "dueText" => "截止日期与时间",
                        "reminderText" => "提醒日期与时间", _ => repeating ? "重复事项的提醒时间" : "日期与时间"
                    }, repeating && key == "timeText" ? "time" : "datetime",
                        value is null ? null : repeating && key == "timeText" ? value.LocalTime?.ToString("HH:mm") : $"{value.LocalDate:yyyy-MM-dd} {value.LocalTime:HH:mm}");
                return value;
            }
            if (repeat?.Frequency == AssistantRecurrenceFrequencyV1.Monthly)
                Add("repeatText", "当前提醒调度支持每日或每周，请选择支持的规则，或取消此任务", "choice", options: RepeatOptions);
            if (repeating && draft.Operation == "create_long_term_item")
                Add("repeatText", "长期事项暂不支持重复，请选择不重复或取消", "choice", options: [new("不重复", "不重复")]);
            if (repeating && (draft.Operation == "create_todo" || draft.ItemKind is "todo" or "event" or "long_term"))
                Add("repeatText", "周期待办暂未接入提醒调度，可改为单次事项或取消", "choice", options: [new("不重复", "不重复")]);
            var requiresTime = draft.Operation is "create_reminder" or "create_event" or "create_recurring_task" or "reschedule_item";
            var time = Time("timeText", draft.TimeText, requiresTime || repeating);
            // A time-only event end belongs to the explicitly resolved start date.
            var endReference = time?.LocalDate is { } eventDate && draft.EndText is { } endText &&
                Regex.IsMatch(endText.Replace(" ", ""), @"^(?:凌晨|早上|早晨|上午|中午|下午|晚上|晚间)?[0-9零〇一二两三四五六七八九十]+(?:点|时|:|：)")
                ? new DateTimeOffset(eventDate.ToDateTime(TimeOnly.MinValue), turn.ReferenceTime.Offset) : (DateTimeOffset?)null;
            AssistantTimeExpressionV1? end;
            if (draft.Operation == "create_event" && string.IsNullOrWhiteSpace(draft.EndText))
            {
                var duration = AssistantScenarioPlanner.Minutes(draft.DurationText);
                if (duration is null || edit)
                    fields.Add(new($"{i}.durationText", "这项日程持续多久？", "duration", AssistantScenarioPlanner.DurationOptions,
                        duration?.ToString(), "选择建议或填写分钟数；也可在对话中指定结束时间。"));
                var finish = time?.LocalDate is { } date && time.LocalTime is { } clock && duration is { } minutes
                    ? date.ToDateTime(clock).AddMinutes(minutes) : (DateTime?)null;
                end = finish is { } value ? new(DateOnly.FromDateTime(value), TimeOnly.FromDateTime(value), null, turn.TimeZone, value.ToString("yyyy-MM-dd HH:mm")) : null;
            }
            else end = Time("endText", draft.EndText, false, endReference);
            var due = Time("dueText", draft.DueText, false);
            var remind = Time("reminderText", draft.ReminderText, false);
            if (time?.LocalDate is { } startDate && end?.LocalDate is { } endDate &&
                startDate.ToDateTime(time.LocalTime!.Value) >= endDate.ToDateTime(end.LocalTime!.Value))
                Add("endText", "结束时间需要晚于开始时间", "datetime");

            var target = turn.Bindings.TryGetValue(i, out var bound)
                ? new AssistantTargetSelectorV1(bound.Title, bound.Kind, null, bound.CandidateRef)
                : new AssistantTargetSelectorV1(draft.Target, null, null);
            var priority = Enum.TryParse<AssistantPriorityV1>(draft.Priority, true, out var parsedPriority)
                ? parsedPriority : (AssistantPriorityV1?)null;
            var kind = draft.ItemKind switch
            {
                "todo" => AssistantItemKindV1.Todo, "event" => AssistantItemKindV1.Event,
                "long_term" => AssistantItemKindV1.LongTerm, _ => AssistantItemKindV1.Reminder
            };
            IAssistantCommandArgumentsV1 args;
            AssistantCommandName command;
            switch (draft.Operation)
            {
                case "create_reminder" when repeating:
                case "create_todo" when repeating:
                case "create_recurring_task":
                    command = AssistantCommandName.CreateRecurringTask;
                    args = new CreateRecurringTaskArgumentsV1(draft.Title, draft.Notes,
                        draft.Operation == "create_todo" ? AssistantItemKindV1.Todo : kind, time, repeat, priority);
                    if (draft.DueText is not null || draft.ReminderText is not null || draft.EndText is not null)
                        Add("repeatText", "此周期任务包含额外时间，请改为单次事项或重新明确重复规则", "choice", options: [new("改为单次", "不重复")]);
                    break;
                case "create_reminder":
                    command = AssistantCommandName.CreateReminder;
                    args = new CreateReminderArgumentsV1(draft.Title, draft.Notes, time, due, null, priority);
                    if (draft.ReminderText is not null)
                        Add("timeText", "此提醒包含两个不同时间，请选择最终提醒时间", "datetime");
                    break;
                case "create_todo":
                    command = AssistantCommandName.CreateTodo;
                    args = new CreateTodoArgumentsV1(draft.Title, draft.Notes, due ?? time, remind, null, priority); break;
                case "create_event":
                    command = AssistantCommandName.CreateEvent;
                    args = new CreateEventArgumentsV1(draft.Title, draft.Notes, time, end, remind);
                    if (repeating) Add("repeatText", "周期日程暂不支持，请选择单次或取消", "choice", options: [new("单次日程", "不重复")]);
                    break;
                case "create_long_term_item":
                    command = AssistantCommandName.CreateLongTermItem;
                    args = new CreateLongTermItemArgumentsV1(draft.Title, draft.Notes, due ?? time, remind, priority); break;
                case "update_todo":
                    command = AssistantCommandName.UpdateTodo;
                    var clears = (draft.ClearFields ?? []).Select(s => Enum.Parse<UpdateTodoClearFieldV1>(s, true)).ToArray();
                    var changes = new UpdateTodoChangesV1(draft.Title, draft.Notes, due, remind ?? time, priority, clears);
                    args = new UpdateTodoArgumentsV1(target, changes);
                    if (!changes.HasAnyChange) Add("title", "新的事项名称（也可以在聊天中描述其他修改）", "text");
                    break;
                case "complete_todo": command = AssistantCommandName.CompleteTodo; args = new CompleteTodoArgumentsV1(target); break;
                case "delete_todo": command = AssistantCommandName.DeleteTodo; args = new DeleteTodoArgumentsV1(target); break;
                case "reschedule_item": command = AssistantCommandName.RescheduleItem; args = new RescheduleItemArgumentsV1(target, time, remind); break;
                case "decompose_goal":
                    command = AssistantCommandName.DecomposeGoal;
                    args = new DecomposeGoalArgumentsV1(draft.Title, [], 10,
                        draft.ProposedTasks?.Select(t => new DecomposedTaskArgumentsV1(t, null, null, null)).ToArray());
                    break;
                case "list_items":
                case "summarize_period":
                    summaries.Add(draft.Evidence);
                    continue;
                default: throw new InvalidOperationException("未知任务类型。");
            }
            commands.Add(new(1, command, args, [], []));
            var label = draft.Operation switch
            {
                "delete_todo" => "删除", "complete_todo" => "完成", "update_todo" => "修改",
                "reschedule_item" => "调整时间", "decompose_goal" => "拆解目标", _ => "创建"
            };
            var timeLabel = time is null ? draft.TimeText : repeating ? time.LocalTime?.ToString("HH:mm") : $"{time.LocalDate:yyyy-MM-dd} {time.LocalTime:HH:mm}";
            summaries.Add($"{label}：{draft.Title ?? draft.Target ?? "待填写"}" +
                (string.IsNullOrWhiteSpace(timeLabel) ? "" : $" · {timeLabel}") + (repeating ? $" · {draft.RepeatText}" : "") +
                (end is null ? "" : $" 至 {end.LocalDate:yyyy-MM-dd} {end.LocalTime:HH:mm}"));
        }
        return new(commands, fields, string.Join("\n", summaries), facts, preview);
    }

    static AssistantTaskDraft Normalize(AssistantTaskDraft draft, AssistantDraftTurn turn)
    {
        draft = AssistantScenarioPlanner.Enrich(draft, turn.Tasks.Count == 1 ? turn.SourceText : draft.Evidence);
        if (draft.Operation == "create_recurring_task" && draft.RepeatText == "不重复")
            draft = draft with { Operation = draft.ItemKind switch
            { "todo" => "create_todo", "event" => "create_event", "long_term" => "create_long_term_item", _ => "create_reminder" } };
        if (draft.Operation == "create_reminder" ||
            draft.Operation == "create_recurring_task" && draft.ItemKind is null or "reminder")
        {
            if (string.IsNullOrWhiteSpace(draft.TimeText) && !string.IsNullOrWhiteSpace(draft.ReminderText))
                return draft with { TimeText = draft.ReminderText, ReminderText = null };
            TryRecurrence(draft.RepeatText ?? "", out var repeat);
            var primary = ParseTime(draft.TimeText, turn.ReferenceTime, turn.TimeZone, repeat);
            var duplicate = ParseTime(draft.ReminderText, turn.ReferenceTime, turn.TimeZone, repeat);
            if (draft.TimeText == draft.ReminderText || primary is not null && duplicate is not null &&
                primary.LocalDate == duplicate.LocalDate && primary.LocalTime == duplicate.LocalTime)
                draft = draft with { ReminderText = null };
        }
        return draft;
    }

    public static readonly IReadOnlyList<AssistantInputOption> RepeatOptions =
        [new("不重复", "不重复"), new("每天", "每天"), new("周一至周五", "工作日"),
         new("每周一", "每周一"), new("每周二", "每周二"), new("每周三", "每周三"), new("每周四", "每周四"), new("每周五", "每周五"), new("每周六", "每周六"), new("每周日", "每周日")];

    public static bool TryRecurrence(string text, out AssistantRecurrenceRuleV1? rule)
    {
        rule = null;
        var value = text.Replace(" ", "").Replace("的", "");
        if (value is "每天" or "每日") rule = new(AssistantRecurrenceFrequencyV1.Daily, 1, [], null, null);
        else if (value is "工作日" or "周一至周五")
            rule = new(AssistantRecurrenceFrequencyV1.Weekly, 1,
                [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], null, null);
        else if (Regex.IsMatch(value, @"^每周[一二三四五六日天](?:[、和][一二三四五六日天])*$"))
            rule = new(AssistantRecurrenceFrequencyV1.Weekly, 1,
                value[2..].Where(c => "一二三四五六日天".Contains(c)).Select(Weekday).Distinct().ToArray(), null, null);
        else
        {
            var match = Regex.Match(value, @"^每月(?<day>\d{1,2})[日号]$");
            if (match.Success && int.Parse(match.Groups["day"].Value) is var day && day is >= 1 and <= 31)
                rule = new(AssistantRecurrenceFrequencyV1.Monthly, 1, [], day, null);
        }
        return rule is not null;
    }

    public static AssistantTimeExpressionV1? ParseTime(string? text, DateTimeOffset reference, string zoneId, AssistantRecurrenceRuleV1? repeat = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim().Replace(" ", "").Replace("：", ":");
        var local = reference.DateTime;
        var relative = Regex.Match(value, @"^(?<n>\d+|[一二两三四五六七八九十半]+)(?<unit>分钟|小时|天)后$");
        if (relative.Success && repeat is null)
        {
            var amount = relative.Groups["n"].Value == "半" ? .5 : Number(relative.Groups["n"].Value);
            if (amount <= 0 || amount > 3650) return null;
            local = relative.Groups["unit"].Value switch
            {
                "分钟" => local.AddMinutes(amount), "小时" => local.AddHours(amount), _ => local.AddDays(amount)
            };
            return Expression(local, zoneId, text);
        }
        value = Regex.Replace(value, @"^(?:每天|每日|工作日|每周[一二三四五六日天](?:[、和][一二三四五六日天])*|每月\d{1,2}[日号])的?", "");
        var date = local.Date;
        var explicitDate = false;
        var dateMatch = Regex.Match(value, @"^(?<y>\d{4})[-/年](?<m>\d{1,2})[-/月](?<d>\d{1,2})日?");
        if (dateMatch.Success)
        {
            if (!DateTime.TryParseExact($"{dateMatch.Groups["y"].Value}-{int.Parse(dateMatch.Groups["m"].Value):00}-{int.Parse(dateMatch.Groups["d"].Value):00}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return null;
            value = value[dateMatch.Length..]; explicitDate = true;
        }
        else if (value.StartsWith("明早") || value.StartsWith("明晚"))
        {
            date = date.AddDays(1); explicitDate = true;
            value = (value.StartsWith("明早") ? "早上" : "晚上") + value[2..];
        }
        else
        {
            foreach (var (word, days) in new[] { ("今天", 0), ("明天", 1), ("后天", 2) })
                if (value.StartsWith(word)) { date = date.AddDays(days); value = value[word.Length..]; explicitDate = true; break; }
            var week = Regex.Match(value, @"^(?<next>下)?(?:周|星期)(?<day>[一二三四五六日天])");
            if (week.Success)
            {
                var offset = ((int)Weekday(week.Groups["day"].Value[0]) - (int)date.DayOfWeek + 7) % 7;
                if (week.Groups["next"].Success)
                    offset = 7 - ((int)date.DayOfWeek + 6) % 7 + ((int)Weekday(week.Groups["day"].Value[0]) + 6) % 7;
                date = date.AddDays(offset); value = value[week.Length..]; explicitDate = true;
            }
        }
        var clock = Regex.Match(value, @"^(?<period>凌晨|早上|早晨|上午|中午|下午|晚上|晚间)?(?<h>\d{1,2}|[零〇一二两三四五六七八九十]{1,3})(?:(?:点|时)(?<minute>半|一刻|三刻|(?:\d{1,2}|[零〇一二三四五六七八九十]{1,3})分?)?|:(?<digital>\d{2}))$");
        if (!clock.Success) return null;
        var hour = Number(clock.Groups["h"].Value);
        var period = clock.Groups["period"].Value;
        // Bare twelve-hour clocks need an AM/PM choice; HH:mm is explicitly 24-hour time.
        if (period.Length == 0 && !clock.Groups["digital"].Success && hour is >= 1 and <= 12) return null;
        if (period is "下午" or "晚上" or "晚间" && hour is >= 1 and < 12) hour += 12;
        if (period == "凌晨" && hour == 12) hour = 0;
        if (period == "中午" && hour is >= 1 and < 11) hour += 12;
        var minuteText = clock.Groups["digital"].Success ? clock.Groups["digital"].Value : clock.Groups["minute"].Value.TrimEnd('分');
        var minute = minuteText switch { "" => 0, "半" => 30, "一刻" => 15, "三刻" => 45, _ => Number(minuteText) };
        if (hour is < 0 or > 23 || minute is < 0 or > 59) return null;
        var resolved = date.AddHours(hour).AddMinutes(minute);
        if (repeat is not null)
        {
            if (!explicitDate && resolved <= local) resolved = resolved.AddDays(1);
            if (repeat.Frequency == AssistantRecurrenceFrequencyV1.Weekly)
                while (!repeat.Weekdays!.Contains(resolved.DayOfWeek)) resolved = resolved.AddDays(1);
            if (repeat.Frequency == AssistantRecurrenceFrequencyV1.Monthly)
            {
                var day = repeat.MonthDay!.Value;
                var month = new DateTime(resolved.Year, resolved.Month, 1);
                while (day > DateTime.DaysInMonth(month.Year, month.Month) || month.AddDays(day - 1).AddHours(hour).AddMinutes(minute) < resolved)
                    month = month.AddMonths(1);
                resolved = month.AddDays(day - 1).AddHours(hour).AddMinutes(minute);
            }
        }
        else if (!explicitDate && resolved <= local) resolved = resolved.AddDays(1);
        else if (resolved <= local) return null;
        return Expression(resolved, zoneId, text);
    }

    static AssistantTimeExpressionV1 Expression(DateTime value, string zone, string original) =>
        new(DateOnly.FromDateTime(value), TimeOnly.FromDateTime(value), null, zone, original);
    static DayOfWeek Weekday(char c) => c is '日' or '天' ? DayOfWeek.Sunday : (DayOfWeek)("一二三四五六".IndexOf(c) + 1);
    static int Number(string value)
    {
        if (int.TryParse(value, out var n)) return n;
        if (value == "两") return 2;
        if (value is "零" or "〇") return 0;
        var parts = value.Split('十');
        int Digit(string s) => s.Length == 1 ? "零一二三四五六七八九".IndexOf(s[0]) : -1;
        if (parts.Length == 1) return Digit(value);
        if (parts.Length != 2) return -1;
        var tens = parts[0].Length == 0 ? 1 : Digit(parts[0]);
        var ones = parts[1].Length == 0 ? 0 : Digit(parts[1]);
        return tens < 0 || ones < 0 ? -1 : tens * 10 + ones;
    }

    public static AssistantTaskDraft SetField(AssistantTaskDraft draft, string field, string value) => field switch
    {
        "durationText" => draft with { DurationText = value, EndText = null },
        "title" => draft with { Title = value }, "target" => draft with { Target = value },
        "timeText" => draft with { TimeText = value,
            ReminderText = draft.Operation == "create_reminder" ||
                draft.Operation == "create_recurring_task" && draft.ItemKind is null or "reminder" ? null : draft.ReminderText }, "endText" => draft with { EndText = value },
        "dueText" => draft with { DueText = value }, "reminderText" => draft with { ReminderText = value },
        "repeatText" => draft with { RepeatText = value }, _ => throw new ArgumentException("此字段不能编辑。")
    };
}
