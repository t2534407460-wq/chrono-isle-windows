using System.IO;

namespace OpenIsland.UiTests;

public sealed class AutomationIdContractTests
{
    [Theory]
    [InlineData("src/OpenIsland.App/Views/LifeMainWindow.xaml", "LifeMainWindow", "ChatInput", "ChatSendButton", "PendingActionCard", "ConfirmPendingActionButton", "CancelPendingActionButton")]
    [InlineData("src/OpenIsland.App/Views/LifeIslandWindow.xaml", "LifeIslandWindow", "IslandExpandButton", "IslandQuickAddInput", "IslandQuickAddButton")]
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
            if (File.Exists(Path.Combine(directory.FullName, "OpenIsland.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("OpenIsland.sln was not found from the UI test host.");
    }
}