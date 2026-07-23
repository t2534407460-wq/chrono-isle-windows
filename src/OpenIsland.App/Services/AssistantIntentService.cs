using System.Globalization;
using System.IO;
using System.Text.Json;

namespace OpenIsland.App.Services;

public sealed class AssistantIntentService(IChatCompletionClient chat)
{
    const string SystemPrompt = """
        You are Island's local creation intent parser. Return one JSON object only: no Markdown, prose, or code fence.
        You can only identify creation intents and never answer questions or access the database.
        Allowed intent values: create_todo, create_event, create_recurring_reminder.
        create_todo fields: title, notes, dueDateTime, reminderDateTime, reminderRequested.
        create_event fields: title, notes, startDateTime, endDateTime, reminderDateTime. Events require start and end; put a missing end in missingFields.
        create_recurring_reminder fields: title, notes, reminderTime, recurrence, weekdays. reminderTime is local HH:mm; recurrence is daily, weekdays, or weekly; weekly requires monday through sunday weekday values.
        Dates must be ISO 8601 or null. Fixed JSON fields: intent,title,notes,dueDateTime,reminderDateTime,reminderRequested,startDateTime,endDateTime,reminderTime,recurrence,weekdays,missingFields.
        """;

    public async Task<IntentAnalysis> AnalyzeAsync(ProviderSettings provider, IEnumerable<ChatMessage> history, string input, AssistantAction? activeDraft)
    {
        var messages = new List<ModelMessage>
        {
            new("system", SystemPrompt),
            new("system", $"Current local time: {DateTime.Now:O}; time zone: {TimeZoneInfo.Local.Id}.")
        };
        if (activeDraft is not null)
            messages.Add(new("system", $"用户正在补充这个未完成草稿，请在新 JSON 中补齐并保留已有信息：{activeDraft.IntentJson}"));
        messages.AddRange(history.Select(x => new ModelMessage(x.Role, x.Content)));
        messages.Add(new("user", input));
        return Parse(await chat.Complete(provider, messages, jsonObject: true));
    }

    public static IntentAnalysis Parse(string response)
    {
        var json = ExtractJson(response);
        if (json is null) { LogJsonFailure("no-valid-object", response, null, null); return new(null, response, "模型未返回 JSON，未创建任何事项。", null); }

        IntentPayload? payload;
        try { payload = JsonSerializer.Deserialize<IntentPayload>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException exception) { LogJsonFailure("deserialize", response, json, exception); return new(null, json, "模型返回的 JSON 无法解析，未创建任何事项。", null); }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Intent))
            return new(null, json, "模型没有返回有效意图，未创建任何事项。", null);
        if (!TryDate(payload.DueDateTime, out var dueAt) || !TryDate(payload.ReminderDateTime, out var reminderAt) ||
            !TryDate(payload.StartDateTime, out var startsAt) || !TryDate(payload.EndDateTime, out var endsAt))
            return new(null, json, "模型返回了无效时间，未创建任何事项。", null);
        if (!TryTime(payload.ReminderTime, out var reminderTime))
            return new(null, json, "模型返回了无效的周期提醒时间，未创建任何事项。", null);
        if (!TryRecurrence(payload.Recurrence, out var recurrence))
            return new(null, json, "模型返回了不支持的周期规则，未创建任何事项。", null);
        if (!TryWeekdays(payload.Weekdays, out var weekdays))
            return new(null, json, "模型返回了无效的星期，未创建任何事项。", null);

        var kind = payload.Intent.Trim().ToLowerInvariant() switch
        {
            "create_todo" => AssistantIntentKind.CreateTodo,
            "create_event" => AssistantIntentKind.CreateEvent,
            "create_recurring_reminder" => AssistantIntentKind.CreateRecurringReminder,
            _ => (AssistantIntentKind?)null
        };
        if (kind is null) return new(null, json, "模型返回了不受支持的意图，未创建任何事项。", null);

        var intent = new AssistantIntent(
            kind.Value,
            payload.Title?.Trim(),
            payload.Notes?.Trim(),
            dueAt,
            reminderAt,
            reminderTime,
            recurrence,
            weekdays,
            startsAt,
            endsAt,
            payload.ReminderRequested,
            payload.Reply?.Trim(),
            (payload.MissingFields ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        return Validate(intent, json);
    }

    static IntentAnalysis Validate(AssistantIntent intent, string rawJson)
    {
        if (intent.Kind is AssistantIntentKind.CreateTodo or AssistantIntentKind.CreateEvent or AssistantIntentKind.CreateRecurringReminder &&
            string.IsNullOrWhiteSpace(intent.Title))
            return Clarify(intent, rawJson, "请补充事项标题。", "title");

        if (intent.Kind == AssistantIntentKind.CreateTodo && intent.ReminderRequested && intent.ReminderAt is null)
            return Clarify(intent, rawJson, "想在什么时间提醒你呢？", "reminderDateTime");
        if (intent.Kind == AssistantIntentKind.CreateEvent && intent.StartsAt is null)
            return Clarify(intent, rawJson, "请补充日程的开始时间。", "startDateTime");
        if (intent.Kind == AssistantIntentKind.CreateEvent && intent.EndsAt is null)
            return Clarify(intent, rawJson, "请补充日程的结束时间。", "endDateTime");
        if (intent.Kind == AssistantIntentKind.CreateEvent && intent.EndsAt <= intent.StartsAt)
            return new(null, rawJson, "日程结束时间必须晚于开始时间，未创建任何事项。", null);

        if (intent.Kind == AssistantIntentKind.CreateRecurringReminder && intent.ReminderTime is null)
            return Clarify(intent, rawJson, "想在每天的什么时间提醒你呢？", "reminderTime");
        if (intent.Kind == AssistantIntentKind.CreateRecurringReminder && intent.Recurrence is null)
            return Clarify(intent, rawJson, "请补充提醒周期：每天、工作日或每周。", "recurrence");
        if (intent.Kind == AssistantIntentKind.CreateRecurringReminder && intent.Recurrence == RecurrenceKind.Weekly && intent.Weekdays.Count == 0)
            return Clarify(intent, rawJson, "每周提醒需要指定星期几。", "weekdays");
        return new(intent, rawJson, null, null);
    }

    static IntentAnalysis Clarify(AssistantIntent intent, string rawJson, string message, string field)
    {
        var missing = intent.MissingFields.Append(field).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new(intent with { MissingFields = missing }, rawJson, null, message);
    }

    static bool TryDate(string? value, out DateTime? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)) return false;
        date = parsed.LocalDateTime;
        return true;
    }

    static bool TryTime(string? value, out TimeOnly? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!TimeOnly.TryParseExact(value, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        time = parsed;
        return true;
    }

    static bool TryRecurrence(string? value, out RecurrenceKind? recurrence)
    {
        recurrence = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        recurrence = value.Trim().ToLowerInvariant() switch
        {
            "daily" => RecurrenceKind.Daily,
            "weekdays" => RecurrenceKind.Weekdays,
            "weekly" => RecurrenceKind.Weekly,
            _ => null
        };
        return recurrence is not null;
    }

    static bool TryWeekdays(IEnumerable<string>? values, out IReadOnlyList<DayOfWeek> weekdays)
    {
        var mapping = new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
        {
            ["monday"] = DayOfWeek.Monday, ["tuesday"] = DayOfWeek.Tuesday, ["wednesday"] = DayOfWeek.Wednesday,
            ["thursday"] = DayOfWeek.Thursday, ["friday"] = DayOfWeek.Friday, ["saturday"] = DayOfWeek.Saturday,
            ["sunday"] = DayOfWeek.Sunday
        };
        var parsed = new List<DayOfWeek>();
        foreach (var value in values ?? [])
        {
            if (!mapping.TryGetValue(value.Trim(), out var day))
            {
                weekdays = [];
                return false;
            }
            if (!parsed.Contains(day)) parsed.Add(day);
        }
        weekdays = parsed;
        return true;
    }

    static void LogJsonFailure(string stage, string response, string? candidate, JsonException? exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenIsland");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "intent-json.log");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(response)));
            var entry = $"[{DateTime.Now:O}] stage={stage}{Environment.NewLine}" +
                $"exceptionType={exception?.GetType().Name}{Environment.NewLine}" +
                $"rawLength={response.Length}{Environment.NewLine}candidateLength={candidate?.Length ?? 0}{Environment.NewLine}" +
                $"rawSha256={hash}{Environment.NewLine}{Environment.NewLine}";
            if (File.Exists(path) && new FileInfo(path).Length >= 256 * 1024)
                File.WriteAllText(path, entry);
            else
                File.AppendAllText(path, entry);
        }
        catch { }
    }
    static string? ExtractJson(string response)
    {
        var candidates = new List<string>();
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;

        for (var index = 0; index < response.Length; index++)
        {
            var current = response[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == '"') inString = false;
                continue;
            }
            if (current == '"') { inString = true; continue; }
            if (current == '{')
            {
                if (depth++ == 0) start = index;
                continue;
            }
            if (current == '}' && depth > 0 && --depth == 0 && start >= 0)
                candidates.Add(response[start..(index + 1)]);
        }

        foreach (var candidate in candidates.AsEnumerable().Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind == JsonValueKind.Object) return candidate;
            }
            catch (JsonException) { }
        }
        return null;
    }
    sealed class IntentPayload
    {
        public string? Intent { get; init; }
        public string? Title { get; init; }
        public string? Notes { get; init; }
        public string? DueDateTime { get; init; }
        public string? ReminderDateTime { get; init; }
        public bool ReminderRequested { get; init; }
        public string? StartDateTime { get; init; }
        public string? EndDateTime { get; init; }
        public string? ReminderTime { get; init; }
        public string? Recurrence { get; init; }
        public List<string>? Weekdays { get; init; }
        public string? Reply { get; init; }
        public List<string>? MissingFields { get; init; }
    }
}