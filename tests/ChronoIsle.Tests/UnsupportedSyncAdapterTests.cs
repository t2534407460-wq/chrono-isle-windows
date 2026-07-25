using ChronoIsle.App.Services.Sync;

namespace ChronoIsle.Tests;

public sealed class UnsupportedSyncAdapterTests
{
    [Fact]
    public async Task Unsupported_is_distinct_from_an_unconfigured_account()
    {
        ISyncAdapter adapter = new UnsupportedSyncAdapter(
            SyncProvider.OutlookCalendar, "The recurrence rule cannot be represented losslessly.");

        var result = await adapter.PushAsync(new("account", "item", 4));

        Assert.Equal(SyncAdapterOutcome.Unsupported, result.Outcome);
        Assert.Contains("cannot be represented", result.Message, StringComparison.Ordinal);
    }
}
