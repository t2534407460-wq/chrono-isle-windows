using OpenIsland.App.Services;

namespace OpenIsland.Tests;

public sealed class ChinaStatutoryHolidayCalendarTests
{
    readonly ChinaStatutoryHolidayCalendar calendar = new();

    [Fact]
    public void Official2026HolidayAndMakeUpWorkday_AreBothRepresented()
    {
        var springFestival = calendar.Get(new DateTime(2026, 2, 16));
        var makeUpWorkday = calendar.Get(new DateTime(2026, 2, 14));

        Assert.True(springFestival.IsHoliday);
        Assert.Equal("\u6625\u8282", springFestival.Name);
        Assert.Equal(ChinaStatutoryHolidayCalendar.Source2026, springFestival.Source);
        Assert.True(makeUpWorkday.IsAdjustedWorkday);
        Assert.False(calendar.IsRestDay(new DateTime(2026, 2, 14)));
    }

    [Fact]
    public void RestDay_UsesHolidayDataBeforeTheUsualWeekendRule()
    {
        Assert.True(calendar.IsRestDay(new DateTime(2026, 10, 3)));
        Assert.False(calendar.IsRestDay(new DateTime(2026, 10, 10)));
        Assert.True(calendar.IsRestDay(new DateTime(2026, 7, 18)));
    }

    [Fact]
    public void UnsupportedYear_IsNotInventedAsAnOfficialHoliday()
    {
        var day = calendar.Get(new DateTime(2027, 10, 1));

        Assert.Equal(OfficialCalendarDayKind.None, day.Kind);
        Assert.Null(day.Name);
    }
}
