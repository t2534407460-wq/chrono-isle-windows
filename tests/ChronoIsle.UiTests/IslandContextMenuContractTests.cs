using System.IO;

namespace ChronoIsle.UiTests;

public sealed class IslandContextMenuContractTests
{
    [Fact]
    public void ContextMenu_NearTaskbarUsesAbovePlacementAndIslandVisuals()
    {
        var workspace = FindWorkspace();
        var code = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml"));

        Assert.Contains("ShouldPlaceQuickActionMenuAboveTaskbar()", code, StringComparison.Ordinal);
        Assert.Contains("PlacementMode.Custom", code, StringComparison.Ordinal);
        Assert.Contains("new System.Windows.Point(left, -popupSize.Height - 6)", code, StringComparison.Ordinal);
        Assert.Contains("ContextMenuTaskbarProximity", code, StringComparison.Ordinal);
        Assert.Contains("StartsQuickActionGroup(action)", code, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IslandContextMenu\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IslandContextMenuItem\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{DynamicResource Brush.Island}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{DynamicResource Brush.Stroke}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{DynamicResource Brush.Control}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextMenu_ContainsCurrentDashboardAndSettingsActions()
    {
        var code = IslandCode();

        foreach (var action in new[]
                 {
                     "ViewToday", "ViewCalendar", "ViewStatus", "ViewMedia", "QuickAsk",
                     "ManageItems", "Settings", "PauseReminders", "ToggleDoNotDisturb",
                     "ToggleTopDockAutoFold"
                 })
            Assert.Contains($"IslandQuickAction.{action}", code, StringComparison.Ordinal);

        foreach (var label in new[]
                 {
                     "今天", "月历", "状态", "音乐", "快问", "事项管理", "设置",
                     "暂停提醒", "勿扰模式", "顶部吸附自动收缩"
                 })
            Assert.Contains(label, code, StringComparison.Ordinal);

        Assert.Contains(
            "case IslandQuickAction.ViewToday: Expand(); ShowTodayDashboard(); break;",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "case IslandQuickAction.ViewCalendar: Expand(); ShowCalendarDashboard(); break;",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "case IslandQuickAction.ViewStatus: Expand(); ShowTelemetryDashboard(); break;",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "case IslandQuickAction.ViewMedia: Expand(); ShowMediaDashboard(); break;",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "case IslandQuickAction.Settings: OpenSettings(); break;",
            code,
            StringComparison.Ordinal);
        Assert.Contains("case IslandQuickAction.QuickAsk:", code, StringComparison.Ordinal);
        Assert.Contains("ShowQuickAskDashboard();", code, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.BeginInvoke(QuickAskInput.Focus);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextMenu_RefreshesCheckableActionsWhenOpened()
    {
        var code = IslandCode();

        Assert.Contains("Tag = action", code, StringComparison.Ordinal);
        Assert.Contains(
            "IsCheckable = action is IslandQuickAction.ToggleDoNotDisturb or IslandQuickAction.ToggleTopDockAutoFold",
            code,
            StringComparison.Ordinal);
        Assert.Contains("menu.Items.OfType<MenuItem>()", code, StringComparison.Ordinal);
        Assert.Contains("item.Tag is not IslandQuickAction action", code, StringComparison.Ordinal);
        Assert.Contains(
            "IslandQuickAction.ToggleDoNotDisturb => reminders.IsDoNotDisturbEnabled",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "IslandQuickAction.ToggleTopDockAutoFold => currentPreferences.IslandTopDockAutoFold",
            code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ContextMenu_UsesFixedIconColumnForAlignedLabels()
    {
        var code = IslandCode();

        Assert.Contains("Header = CreateQuickActionHeader(action)", code, StringComparison.Ordinal);
        Assert.Contains("new ColumnDefinition { Width = new GridLength(22) }", code, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(label, 1);", code, StringComparison.Ordinal);
        Assert.Contains("VerticalAlignment = VerticalAlignment.Center", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextMenu_CheckableActionsShowClearCheckedState()
    {
        var workspace = FindWorkspace();
        var xaml = File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml"));

        Assert.Contains("x:Name=\"CheckBoxSurface\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CheckMark\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Trigger Property=\"IsCheckable\" Value=\"True\">", xaml, StringComparison.Ordinal);
        Assert.Contains("<Trigger Property=\"IsChecked\" Value=\"True\">", xaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"Visibility\" Value=\"Visible\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{DynamicResource Brush.AccentSoft}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{DynamicResource Brush.Accent}\"", xaml, StringComparison.Ordinal);
    }

    static string IslandCode()
    {
        var workspace = FindWorkspace();
        return File.ReadAllText(Path.Combine(
            workspace,
            "src",
            "ChronoIsle.App",
            "Views",
            "LifeIslandWindow.xaml.cs"));
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}
