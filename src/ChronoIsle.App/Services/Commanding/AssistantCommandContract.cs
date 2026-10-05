namespace ChronoIsle.App.Services.Commanding;

public static class AssistantCommandSchema
{
    public const int V1 = 1;
    public const int Current = V1;
}

public enum AssistantCommandName
{
    CreateTodo,
    CreateReminder,
    CreateEvent,
    CreateLongTermItem,
    ListItems,
    UpdateTodo,
    CompleteTodo,
    DeleteTodo,
    CreateRecurringTask,
    RescheduleItem,
    DecomposeGoal,
    SummarizePeriod
}

public enum AssistantPriorityV1
{
    Low,
    Normal,
    High,
    Urgent
}

public enum AssistantItemKindV1
{
    Todo,
    Reminder,
    Event,
    LongTerm
}

public enum AssistantRecurrenceFrequencyV1
{
    Daily,
    Weekly,
    Monthly
}

public enum AssistantRecurrenceEndKindV1
{
    Never,
    Count,
    Until
}

public enum AssistantPeriodKindV1
{
    Today,
    ThisWeek,
    ThisMonth,
    Custom
}

public enum UpdateTodoClearFieldV1
{
    Notes,
    Due,
    Remind
}

public interface IAssistantCommandArgumentsV1;

/// <summary>
/// A model-parsed time expression. It is input to the local time resolver and must never be
/// persisted as a resolved business timestamp without local validation.
/// </summary>
public sealed record AssistantTimeExpressionV1(
    DateOnly? LocalDate,
    TimeOnly? LocalTime,
    string? RelativeExpression,
    string? TimeZoneHint,
    string? OriginalText)
{
    public bool HasAnyTimeClue => LocalDate is not null || LocalTime is not null ||
        !string.IsNullOrWhiteSpace(RelativeExpression);

    public bool HasResolvableInstantShape => !string.IsNullOrWhiteSpace(RelativeExpression) ||
        LocalDate is not null && LocalTime is not null;
}

public sealed record AssistantRecurrenceEndV1(
    AssistantRecurrenceEndKindV1? Kind,
    int? Count,
    DateOnly? UntilDate);

public sealed record AssistantRecurrenceRuleV1(
    AssistantRecurrenceFrequencyV1? Frequency,
    int? Interval,
    IReadOnlyList<DayOfWeek>? Weekdays,
    int? MonthDay,
    AssistantRecurrenceEndV1? End);

/// <summary>
/// Describes an existing item only with user-visible clues. Database IDs and row versions are
/// deliberately absent and therefore rejected by strict JSON deserialization.
/// </summary>
public sealed record AssistantTargetSelectorV1(
    string? Title,
    AssistantItemKindV1? Kind,
    AssistantTimeExpressionV1? TimeHint,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? CandidateRef = null)
{
    public bool HasAnyClue => !string.IsNullOrWhiteSpace(CandidateRef) ||
                              !string.IsNullOrWhiteSpace(Title) ||
                              TimeHint?.HasAnyTimeClue == true;
}

public sealed record AssistantPeriodV1(
    AssistantPeriodKindV1? Kind,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? OriginalText);

public sealed record CreateTodoArgumentsV1(
    string? Title,
    string? Notes,
    AssistantTimeExpressionV1? Due,
    AssistantTimeExpressionV1? Remind,
    AssistantRecurrenceRuleV1? Recurrence,
    AssistantPriorityV1? Priority) : IAssistantCommandArgumentsV1;

public sealed record CreateReminderArgumentsV1(
    string? Title,
    string? Notes,
    AssistantTimeExpressionV1? Remind,
    AssistantTimeExpressionV1? Due,
    AssistantRecurrenceRuleV1? Recurrence,
    AssistantPriorityV1? Priority) : IAssistantCommandArgumentsV1;

public sealed record CreateEventArgumentsV1(
    string? Title,
    string? Notes,
    AssistantTimeExpressionV1? Start,
    AssistantTimeExpressionV1? End,
    AssistantTimeExpressionV1? Remind) : IAssistantCommandArgumentsV1;

public sealed record CreateLongTermItemArgumentsV1(
    string? Title,
    string? Notes,
    AssistantTimeExpressionV1? Due,
    AssistantTimeExpressionV1? Remind,
    AssistantPriorityV1? Priority) : IAssistantCommandArgumentsV1;

public sealed record ListItemsArgumentsV1(
    AssistantPeriodV1? Range,
    AssistantItemKindV1? Kind,
    bool? IncludeCompleted) : IAssistantCommandArgumentsV1;

public sealed record UpdateTodoChangesV1(
    string? Title,
    string? Notes,
    AssistantTimeExpressionV1? Due,
    AssistantTimeExpressionV1? Remind,
    AssistantPriorityV1? Priority,
    IReadOnlyList<UpdateTodoClearFieldV1>? ClearFields,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ChronoIsle.App.Services.Domain.ReminderDailySchedule? DailySchedule = null)
{
    public bool HasAnyChange => !string.IsNullOrWhiteSpace(Title) || Notes is not null || Due is not null ||
        Remind is not null || Priority is not null || ClearFields is { Count: > 0 } || DailySchedule is not null;
}

public sealed record UpdateTodoArgumentsV1(
    AssistantTargetSelectorV1? Target,
    UpdateTodoChangesV1? Changes) : IAssistantCommandArgumentsV1;

public sealed record CompleteTodoArgumentsV1(
    AssistantTargetSelectorV1? Target) : IAssistantCommandArgumentsV1;

public sealed record DeleteTodoArgumentsV1(
    AssistantTargetSelectorV1? Target) : IAssistantCommandArgumentsV1;

public sealed record CreateRecurringTaskArgumentsV1(
    string? Title,
    string? Notes,
    AssistantItemKindV1? Kind,
    AssistantTimeExpressionV1? WallStart,
    AssistantRecurrenceRuleV1? Recurrence,
    AssistantPriorityV1? Priority,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ChronoIsle.App.Services.Domain.ReminderDailySchedule? DailySchedule = null) : IAssistantCommandArgumentsV1;

public sealed record RescheduleItemArgumentsV1(
    AssistantTargetSelectorV1? Target,
    AssistantTimeExpressionV1? NewTime,
    AssistantTimeExpressionV1? NewReminder) : IAssistantCommandArgumentsV1;

public sealed record DecomposedTaskArgumentsV1(
    string? Title,
    AssistantPriorityV1? Priority,
    int? EstimatedMinutes,
    string? Category);

public sealed record DecomposeGoalArgumentsV1(
    string? Goal,
    IReadOnlyList<string>? Constraints,
    int? MaxItems,
    IReadOnlyList<DecomposedTaskArgumentsV1>? ProposedTasks = null) : IAssistantCommandArgumentsV1;

public sealed record SummarizePeriodArgumentsV1(
    AssistantPeriodV1? Period) : IAssistantCommandArgumentsV1;

public sealed record AssistantCommandEnvelope
{
    public int SchemaVersion { get; }
    public AssistantCommandName Command { get; }
    public IAssistantCommandArgumentsV1 Arguments { get; }
    public IReadOnlyList<string> MissingFields { get; }
    public IReadOnlyList<string> AmbiguityReasons { get; }

    public AssistantCommandEnvelope(
        int schemaVersion,
        AssistantCommandName command,
        IAssistantCommandArgumentsV1 arguments,
        IEnumerable<string>? missingFields,
        IEnumerable<string>? ambiguityReasons)
    {
        SchemaVersion = schemaVersion;
        Command = command;
        Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        MissingFields = (missingFields ?? []).ToArray();
        AmbiguityReasons = (ambiguityReasons ?? []).ToArray();
    }
}
