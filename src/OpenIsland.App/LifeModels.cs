namespace OpenIsland.App;

public sealed record TodoItem(
    string Id,
    string Title,
    string? Notes,
    bool IsCompleted,
    DateTime? DueAt,
    DateTime? RemindAt,
    DateTime? NotifiedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CalendarEventItem(
    string Id,
    string Title,
    string? Notes,
    DateTime StartsAt,
    DateTime EndsAt,
    DateTime? RemindAt,
    DateTime? NotifiedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record SingleReminder(
    string Id,
    string Title,
    string? Notes,
    DateTime RemindAt,
    DateTime? NotifiedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);
public enum RecurrenceKind
{
    Daily,
    Weekdays,
    Weekly
}

public sealed record RecurringReminder(
    string Id,
    string Title,
    string? Notes,
    TimeOnly ReminderTime,
    RecurrenceKind Recurrence,
    IReadOnlyList<DayOfWeek> Weekdays,
    DateTime? LastNotifiedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AgendaItem(
    string Id,
    string Kind,
    string Title,
    string? Notes,
    DateTime StartsAt,
    DateTime? EndsAt,
    DateTime? RemindAt,
    bool IsCompleted,
    bool IsRecurring = false);

public enum IslandIndicatorState
{
    Idle,
    ReminderOnly,
    PendingTodo,
    DueSoonTodo,
    OverdueTodo
}
public sealed record ManagedLifeItem(
    string Id,
    string Kind,
    string Title,
    string? Notes,
    DateTime? ScheduledAt,
    bool IsCompleted,
    string? RecurrenceLabel);

public sealed record ChatSession(string Id, string Title, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record ChatMessage(string Id, string SessionId, string Role, string Content, DateTime CreatedAt);

public sealed record AssistantAction(
    string Id,
    string SessionId,
    string SourceText,
    string IntentJson,
    string Status,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ProviderSettings(string BaseUrl, string Model, string ApiKey)
{
    public static ProviderSettings Default => new("https://api.deepseek.com/v1", "deepseek-chat", "");
}

public sealed record LifePreferences(bool WindowsNotifications)
{
    public static LifePreferences Default => new(true);
}

public sealed record ModelMessage(string Role, string Content);

public enum AssistantIntentKind
{
    CreateTodo,
    CreateEvent,
    CreateRecurringReminder,
    QueryToday,
    Chat
}

public sealed record AssistantIntent(
    AssistantIntentKind Kind,
    string? Title,
    string? Notes,
    DateTime? DueAt,
    DateTime? ReminderAt,
    TimeOnly? ReminderTime,
    RecurrenceKind? Recurrence,
    IReadOnlyList<DayOfWeek> Weekdays,
    DateTime? StartsAt,
    DateTime? EndsAt,
    bool ReminderRequested,
    string? Reply,
    IReadOnlyList<string> MissingFields);

public sealed record IntentAnalysis(
    AssistantIntent? Intent,
    string RawJson,
    string? ErrorMessage,
    string? ClarificationMessage)
{
    public bool IsValid => Intent is not null && ErrorMessage is null;
    public bool NeedsClarification => IsValid && !string.IsNullOrWhiteSpace(ClarificationMessage);
    public bool NeedsConfirmation => IsValid && !NeedsClarification && Intent!.Kind is
        AssistantIntentKind.CreateTodo or AssistantIntentKind.CreateEvent or AssistantIntentKind.CreateRecurringReminder;
}

public enum ConversationRouteKind
{
    CreateAction,
    LocalQuery,
    GeneralChat
}

public sealed record LocalAgendaQuery(DateTime StartsAt, DateTime EndsAt, string Label);
public sealed record ConversationRoute(ConversationRouteKind Kind, LocalAgendaQuery? Query = null);
public sealed record LocalAgendaQueryResult(LocalAgendaQuery Query, IReadOnlyList<AgendaItem> Items, string ListText);
public sealed record AssistantConversationResult(string Reply, AssistantAction? PendingAction, bool IsFailure);
public sealed record ActionExecutionResult(bool Succeeded, string Message, AgendaItem? AgendaItem);