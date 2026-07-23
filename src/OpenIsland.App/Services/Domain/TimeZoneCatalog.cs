namespace OpenIsland.App.Services.Domain;

public sealed record TimeZoneCapability(
    bool IsHealthy,
    string LocalIanaTimeZoneId,
    IReadOnlyList<string> Errors);

public interface ITimeZoneCatalog
{
    string LocalIanaTimeZoneId { get; }
    TimeZoneCapability CheckCapabilities();
    bool TryResolveIana(string ianaTimeZoneId, out TimeZoneInfo? timeZone, out string? windowsTimeZoneId);
}

public sealed class SystemTimeZoneCatalog : ITimeZoneCatalog
{
    // Versioned fallback derived from the common CLDR Windows/IANA mappings used by this app.
    // Unknown zones are rejected rather than guessed.
    static readonly IReadOnlyDictionary<string, string> FallbackWindowsIds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Etc/UTC"] = "UTC",
            ["UTC"] = "UTC",
            ["Asia/Shanghai"] = "China Standard Time",
            ["Asia/Tokyo"] = "Tokyo Standard Time",
            ["America/New_York"] = "Eastern Standard Time",
            ["Europe/London"] = "GMT Standard Time"
        };

    public string LocalIanaTimeZoneId { get; }

    public SystemTimeZoneCatalog()
    {
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) && !string.IsNullOrWhiteSpace(iana))
            LocalIanaTimeZoneId = iana;
        else if (FallbackWindowsIds.FirstOrDefault(x => string.Equals(x.Value, TimeZoneInfo.Local.Id, StringComparison.OrdinalIgnoreCase)) is var pair && !string.IsNullOrWhiteSpace(pair.Key))
            LocalIanaTimeZoneId = pair.Key;
        else
            LocalIanaTimeZoneId = TimeZoneInfo.Local.Id;
    }

    public TimeZoneCapability CheckCapabilities()
    {
        var errors = new List<string>();
        foreach (var id in new[] { "Etc/UTC", "Asia/Shanghai", "America/New_York" })
            if (!TryResolveIana(id, out _, out _)) errors.Add($"无法解析 IANA 时区 {id}。");

        if (!TryResolveIana(LocalIanaTimeZoneId, out _, out _))
            errors.Add($"无法解析当前系统时区 {LocalIanaTimeZoneId}。");

        return new(errors.Count == 0, LocalIanaTimeZoneId, errors);
    }

    public bool TryResolveIana(string ianaTimeZoneId, out TimeZoneInfo? timeZone, out string? windowsTimeZoneId)
    {
        timeZone = null;
        windowsTimeZoneId = null;
        if (string.IsNullOrWhiteSpace(ianaTimeZoneId)) return false;

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId);
            TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaTimeZoneId, out windowsTimeZoneId);
            windowsTimeZoneId ??= timeZone.Id;
            return true;
        }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaTimeZoneId, out windowsTimeZoneId) || string.IsNullOrWhiteSpace(windowsTimeZoneId))
            FallbackWindowsIds.TryGetValue(ianaTimeZoneId, out windowsTimeZoneId);
        if (string.IsNullOrWhiteSpace(windowsTimeZoneId)) return false;

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(windowsTimeZoneId);
            return true;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}
