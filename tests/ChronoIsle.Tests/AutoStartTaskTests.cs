using System.Xml.Linq;
using System.Security.Principal;
using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class AutoStartTaskTests : IDisposable
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private readonly AutoStartTestContext context = new();

    [Fact]
    public void Enabling_registers_interactive_elevated_task_instead_of_Run_entry()
    {
        context.Service.SetEnabled(true);

        Assert.True(context.Service.IsEnabled);
        Assert.Null(context.Registry.Read(AutoStartService.ValueName));
        var root = XDocument.Parse(context.Task.Definition!).Root!;
        var principal = root.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.Equal(AutoStartTestContext.UserId, principal.Element(Ns + "UserId")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(Ns + "LogonType")!.Value);
        Assert.Equal("HighestAvailable", principal.Element(Ns + "RunLevel")!.Value);
        var trigger = root.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!;
        Assert.Equal(AutoStartTestContext.UserId, trigger.Element(Ns + "UserId")!.Value);
        Assert.Equal("PT10S", trigger.Element(Ns + "Delay")!.Value);
        var action = root.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
        Assert.Equal(context.Executable, action.Element(Ns + "Command")!.Value);
        Assert.Equal(Path.GetDirectoryName(context.Executable), action.Element(Ns + "WorkingDirectory")!.Value);
        var settings = root.Element(Ns + "Settings")!;
        foreach (var name in new[] { "DisallowStartIfOnBatteries", "StopIfGoingOnBatteries", "RunOnlyIfNetworkAvailable", "RunOnlyIfIdle" })
            Assert.Equal("false", settings.Element(Ns + name)!.Value);
        Assert.Equal("PT0S", settings.Element(Ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal("IgnoreNew", settings.Element(Ns + "MultipleInstancesPolicy")!.Value);
    }

    [Fact]
    public void Scheduler_normalized_account_name_and_omitted_defaults_are_accepted()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var service = new AutoStartService(context.Registry, context.Task, () => context.Executable, () => identity.User!.Value);
        service.SetEnabled(true);
        var definition = XDocument.Parse(context.Task.Definition!);
        definition.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value = identity.Name;
        definition.Descendants(Ns + "Enabled").Remove();
        context.Task.Definition = definition.ToString();

        Assert.True(service.IsEnabled);
    }

    [Fact]
    public void Disabling_removes_task_and_both_old_Run_entries()
    {
        context.Service.SetEnabled(true);
        context.Registry.Write(AutoStartService.ValueName, "old");
        context.Registry.Write(AutoStartService.LegacyValueName, "legacy");

        context.Service.SetEnabled(false);

        Assert.False(context.Service.IsEnabled);
        Assert.Null(context.Task.Definition);
        Assert.Null(context.Registry.Read(AutoStartService.ValueName));
        Assert.Null(context.Registry.Read(AutoStartService.LegacyValueName));
    }

    [Theory]
    [InlineData("UserId", "S-1-5-21-other")]
    [InlineData("RunLevel", "LeastPrivilege")]
    [InlineData("LogonType", "Password")]
    [InlineData("Command", "C:\\Old\\ChronoIsle.exe")]
    [InlineData("WorkingDirectory", "C:\\Old")]
    [InlineData("Enabled", "false")]
    public void Incompatible_or_disabled_task_is_not_reported_as_enabled(string element, string value)
    {
        context.Service.SetEnabled(true);
        var root = XDocument.Parse(context.Task.Definition!);
        root.Descendants(Ns + element).First().Value = value;
        context.Task.Definition = root.ToString();

        Assert.False(context.Service.IsEnabled);
    }

    [Fact]
    public void Disabled_task_setting_is_not_reported_as_enabled()
    {
        context.Service.SetEnabled(true);
        var root = XDocument.Parse(context.Task.Definition!);
        root.Root!.Element(Ns + "Settings")!.Element(Ns + "Enabled")!.Value = "false";
        context.Task.Definition = root.ToString();

        Assert.False(context.Service.IsEnabled);
    }

    [Fact]
    public void Invalid_task_definition_does_not_crash_settings()
    {
        context.Task.Definition = "invalid xml";

        Assert.False(context.Service.IsEnabled);
        Assert.Contains("无法读取", context.Service.Status);
    }

    [Fact]
    public void Scheduler_read_failure_is_reported_without_crashing_settings()
    {
        context.Task.ReadError = new InvalidOperationException("scheduler unavailable");

        Assert.False(context.Service.IsEnabled);
        Assert.Contains("scheduler unavailable", context.Service.Status);
    }

    [Fact]
    public void Failed_registration_is_reported_and_preserves_previous_Run_entry()
    {
        context.Registry.Write(AutoStartService.ValueName, "old");
        context.Task.RegisterError = new UnauthorizedAccessException("access denied");

        Assert.Throws<UnauthorizedAccessException>(() => context.Service.SetEnabled(true));
        Assert.Equal("old", context.Registry.Read(AutoStartService.ValueName));
        Assert.False(context.Service.IsEnabled);
    }

    [Fact]
    public void Registration_that_does_not_persist_is_not_reported_as_success()
    {
        context.Registry.Write(AutoStartService.ValueName, "old");
        context.Task.IgnoreRegistration = true;

        Assert.Throws<InvalidOperationException>(() => context.Service.SetEnabled(true));
        Assert.Equal("old", context.Registry.Read(AutoStartService.ValueName));
        Assert.False(context.Service.IsEnabled);
    }

    [Fact]
    public void Unsupported_executable_does_not_modify_startup_entries()
    {
        File.Delete(context.Executable);
        context.Registry.Write(AutoStartService.LegacyValueName, "legacy");

        context.Service.Initialize();

        Assert.False(context.Service.IsSupported);
        Assert.False(context.Service.IsEnabled);
        Assert.Throws<InvalidOperationException>(() => context.Service.SetEnabled(true));
        Assert.Equal("legacy", context.Registry.Read(AutoStartService.LegacyValueName));
        Assert.Null(context.Task.Definition);
    }

    public void Dispose() => context.Dispose();
}

internal sealed class AutoStartTestContext : IDisposable
{
    public const string UserId = "S-1-5-21-123-456-789-1001";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "chronoisle startup & " + Guid.NewGuid().ToString("N"));
    public string Executable { get; }
    public AutoStartMemoryRegistry Registry { get; } = new();
    public AutoStartMemoryTask Task { get; } = new();
    public AutoStartService Service { get; }

    public AutoStartTestContext()
    {
        Directory.CreateDirectory(directory);
        Executable = Path.Combine(directory, "ChronoIsle.exe");
        File.WriteAllText(Executable, "");
        Service = new AutoStartService(Registry, Task, () => Executable, () => UserId);
    }

    public void Dispose() => Directory.Delete(directory, true);
}

internal sealed class AutoStartMemoryRegistry : IAutoStartRegistry
{
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
    public string? Read(string name) => values.GetValueOrDefault(name);
    public void Write(string name, string command) => values[name] = command;
    public void Remove(string name) => values.Remove(name);
}

internal sealed class AutoStartMemoryTask : IAutoStartTask
{
    public string? Definition { get; set; }
    public Exception? ReadError { get; set; }
    public Exception? RegisterError { get; set; }
    public bool IgnoreRegistration { get; set; }
    public string? ReadDefinition() => ReadError is { } error ? throw error : Definition;
    public void Register(string definition)
    {
        if (RegisterError is { } error) throw error;
        if (!IgnoreRegistration) Definition = definition;
    }
    public void Remove() => Definition = null;
}
