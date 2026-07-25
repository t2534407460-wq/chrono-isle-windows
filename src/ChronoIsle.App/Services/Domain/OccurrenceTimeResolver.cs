namespace ChronoIsle.App.Services.Domain;

public interface IOccurrenceTimeResolver
{
    DateTimeOffset ResolveSingleLocalTime(TemporalValue value);
    DateTimeOffset ResolveRecurringOccurrence(DateTime localDateTime, string ianaTimeZoneId);
    DateTime ResolveGap(DateTime localDateTime, TimeZoneInfo timeZone);
    DateTimeOffset ResolveOverlap(DateTime localDateTime, TimeZoneInfo timeZone);
}

public sealed class OccurrenceTimeResolver(ITimeZoneCatalog timeZones) : IOccurrenceTimeResolver
{
    public DateTimeOffset ResolveSingleLocalTime(TemporalValue value)
    {
        if (value.Semantics == TimeSemantics.AbsoluteInstant)
            return value.UtcInstant?.ToUniversalTime()
                ?? throw new InvalidOperationException("AbsoluteInstant 必须包含 UTC 时刻。");

        var local = value.LocalDateTime is null
            ? throw new InvalidOperationException("墙上时间必须包含本地日期时间。")
            : DateTime.SpecifyKind(value.LocalDateTime.Value, DateTimeKind.Unspecified);

        var iana = value.Semantics == TimeSemantics.DeviceLocalFloatingWallClock
            ? timeZones.LocalIanaTimeZoneId
            : value.IanaTimeZoneId;
        if (string.IsNullOrWhiteSpace(iana) || !timeZones.TryResolveIana(iana, out var zone, out _ ) || zone is null)
            throw new TimeZoneNotFoundException($"无法解析时区 {iana ?? "<null>"}，未安排事项。");

        return ResolveLocal(local, zone);
    }

    public DateTimeOffset ResolveRecurringOccurrence(DateTime localDateTime, string ianaTimeZoneId)
    {
        if (!timeZones.TryResolveIana(ianaTimeZoneId, out var zone, out _) || zone is null)
            throw new TimeZoneNotFoundException($"无法解析时区 {ianaTimeZoneId}，未生成重复事项。");
        return ResolveLocal(DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified), zone);
    }

    public DateTime ResolveGap(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        var candidate = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        for (var minute = 0; minute < 24 * 60 && timeZone.IsInvalidTime(candidate); minute++)
            candidate = candidate.AddMinutes(1);
        if (timeZone.IsInvalidTime(candidate))
            throw new InvalidOperationException($"无法找到 {timeZone.Id} 中的下一有效本地时间。");
        return candidate;
    }

    public DateTimeOffset ResolveOverlap(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        var offsets = timeZone.GetAmbiguousTimeOffsets(local);
        if (offsets.Length == 0) throw new InvalidOperationException("给定时间不是 DST overlap。");
        // Larger offset yields the earlier UTC instant for the same wall-clock time.
        return new DateTimeOffset(local, offsets.Max()).ToUniversalTime();
    }

    DateTimeOffset ResolveLocal(DateTime localDateTime, TimeZoneInfo timeZone)
    {
        var local = timeZone.IsInvalidTime(localDateTime) ? ResolveGap(localDateTime, timeZone) : localDateTime;
        if (timeZone.IsAmbiguousTime(local)) return ResolveOverlap(local, timeZone);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime();
    }
}
