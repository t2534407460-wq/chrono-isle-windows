using System.IO;

using System.Diagnostics;
using System.Windows.Automation;
using Xunit.Sdk;

namespace ChronoIsle.UiTests;

public sealed class IslandWindowSmokeTests
{
    [Fact]
    [Trait("Category", "UIA")]
    public void IslandWindow_IsVisibleInAnIsolatedProcess()
    {        if (!string.Equals(Environment.GetEnvironmentVariable("CHRONOISLE_RUN_UIA"), "1", StringComparison.Ordinal)) return;

        var workspace = FindWorkspace();
        var executable = Path.Combine(workspace, "src", "ChronoIsle.App", "bin", "Debug", "net8.0-windows10.0.19041.0", "ChronoIsle.exe");
        Assert.True(File.Exists(executable), $"App executable was not found: {executable}");
        var database = Path.Combine(Path.GetTempPath(), $"chrono-isle-uia-{Guid.NewGuid():N}.db");
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            Environment = { ["CHRONOISLE_DATABASE_PATH"] = database, ["CHRONOISLE_UI_TEST_MODE"] = "1" }
        })!;
        try
        {
            var found = SpinWait.SpinUntil(() => FindWindow(process.Id) is not null, TimeSpan.FromSeconds(15));
            Assert.True(found, "The Island window did not become visible within 15 seconds.");
        }
        finally
        {            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            foreach (var candidate in new[] { database, database + "-wal", database + "-shm" })
            {
                try { if (File.Exists(candidate)) File.Delete(candidate); }
                catch (IOException) { }
            }
        }
    }

    static AutomationElement? FindWindow(int processId) => AutomationElement.RootElement.FindFirst(TreeScope.Children,
        new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "LifeIslandWindow")));

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}
