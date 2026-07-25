namespace ChronoIsle.App.Services.Domain;

/// <summary>Calculates monthly wall-clock recurrences. Months without the requested day are skipped.</summary>
public static class MonthlyRecurrenceCalculator
{
    public static DateTime NextAfter(DateTime after, int monthDay)
    {
        if (monthDay is < 1 or > 31) throw new ArgumentOutOfRangeException(nameof(monthDay));

        var year = after.Year;
        var month = after.Month;
        for (var attempts = 0; attempts < 2400; attempts++)
        {
            if (DateTime.DaysInMonth(year, month) >= monthDay)
            {
                var candidate = new DateTime(year, month, monthDay, after.Hour, after.Minute, after.Second,
                    after.Millisecond, after.Kind);
                if (candidate > after) return candidate;
            }

            if (++month == 13) { month = 1; year++; }
        }

        throw new InvalidOperationException("No monthly occurrence could be calculated.");
    }
}