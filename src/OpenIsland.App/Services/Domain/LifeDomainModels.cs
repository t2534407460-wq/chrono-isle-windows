namespace OpenIsland.App.Services.Domain;

public enum LifeItemKind
{
    Todo,
    Reminder,
    Event
}

public enum LifeItemStatus
{
    Pending,
    InProgress,
    Completed,
    Deferred,
    Cancelled,
    Ignored
}

public enum LifePriority
{
    Low,
    Normal,
    High,
    Urgent
}

public enum EnergyLevel
{
    Low,
    Medium,
    High
}

public enum TimeSemantics
{
    AbsoluteInstant,
    ZonedWallClock,
    DeviceLocalFloatingWallClock
}

public enum LifeItemOrigin
{
    Local,
    IcsImport,
    MicrosoftToDo,
    OutlookCalendar
}

public enum RecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly
}

public enum RecurrenceEndKind
{
    Never,
    Count,
    Until
}

public sealed record TemporalValue(
    DateTime? LocalDateTime,
    DateTimeOffset? UtcInstant,
    string? IanaTimeZoneId,
    string? WindowsTimeZoneIdCache,
    TimeSemantics Semantics)
{
    public static TemporalValue Absolute(DateTimeOffset instant) =>
        new(null, instant.ToUniversalTime(), null, null, TimeSemantics.AbsoluteInstant);

    public static TemporalValue Zoned(DateTime localDateTime, string ianaTimeZoneId, string? windowsTimeZoneId = null) =>
        new(DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified), null, ianaTimeZoneId, windowsTimeZoneId, TimeSemantics.ZonedWallClock);

    public static TemporalValue DeviceLocal(DateTime localDateTime) =>
        new(DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified), null, null, null, TimeSemantics.DeviceLocalFloatingWallClock);
}

public sealed record LifeItem(
    string Id,
    LifeItemKind Kind,
    string Title,
    string? Notes,
    LifeItemStatus Status,
    LifePriority Priority,
    TemporalValue? Due,
    TemporalValue? Reminder,
    TemporalValue? Start,
    TemporalValue? End,
    string? Category,
    int? EstimatedMinutes,
    EnergyLevel? Energy,
    string? ParentItemId,
    DateTimeOffset? CompletedAt,
    int PostponedCount,
    LifeItemOrigin Origin,
    string? OriginAdapter,
    bool IsReadOnly,
    string? ReadOnlyReason,
    string? RawExternalPayloadId,
    long RowVersion,
    DateTimeOffset? DeletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RecurrenceRule(
    string Id,
    string ItemId,
    long RuleRevision,
    DateTime StartLocalDateTime,
    string IanaTimeZoneId,
    string? WindowsTimeZoneIdCache,
    RecurrenceFrequency Frequency,
    int Interval,
    IReadOnlyList<DayOfWeek> Weekdays,
    int? MonthDay,
    RecurrenceEndKind EndKind,
    int? OccurrenceCount,
    DateTime? UntilLocalDateTime,
    DateTimeOffset? NextOccurrenceUtc);

public sealed record OccurrenceKey(
    string SeriesItemId,
    long RuleRevision,
    DateTime OriginalStartLocalDateTime,
    DateTimeOffset OriginalStartUtc,
    string IanaTimeZoneId)
{
    public override string ToString() =>
        $"{SeriesItemId}:{RuleRevision}:{OriginalStartLocalDateTime:O}:{OriginalStartUtc:O}:{IanaTimeZoneId}";
}
