using System.Globalization;

namespace OpenIsland.App.Services.Sync;

/// <summary>
/// Writes self-contained VTIMEZONE components for the IANA zones referenced by an export.
/// The transition rules come from the host time-zone database; an unknown zone is deliberately
/// omitted so that callers never invent an offset.
/// </summary>
internal static class IcsTimeZoneWriter
{
    static readonly string[] DayNames = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    public static IReadOnlyList<string> Build(string ianaTimeZoneId, int anchorYear)
    {
        if (!TryResolve(ianaTimeZoneId, out var zone)) return [];
        var lines = new List<string>
        {
            "BEGIN:VTIMEZONE",
            "TZID:" + ianaTimeZoneId,
            "X-LIC-LOCATION:" + ianaTimeZoneId
        };

        var rules = zone.GetAdjustmentRules();
        var rule = rules.LastOrDefault(value => value.DateStart.Year <= anchorYear && value.DateEnd.Year >= anchorYear);
        if (rule is null || rule.DaylightDelta == TimeSpan.Zero)
        {
            AppendStandard(lines, zone, zone.BaseUtcOffset, new DateTime(Math.Max(1970, anchorYear), 1, 1));
        }
        else
        {
            var daylightOffset = zone.BaseUtcOffset + rule.DaylightDelta;
            AppendTransition(lines, "DAYLIGHT", zone.DaylightName, zone.BaseUtcOffset, daylightOffset,
                rule.DaylightTransitionStart, anchorYear);
            AppendTransition(lines, "STANDARD", zone.StandardName, daylightOffset, zone.BaseUtcOffset,
                rule.DaylightTransitionEnd, anchorYear);
        }

        lines.Add("END:VTIMEZONE");
        return lines;
    }

    static void AppendStandard(List<string> lines, TimeZoneInfo zone, TimeSpan offset, DateTime start)
    {
        lines.Add("BEGIN:STANDARD");
        lines.Add("DTSTART:" + start.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
        lines.Add("TZOFFSETFROM:" + Offset(offset));
        lines.Add("TZOFFSETTO:" + Offset(offset));
        lines.Add("TZNAME:" + IcsCodec.Escape(zone.StandardName));
        lines.Add("END:STANDARD");
    }

    static void AppendTransition(List<string> lines, string kind, string name, TimeSpan from, TimeSpan to,
        TimeZoneInfo.TransitionTime transition, int year)
    {
        var date = ResolveTransitionDate(transition, year);
        lines.Add("BEGIN:" + kind);
        lines.Add("DTSTART:" + date.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
        lines.Add("TZOFFSETFROM:" + Offset(from));
        lines.Add("TZOFFSETTO:" + Offset(to));
        lines.Add("TZNAME:" + IcsCodec.Escape(name));
        lines.Add("RRULE:FREQ=YEARLY;BYMONTH=" + transition.Month + ";" + BuildByRule(transition));
        lines.Add("END:" + kind);
    }

    static string BuildByRule(TimeZoneInfo.TransitionTime transition)
    {
        if (transition.IsFixedDateRule)
            return "BYMONTHDAY=" + transition.Day;
        var ordinal = transition.Week == 5 ? "-1" : transition.Week.ToString(CultureInfo.InvariantCulture);
        return "BYDAY=" + ordinal + DayNames[(int)transition.DayOfWeek];
    }

    static DateTime ResolveTransitionDate(TimeZoneInfo.TransitionTime transition, int year)
    {
        var day = transition.IsFixedDateRule
            ? Math.Min(transition.Day, DateTime.DaysInMonth(year, transition.Month))
            : FloatingDay(year, transition.Month, transition.Week, transition.DayOfWeek);
        return new DateTime(year, transition.Month, day, transition.TimeOfDay.Hour,
            transition.TimeOfDay.Minute, transition.TimeOfDay.Second, DateTimeKind.Unspecified);
    }

    static int FloatingDay(int year, int month, int week, DayOfWeek dayOfWeek)
    {
        if (week == 5)
        {
            var last = DateTime.DaysInMonth(year, month);
            return last - ((7 + (int)new DateTime(year, month, last).DayOfWeek - (int)dayOfWeek) % 7);
        }
        var first = new DateTime(year, month, 1);
        return 1 + ((7 + (int)dayOfWeek - (int)first.DayOfWeek) % 7) + ((week - 1) * 7);
    }

    static string Offset(TimeSpan value) => (value < TimeSpan.Zero ? "-" : "+") +
        value.Duration().ToString("hh\\:mm", CultureInfo.InvariantCulture).Replace(":", string.Empty, StringComparison.Ordinal);

    static bool TryResolve(string ianaTimeZoneId, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException) when (TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaTimeZoneId, out var windowsId))
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                return true;
            }
            catch (TimeZoneNotFoundException) { }
        }
        catch (InvalidTimeZoneException) { }
        zone = null!;
        return false;
    }
}
