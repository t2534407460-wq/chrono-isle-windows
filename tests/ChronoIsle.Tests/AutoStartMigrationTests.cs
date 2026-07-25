using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class AutoStartMigrationTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "chronoisle-autostart-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Constructor_moves_legacy_autostart_to_current_executable()
    {
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "ChronoIsle.exe");
        File.WriteAllText(executable, "");
        var registry = new MemoryRegistry();
        registry.Write(AutoStartService.LegacyValueName, "\"C:\\Old\\OpenIsland.exe\"");

        var service = new AutoStartService(registry, () => executable);

        Assert.True(service.IsEnabled);
        Assert.Null(registry.Read(AutoStartService.LegacyValueName));
        Assert.Equal($"\"{executable}\"", registry.Read(AutoStartService.ValueName));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    sealed class MemoryRegistry : IAutoStartRegistry
    {
        readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

        public string? Read(string name) => values.TryGetValue(name, out var value) ? value : null;

        public void Write(string name, string command) => values[name] = command;

        public void Remove(string name) => values.Remove(name);
    }
}
