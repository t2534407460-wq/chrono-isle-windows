using ChronoIsle.App.Services.Domain;

namespace ChronoIsle.Tests;

public sealed class MonthlyRecurrenceCalculatorTests
{
    [Fact]
    public void MonthDay31_SkipsShortMonthsWithoutMovingToMonthEnd()
    {
        var january = new DateTime(2026, 1, 31, 9, 0, 0, DateTimeKind.Unspecified);

        var march = MonthlyRecurrenceCalculator.NextAfter(january, 31);
        var may = MonthlyRecurrenceCalculator.NextAfter(march, 31);

        Assert.Equal(new DateTime(2026, 3, 31, 9, 0, 0), march);
        Assert.Equal(new DateTime(2026, 5, 31, 9, 0, 0), may);
    }

    [Fact]
    public void MonthDay30_SkipsFebruaryIncludingLeapYears()
    {
        var january = new DateTime(2028, 1, 30, 9, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(new DateTime(2028, 3, 30, 9, 0, 0), MonthlyRecurrenceCalculator.NextAfter(january, 30));
    }
}