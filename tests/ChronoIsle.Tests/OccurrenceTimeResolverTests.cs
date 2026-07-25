using ChronoIsle.App.Services.Domain;

namespace ChronoIsle.Tests;

public sealed class OccurrenceTimeResolverTests
{
    [Fact]
    public void ResolveRecurringOccurrence_DstGap_MovesToNextValidLocalTime()
    {
        var catalog = new SystemTimeZoneCatalog();
        if (!catalog.TryResolveIana("America/New_York", out _, out _)) return;
        var resolver = new OccurrenceTimeResolver(catalog);

        var resolved = resolver.ResolveRecurringOccurrence(new DateTime(2026, 3, 8, 2, 30, 0), "America/New_York");

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), resolved);
    }

    [Fact]
    public void ResolveRecurringOccurrence_DstOverlap_UsesEarlierInstant()
    {
        var catalog = new SystemTimeZoneCatalog();
        if (!catalog.TryResolveIana("America/New_York", out _, out _)) return;
        var resolver = new OccurrenceTimeResolver(catalog);

        var resolved = resolver.ResolveRecurringOccurrence(new DateTime(2026, 11, 1, 1, 30, 0), "America/New_York");

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), resolved);
    }

    [Fact]
    public void ResolveSingleLocalTime_AbsoluteInstant_RequiresUtcInstant()
    {
        var resolver = new OccurrenceTimeResolver(new SystemTimeZoneCatalog());
        var invalid = new TemporalValue(null, null, null, null, TimeSemantics.AbsoluteInstant);

        var error = Assert.Throws<InvalidOperationException>(() => resolver.ResolveSingleLocalTime(invalid));

        Assert.Contains("UTC", error.Message);
    }

    [Fact]
    public void TimeZoneCapability_ValidatesRequiredZones()
    {
        var capability = new SystemTimeZoneCatalog().CheckCapabilities();

        Assert.True(capability.IsHealthy, string.Join(Environment.NewLine, capability.Errors));
        Assert.False(string.IsNullOrWhiteSpace(capability.LocalIanaTimeZoneId));
    }
}
