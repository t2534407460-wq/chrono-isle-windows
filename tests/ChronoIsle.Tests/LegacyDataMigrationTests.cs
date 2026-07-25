using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class LegacyDataMigrationTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "chronoisle-migration-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Run_copies_missing_legacy_files_without_overwriting_current_data()
    {
        var roaming = Path.Combine(root, "Roaming");
        var local = Path.Combine(root, "Local");
        var legacyRoaming = Path.Combine(roaming, LegacyDataMigration.LegacyDirectoryName);
        var currentRoaming = Path.Combine(roaming, LegacyDataMigration.CurrentDirectoryName);
        var legacyLocal = Path.Combine(local, LegacyDataMigration.LegacyDirectoryName);

        Directory.CreateDirectory(legacyRoaming);
        Directory.CreateDirectory(currentRoaming);
        Directory.CreateDirectory(legacyLocal);
        File.WriteAllText(Path.Combine(legacyRoaming, "life-preferences.json"), "legacy");
        File.WriteAllText(Path.Combine(currentRoaming, "life-preferences.json"), "current");
        File.WriteAllText(Path.Combine(legacyLocal, "settings.json"), "local");

        LegacyDataMigration.Run(roaming, local);

        Assert.Equal("current", File.ReadAllText(Path.Combine(currentRoaming, "life-preferences.json")));
        Assert.Equal("local", File.ReadAllText(Path.Combine(local, LegacyDataMigration.CurrentDirectoryName, "settings.json")));
        Assert.True(Directory.Exists(legacyRoaming));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
