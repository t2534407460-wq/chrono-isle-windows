using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChronoIsle.App.Services.Commanding;

public sealed class AssistantCommandContractException(
    string code,
    string path,
    string message,
    Exception? innerException = null) : FormatException(message, innerException)
{
    public string Code { get; } = code;
    public string Path { get; } = path;
}

public static class AssistantCommandEnvelopeJson
{
    static readonly JsonSerializerOptions Options = CreateOptions();

    public static AssistantCommandEnvelope Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw Error("invalid_json", "$", "Command JSON must not be empty.");
        RejectDuplicateProperties(json);

        EnvelopeWire? wire;
        try { wire = JsonSerializer.Deserialize<EnvelopeWire>(json, Options); }
        catch (JsonException exception)
        {
            throw Error("schema_rejected", exception.Path ?? "$", "Command JSON does not match the v1 envelope schema.", exception);
        }
        if (wire is null) throw Error("schema_rejected", "$", "Command envelope must be an object.");
        if (wire.Arguments.ValueKind != JsonValueKind.Object)
            throw Error("invalid_type", "$.arguments", "arguments must be an object.");
        if (wire.MissingFields is null)
            throw Error("invalid_type", "$.missingFields", "missingFields must be an array.");
        if (wire.AmbiguityReasons is null)
            throw Error("invalid_type", "$.ambiguityReasons", "ambiguityReasons must be an array.");

        var command = ParseCommand(wire.Command);
        var arguments = DeserializeArguments(command, wire.Arguments);
        var envelope = new AssistantCommandEnvelope(
            wire.SchemaVersion,
            command,
            arguments,
            ValidateExplanationList(wire.MissingFields, "$.missingFields"),
            ValidateExplanationList(wire.AmbiguityReasons, "$.ambiguityReasons"));
        AssistantCommandContractValidator.Validate(envelope);
        return envelope;
    }

    public static bool TryDeserialize(
        string json,
        out AssistantCommandEnvelope? envelope,
        out AssistantCommandContractException? error)
    {
        try
        {
            envelope = Deserialize(json);
            error = null;
            return true;
        }
        catch (AssistantCommandContractException exception)
        {
            envelope = null;
            error = exception;
            return false;
        }
    }

    public static string Serialize(AssistantCommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        AssistantCommandContractValidator.Validate(envelope);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", envelope.SchemaVersion);
            writer.WriteString("command", CommandName(envelope.Command));
            writer.WritePropertyName("arguments");
            JsonSerializer.Serialize(writer, envelope.Arguments, envelope.Arguments.GetType(), Options);
            writer.WritePropertyName("missingFields");
            JsonSerializer.Serialize(writer, envelope.MissingFields, Options);
            writer.WritePropertyName("ambiguityReasons");
            JsonSerializer.Serialize(writer, envelope.AmbiguityReasons, Options);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static IAssistantCommandArgumentsV1 DeserializeArguments(AssistantCommandName command, JsonElement element) => command switch
    {
        AssistantCommandName.CreateTodo => DeserializeArguments<CreateTodoArgumentsV1>(element),
        AssistantCommandName.CreateReminder => DeserializeArguments<CreateReminderArgumentsV1>(element),
        AssistantCommandName.CreateEvent => DeserializeArguments<CreateEventArgumentsV1>(element),
        AssistantCommandName.CreateLongTermItem => DeserializeArguments<CreateLongTermItemArgumentsV1>(element),
        AssistantCommandName.ListItems => DeserializeArguments<ListItemsArgumentsV1>(element),
        AssistantCommandName.UpdateTodo => DeserializeArguments<UpdateTodoArgumentsV1>(element),
        AssistantCommandName.CompleteTodo => DeserializeArguments<CompleteTodoArgumentsV1>(element),
        AssistantCommandName.DeleteTodo => DeserializeArguments<DeleteTodoArgumentsV1>(element),
        AssistantCommandName.CreateRecurringTask => DeserializeArguments<CreateRecurringTaskArgumentsV1>(element),
        AssistantCommandName.RescheduleItem => DeserializeArguments<RescheduleItemArgumentsV1>(element),
        AssistantCommandName.DecomposeGoal => DeserializeArguments<DecomposeGoalArgumentsV1>(element),
        AssistantCommandName.SummarizePeriod => DeserializeArguments<SummarizePeriodArgumentsV1>(element),
        _ => throw Error("unsupported_command", "$.command", "Command is not supported by schema v1.")
    };

    static T DeserializeArguments<T>(JsonElement element) where T : class, IAssistantCommandArgumentsV1
    {
        try
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText(), Options)
                ?? throw Error("schema_rejected", "$.arguments", "arguments cannot be null.");
        }
        catch (JsonException exception)
        {
            var path = exception.Path is null or "$" ? "$.arguments" : "$.arguments" + exception.Path[1..];
            throw Error("schema_rejected", path, $"arguments do not match {typeof(T).Name}.", exception);
        }
    }

    static AssistantCommandName ParseCommand(string? value) => value switch
    {
        "create_todo" => AssistantCommandName.CreateTodo,
        "create_reminder" => AssistantCommandName.CreateReminder,
        "create_event" => AssistantCommandName.CreateEvent,
        "create_long_term_item" => AssistantCommandName.CreateLongTermItem,
        "list_items" => AssistantCommandName.ListItems,
        "update_todo" => AssistantCommandName.UpdateTodo,
        "complete_todo" => AssistantCommandName.CompleteTodo,
        "delete_todo" => AssistantCommandName.DeleteTodo,
        "create_recurring_task" => AssistantCommandName.CreateRecurringTask,
        "reschedule_item" => AssistantCommandName.RescheduleItem,
        "decompose_goal" => AssistantCommandName.DecomposeGoal,
        "summarize_period" => AssistantCommandName.SummarizePeriod,
        _ => throw Error("unsupported_command", "$.command", $"Unsupported command '{value ?? "<null>"}'.")
    };

    internal static string CommandName(AssistantCommandName value) => value switch
    {
        AssistantCommandName.CreateTodo => "create_todo",
        AssistantCommandName.CreateReminder => "create_reminder",
        AssistantCommandName.CreateEvent => "create_event",
        AssistantCommandName.CreateLongTermItem => "create_long_term_item",
        AssistantCommandName.ListItems => "list_items",
        AssistantCommandName.UpdateTodo => "update_todo",
        AssistantCommandName.CompleteTodo => "complete_todo",
        AssistantCommandName.DeleteTodo => "delete_todo",
        AssistantCommandName.CreateRecurringTask => "create_recurring_task",
        AssistantCommandName.RescheduleItem => "reschedule_item",
        AssistantCommandName.DecomposeGoal => "decompose_goal",
        AssistantCommandName.SummarizePeriod => "summarize_period",
        _ => throw Error("unsupported_command", "$.command", "Command is not supported by schema v1.")
    };

    static IReadOnlyList<string> ValidateExplanationList(IReadOnlyList<string> values, string path)
    {
        if (values.Count > 32) throw Error("invalid_value", path, $"{path} contains too many values.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
                throw Error("invalid_value", path, $"{path} contains an invalid value.");
            if (!seen.Add(value)) throw Error("duplicate_value", path, $"{path} contains duplicate values.");
        }
        return values.ToArray();
    }

    static void RejectDuplicateProperties(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 24
            });
        }
        catch (JsonException exception)
        {
            throw Error("invalid_json", "$", "Command JSON is malformed.", exception);
        }

        using (document) Inspect(document.RootElement, "$", 0);

        static void Inspect(JsonElement element, string path, int depth)
        {
            if (depth > 24) throw Error("invalid_json", path, "Command JSON is too deeply nested.");
            if (element.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = path + "." + property.Name;
                    if (!seen.Add(property.Name)) throw Error("duplicate_property", childPath, $"Duplicate property '{property.Name}'.");
                    Inspect(property.Value, childPath, depth + 1);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in element.EnumerateArray()) Inspect(item, $"{path}[{index++}]", depth + 1);
            }
        }
    }

    static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 24
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }

    internal static AssistantCommandContractException Error(
        string code,
        string path,
        string message,
        Exception? innerException = null) => new(code, path, message, innerException);

    sealed class EnvelopeWire
    {
        public required int SchemaVersion { get; init; }
        public required string? Command { get; init; }
        public required JsonElement Arguments { get; init; }
        public required List<string>? MissingFields { get; init; }
        public required List<string>? AmbiguityReasons { get; init; }
    }
}

public static class AssistantCommandContractValidator
{
    public static void Validate(AssistantCommandEnvelope envelope)
    {
        if (envelope.SchemaVersion != AssistantCommandSchema.V1)
            throw AssistantCommandEnvelopeJson.Error("unsupported_version", "$.schemaVersion",
                $"Schema version {envelope.SchemaVersion} is not supported.");

        switch (envelope.Command, envelope.Arguments)
        {
            case (AssistantCommandName.CreateTodo, CreateTodoArgumentsV1 value):
                Text(value.Title, "title", 200);
                Text(value.Notes, "notes", 4000);
                Time(value.Due, "due");
                Time(value.Remind, "remind");
                Recurrence(value.Recurrence);
                break;
            case (AssistantCommandName.CreateReminder, CreateReminderArgumentsV1 value):
                Text(value.Title, "title", 200);
                Text(value.Notes, "notes", 4000);
                Time(value.Remind, "remind");
                Time(value.Due, "due");
                Recurrence(value.Recurrence);
                break;
            case (AssistantCommandName.CreateEvent, CreateEventArgumentsV1 value):
                Text(value.Title, "title", 200);
                Text(value.Notes, "notes", 4000);
                Time(value.Start, "start");
                Time(value.End, "end");
                Time(value.Remind, "remind");
                break;
            case (AssistantCommandName.CreateLongTermItem, CreateLongTermItemArgumentsV1 value):
                Text(value.Title, "title", 200);
                Text(value.Notes, "notes", 4000);
                Time(value.Due, "due");
                Time(value.Remind, "remind");
                break;
            case (AssistantCommandName.ListItems, ListItemsArgumentsV1 value):
                Period(value.Range, "range");
                break;
            case (AssistantCommandName.UpdateTodo, UpdateTodoArgumentsV1 value):
                Target(value.Target);
                Changes(value.Changes);
                break;
            case (AssistantCommandName.CompleteTodo, CompleteTodoArgumentsV1 value):
                Target(value.Target);
                break;
            case (AssistantCommandName.DeleteTodo, DeleteTodoArgumentsV1 value):
                Target(value.Target);
                break;
            case (AssistantCommandName.CreateRecurringTask, CreateRecurringTaskArgumentsV1 value):
                Text(value.Title, "title", 200);
                Text(value.Notes, "notes", 4000);
                if (value.Kind is not (null or AssistantItemKindV1.Todo or AssistantItemKindV1.Reminder))
                    throw Invalid("kind", "create_recurring_task supports todo or reminder only.");
                Time(value.WallStart, "wallStart");
                if (value.DailySchedule is { } schedule)
                {
                    if (value.Kind != AssistantItemKindV1.Reminder) throw Invalid("dailySchedule", "Daily schedules require a reminder.");
                    try { schedule.Validate(); } catch (ArgumentException e) { throw Invalid("dailySchedule", e.Message); }
                }
                Recurrence(value.Recurrence);
                break;
            case (AssistantCommandName.RescheduleItem, RescheduleItemArgumentsV1 value):
                Target(value.Target);
                Time(value.NewTime, "newTime");
                Time(value.NewReminder, "newReminder");
                break;
            case (AssistantCommandName.DecomposeGoal, DecomposeGoalArgumentsV1 value):
                Text(value.Goal, "goal", 1000);
                if (value.Constraints is { Count: > 20 }) throw Invalid("constraints", "At most 20 constraints are allowed.");
                foreach (var constraint in value.Constraints ?? []) Text(constraint, "constraints", 500);
                if (value.MaxItems is <= 0 or > 10) throw Invalid("maxItems", "maxItems must be between 1 and 10.");
                if (value.ProposedTasks is { Count: > 10 }) throw Invalid("proposedTasks", "At most 10 proposed tasks are allowed.");
                if (value.ProposedTasks is { } proposals && value.MaxItems is { } maximum && proposals.Count > maximum)
                    throw Invalid("proposedTasks", "proposedTasks cannot exceed maxItems.");
                foreach (var proposal in value.ProposedTasks ?? [])
                {
                    if (string.IsNullOrWhiteSpace(proposal.Title)) throw Invalid("proposedTasks.title", "Each proposed task needs a title.");
                    Text(proposal.Title, "proposedTasks.title", 200);
                    Text(proposal.Category, "proposedTasks.category", 100);
                    if (proposal.EstimatedMinutes is <= 0 or > 1440)
                        throw Invalid("proposedTasks.estimatedMinutes", "estimatedMinutes must be between 1 and 1440.");
                }
                break;
            case (AssistantCommandName.SummarizePeriod, SummarizePeriodArgumentsV1 value):
                Period(value.Period, "period");
                break;
            default:
                throw AssistantCommandEnvelopeJson.Error("argument_type_mismatch", "$.arguments",
                    $"arguments do not match {AssistantCommandEnvelopeJson.CommandName(envelope.Command)}.");
        }
    }

    static void Text(string? value, string path, int maxLength)
    {
        if (value?.Length > maxLength) throw Invalid(path, $"{path} is too long.");
    }

    static void Time(AssistantTimeExpressionV1? value, string path)
    {
        if (value is null) return;
        Text(value.RelativeExpression, path + ".relativeExpression", 200);
        Text(value.TimeZoneHint, path + ".timeZoneHint", 128);
        Text(value.OriginalText, path + ".originalText", 500);
        if (string.IsNullOrWhiteSpace(value.OriginalText))
            throw Invalid(path + ".originalText", "A time expression must preserve the user's original text.");
    }

    static void Target(AssistantTargetSelectorV1? value)
    {
        if (value is null) return;
        Text(value.CandidateRef, "target.candidateRef", 64);
        Text(value.Title, "target.title", 200);
        Time(value.TimeHint, "target.timeHint");
    }

    static void Changes(UpdateTodoChangesV1? value)
    {
        if (value is null) return;
        Text(value.Title, "changes.title", 200);
        Text(value.Notes, "changes.notes", 4000);
        Time(value.Due, "changes.due");
        Time(value.Remind, "changes.remind");
        var clear = value.ClearFields ?? [];
        if (value.DailySchedule is { } schedule)
        {
            try { schedule.Validate(); } catch (ArgumentException e) { throw Invalid("changes.dailySchedule", e.Message); }
            if (value.Remind is not null || value.Due is not null || clear.Contains(UpdateTodoClearFieldV1.Remind) || clear.Contains(UpdateTodoClearFieldV1.Due))
                throw Invalid("changes.dailySchedule", "A schedule change cannot also set or clear individual times.");
        }
        if (clear.Distinct().Count() != clear.Count) throw Invalid("changes.clearFields", "clearFields contains duplicates.");
        if (value.Due is not null && clear.Contains(UpdateTodoClearFieldV1.Due))
            throw Invalid("changes.due", "due cannot be set and cleared together.");
        if (value.Remind is not null && clear.Contains(UpdateTodoClearFieldV1.Remind))
            throw Invalid("changes.remind", "remind cannot be set and cleared together.");
        if (value.Notes is not null && clear.Contains(UpdateTodoClearFieldV1.Notes))
            throw Invalid("changes.notes", "notes cannot be set and cleared together.");
    }

    static void Recurrence(AssistantRecurrenceRuleV1? value)
    {
        if (value is null) return;
        if (value.Interval is <= 0 or > 366) throw Invalid("recurrence.interval", "interval must be between 1 and 366.");
        var weekdays = value.Weekdays ?? [];
        if (weekdays.Distinct().Count() != weekdays.Count) throw Invalid("recurrence.weekdays", "weekdays contains duplicates.");
        if (value.Frequency == AssistantRecurrenceFrequencyV1.Weekly && weekdays.Count == 0)
            throw Invalid("recurrence.weekdays", "A weekly recurrence requires weekdays.");
        if (value.Frequency != AssistantRecurrenceFrequencyV1.Weekly && weekdays.Count != 0)
            throw Invalid("recurrence.weekdays", "weekdays is only valid for weekly recurrence.");
        if (value.Frequency == AssistantRecurrenceFrequencyV1.Monthly && value.MonthDay is < 1 or > 31)
            throw Invalid("recurrence.monthDay", "A monthly recurrence requires monthDay from 1 to 31.");
        if (value.Frequency != AssistantRecurrenceFrequencyV1.Monthly && value.MonthDay is not null)
            throw Invalid("recurrence.monthDay", "monthDay is only valid for monthly recurrence.");
        if (value.End is not null)
        {
            if (value.End.Kind == AssistantRecurrenceEndKindV1.Count && value.End.Count is <= 0)
                throw Invalid("recurrence.end.count", "A count end requires a positive count.");
            if (value.End.Kind != AssistantRecurrenceEndKindV1.Count && value.End.Count is not null)
                throw Invalid("recurrence.end.count", "count is only valid for a count end.");
            if (value.End.Kind == AssistantRecurrenceEndKindV1.Until && value.End.UntilDate is null)
                throw Invalid("recurrence.end.untilDate", "An until end requires untilDate.");
            if (value.End.Kind != AssistantRecurrenceEndKindV1.Until && value.End.UntilDate is not null)
                throw Invalid("recurrence.end.untilDate", "untilDate is only valid for an until end.");
        }
    }

    static void Period(AssistantPeriodV1? value, string path)
    {
        if (value is null) return;
        Text(value.OriginalText, path + ".originalText", 500);
        if (string.IsNullOrWhiteSpace(value.OriginalText))
            throw Invalid(path + ".originalText", "A period must preserve the user's original text.");
        if (value.StartDate is not null && value.EndDate is not null && value.EndDate <= value.StartDate)
            throw Invalid(path + ".endDate", "endDate must be later than startDate.");
        if (value.Kind != AssistantPeriodKindV1.Custom && (value.StartDate is not null || value.EndDate is not null))
            throw Invalid(path, "Explicit dates are only valid for a custom period.");
    }

    static AssistantCommandContractException Invalid(string path, string message) =>
        AssistantCommandEnvelopeJson.Error("invalid_value", "$.arguments." + path, message);
}
