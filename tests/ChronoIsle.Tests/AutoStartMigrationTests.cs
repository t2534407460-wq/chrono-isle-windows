using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class AutoStartMigrationTests : IDisposable
{
    private readonly AutoStartTestContext context = new();

    [Theory]
    [InlineData(AutoStartService.ValueName)]
    [InlineData(AutoStartService.LegacyValueName)]
    public void Initialize_moves_existing_Run_entry_to_current_executable_task(string valueName)
    {
        context.Registry.Write(valueName, "\"C:\\Old\\OpenIsland.exe\"");

        context.Service.Initialize();

        Assert.True(context.Service.IsEnabled);
        Assert.Null(context.Registry.Read(AutoStartService.LegacyValueName));
        Assert.Null(context.Registry.Read(AutoStartService.ValueName));
        Assert.Contains("ChronoIsle.exe", context.Task.Definition);
    }

    [Fact]
    public void Failed_migration_preserves_original_setting_and_allows_application_to_open()
    {
        context.Registry.Write(AutoStartService.LegacyValueName, "legacy");
        context.Task.RegisterError = new UnauthorizedAccessException("access denied");

        context.Service.Initialize();

        Assert.Equal("legacy", context.Registry.Read(AutoStartService.LegacyValueName));
        Assert.Contains("无法迁移", context.Service.Status);
        Assert.Contains("access denied", context.Service.Status);
        Assert.False(context.Service.IsEnabled);
    }

    [Fact]
    public void Initialize_does_not_enable_startup_when_previously_disabled()
    {
        context.Service.Initialize();

        Assert.Null(context.Task.Definition);
        Assert.False(context.Service.IsEnabled);
    }

    [Fact]
    public void Initialize_does_not_overwrite_task_after_Run_migration_is_complete()
    {
        context.Service.SetEnabled(true);
        var definition = context.Task.Definition!.Replace("<Enabled>true</Enabled>", "<Enabled>false</Enabled>");
        context.Task.Definition = definition;

        context.Service.Initialize();

        Assert.Equal(definition, context.Task.Definition);
        Assert.False(context.Service.IsEnabled);
    }

    public void Dispose() => context.Dispose();
}
