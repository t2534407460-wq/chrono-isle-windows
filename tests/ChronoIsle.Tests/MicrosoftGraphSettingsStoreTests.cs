using ChronoIsle.App.Services.Sync;

namespace ChronoIsle.Tests;

public sealed class MicrosoftGraphSettingsStoreTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "chrono-isle-graph-settings-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Stores_only_public_registration_and_account_metadata()
    {
        var path = Path.Combine(directory, "settings.json");
        var store = new MicrosoftGraphSettingsStore(path);
        store.Save(new("11111111-1111-1111-1111-111111111111", "common", "home-id", "owner@example.com"));

        var restored = store.Load();

        Assert.Equal("11111111-1111-1111-1111-111111111111", restored.ClientId);
        Assert.Equal("common", restored.TenantId);
        Assert.Equal("home-id", restored.AccountId);
        Assert.DoesNotContain("token", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
