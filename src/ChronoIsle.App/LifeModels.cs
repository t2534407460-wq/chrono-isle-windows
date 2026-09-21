using ChronoIsle.App.Services.Domain;

namespace ChronoIsle.App;

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
    OfficialWorkdays,
    StatutoryHolidays,
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
    DateTime UpdatedAt,
    DateTime? StartsAt = null,
    Services.Domain.ReminderDailySchedule? Schedule = null);

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

public sealed record ItemNavigationTarget(string Id, string Kind)
{
    public static ItemNavigationTarget From(AgendaItem item) => new(item.Id, item.Kind);

    public static ItemNavigationTarget From(string id, LifeItemKind kind) =>
        new(id, kind == LifeItemKind.LongTerm ? "long_term" : kind.ToString().ToLowerInvariant());
}

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

public sealed record ArchivedTodoItem(
    string Id,
    string Title,
    string? Notes,
    DateTime? DueAt,
    DateTime ArchivedAt,
    string Reason,
    string Kind = "todo");

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

public sealed record LifePreferences(
    bool WindowsNotifications,
    string AssistantPersona = "Direct",
    bool DoNotDisturbEnabled = false,
    bool FullScreenSilentEnabled = false,
    bool IslandTaskbarDocked = false,
    string? IslandTaskbarMonitor = null,
    double? IslandTaskbarHorizontalRatio = null,
    bool MediaAutoTakeover = true,
    bool LyricsEnabled = true,
    int LyricsOffsetMs = 0,
    bool MoveIslandDuringFullscreen = true,
    string ThemeMode = "System",
    bool TelemetryEnabled = true,
    bool ToastInboxEnabled = true,
    bool GlowBorderEnabled = true,
    string AccentScheme = "Emerald",
    bool IslandShowMascot = true,
    bool IslandShowStatusLight = true,
    bool IslandShowAgendaSummary = true,
    bool IslandShowNetworkSpeed = false,
    bool IslandShowNetworkStatus = false,
    bool IslandShowClock = true,
    bool IslandShowExpandIndicator = true,
    bool IslandTopDockAutoFold = true,
    bool IslandShowCpuUsage = false,
    bool IslandShowMemoryUsage = false,
    bool IslandShowFps = true,
    bool IslandShowMusicMode = true,
    bool IslandShowNetworkLatency = false)
{
    public static LifePreferences Default => new(true, "Direct");
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
    ModificationClarification,
    GeneralChat
}

public sealed record LocalAgendaQuery(DateTime StartsAt, DateTime EndsAt, string Label);
public sealed record ConversationRoute(ConversationRouteKind Kind, LocalAgendaQuery? Query = null);
public sealed record LocalAgendaQueryResult(LocalAgendaQuery Query, IReadOnlyList<AgendaItem> Items, string ListText);
public sealed record AssistantPendingPlanStep(
    int StepIndex,
    string Operation,
    string Description,
    IReadOnlyList<string> Targets);

public sealed record AssistantPendingPlan(
    string PlanId,
    string RiskReason,
    IReadOnlyList<AssistantPendingPlanStep> Steps,
    string State);

public sealed record AssistantConversationResult(
    string Reply, AssistantAction? PendingAction, bool IsFailure, bool RefreshReminders = false,
    AssistantPendingPlan? PendingPlan = null,
    ChronoIsle.App.Services.Commanding.AssistantInteraction? Interaction = null);
public sealed record ActionExecutionResult(bool Succeeded, string Message, AgendaItem? AgendaItem);
/// <summary>
/// Immutable snapshot of a one-off reminder selected for a batch reschedule.
/// The original reminder timestamp is part of the snapshot so a stale
/// confirmation cannot overwrite a newer user edit.
/// </summary>
public sealed record HolidayReminderTarget(
    string Id,
    string Kind,
    string Title,
    DateTime OriginalRemindAt);

public sealed record HolidayReminderTargetSet(
    IReadOnlyList<HolidayReminderTarget> Targets,
    int ExcludedRecurringOccurrences);

public sealed record HolidayReminderBatchApplyResult(int AppliedCount);
