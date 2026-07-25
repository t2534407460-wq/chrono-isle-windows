namespace ChronoIsle.App.Services;

public enum OfficialCalendarDayKind
{
    None,
    StatutoryHoliday,
    AdjustedWorkday
}

public sealed record OfficialCalendarDay(
    DateOnly Date,
    OfficialCalendarDayKind Kind,
    string? Name,
    string? Source)
{
    public bool IsHoliday => Kind == OfficialCalendarDayKind.StatutoryHoliday;
    public bool IsAdjustedWorkday => Kind == OfficialCalendarDayKind.AdjustedWorkday;
}

/// <summary>
/// Local, versioned Chinese statutory-holiday data. It deliberately never guesses
/// a future year's make-up workdays; those dates must come from an official notice.
/// </summary>
public sealed class ChinaStatutoryHolidayCalendar
{
    public const string Source2025 = "\u56fd\u52a1\u9662\u529e\u516c\u5385\u56fd\u529e\u53d1\u660e\u7535\u30142024\u301512\u53f7";
    public const string Source2026 = "\u56fd\u52a1\u9662\u529e\u516c\u5385\u56fd\u529e\u53d1\u660e\u7535\u30142025\u30157\u53f7";

    static readonly IReadOnlyDictionary<DateOnly, OfficialCalendarDay> entries = BuildEntries();

    public OfficialCalendarDay Get(DateTime localDate) => Get(DateOnly.FromDateTime(localDate));

    public OfficialCalendarDay Get(DateOnly localDate) =>
        entries.TryGetValue(localDate, out var day)
            ? day
            : new OfficialCalendarDay(localDate, OfficialCalendarDayKind.None, null, null);

    public bool IsRestDay(DateTime localDate)
    {
        var day = Get(localDate);
        if (day.IsAdjustedWorkday) return false;
        if (day.IsHoliday) return true;
        return localDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    }

    static IReadOnlyDictionary<DateOnly, OfficialCalendarDay> BuildEntries()
    {
        var values = new Dictionary<DateOnly, OfficialCalendarDay>();

        AddHoliday(values, 2025, 1, 1, 1, "\u5143\u65e6", Source2025);
        AddHoliday(values, 2025, 1, 28, 8, "\u6625\u8282", Source2025);
        AddWorkday(values, 2025, 1, 26, Source2025);
        AddWorkday(values, 2025, 2, 8, Source2025);
        AddHoliday(values, 2025, 4, 4, 3, "\u6e05\u660e\u8282", Source2025);
        AddHoliday(values, 2025, 5, 1, 5, "\u52b3\u52a8\u8282", Source2025);
        AddWorkday(values, 2025, 4, 27, Source2025);
        AddHoliday(values, 2025, 5, 31, 3, "\u7aef\u5348\u8282", Source2025);
        AddHoliday(values, 2025, 10, 1, 8, "\u56fd\u5e86\u8282\u3001\u4e2d\u79cb\u8282", Source2025);
        AddWorkday(values, 2025, 9, 28, Source2025);
        AddWorkday(values, 2025, 10, 11, Source2025);

        AddHoliday(values, 2026, 1, 1, 3, "\u5143\u65e6", Source2026);
        AddWorkday(values, 2026, 1, 4, Source2026);
        AddHoliday(values, 2026, 2, 15, 9, "\u6625\u8282", Source2026);
        AddWorkday(values, 2026, 2, 14, Source2026);
        AddWorkday(values, 2026, 2, 28, Source2026);
        AddHoliday(values, 2026, 4, 4, 3, "\u6e05\u660e\u8282", Source2026);
        AddHoliday(values, 2026, 5, 1, 5, "\u52b3\u52a8\u8282", Source2026);
        AddWorkday(values, 2026, 5, 9, Source2026);
        AddHoliday(values, 2026, 6, 19, 3, "\u7aef\u5348\u8282", Source2026);
        AddHoliday(values, 2026, 9, 25, 3, "\u4e2d\u79cb\u8282", Source2026);
        AddHoliday(values, 2026, 10, 1, 7, "\u56fd\u5e86\u8282", Source2026);
        AddWorkday(values, 2026, 9, 20, Source2026);
        AddWorkday(values, 2026, 10, 10, Source2026);

        return values;
    }

    static void AddHoliday(IDictionary<DateOnly, OfficialCalendarDay> values, int year, int month, int day, int count, string name, string source)
    {
        var first = new DateOnly(year, month, day);
        for (var offset = 0; offset < count; offset++)
        {
            var date = first.AddDays(offset);
            values[date] = new OfficialCalendarDay(date, OfficialCalendarDayKind.StatutoryHoliday, name, source);
        }
    }

    static void AddWorkday(IDictionary<DateOnly, OfficialCalendarDay> values, int year, int month, int day, string source)
    {
        var date = new DateOnly(year, month, day);
        values[date] = new OfficialCalendarDay(date, OfficialCalendarDayKind.AdjustedWorkday, "\u8c03\u4f11\u4e0a\u73ed", source);
    }
}
