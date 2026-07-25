using System.IO;

namespace ChronoIsle.UiTests;

public sealed class AutomationIdContractTests
{
    [Theory]
    [InlineData("src/ChronoIsle.App/Views/LifeMainWindow.xaml", "LifeMainWindow", "ChatInput", "ChatSendButton", "PendingActionCard", "ConfirmPendingActionButton", "CancelPendingActionButton")]
    [InlineData("src/ChronoIsle.App/Views/LifeIslandWindow.xaml", "LifeIslandWindow", "IslandExpandButton", "IslandQuickAddInput", "IslandQuickAddButton")]
    public void CriticalAutomationIds_AreStable(string relativePath, params string[] automationIds)
    {
        var workspace = FindWorkspace();
        var xaml = File.ReadAllText(Path.Combine(workspace, relativePath));

        foreach (var automationId in automationIds)
            Assert.Contains($"AutomationProperties.AutomationId=\"{automationId}\"", xaml, StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}