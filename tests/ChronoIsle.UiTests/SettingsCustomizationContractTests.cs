using System.IO;

namespace ChronoIsle.UiTests;

public sealed class SettingsCustomizationContractTests
{
    [Fact]
    public void Settings_ProvidesNineAccentSchemesAndSevenCollapsedControlOptions()
    {
        var (xaml, _) = SettingsFiles();

        Assert.Contains("x:Name=\"AccentSchemeSelector\"", xaml, StringComparison.Ordinal);
        foreach (var scheme in new[]
                 {
                     "Emerald", "OceanBlue", "Violet", "Amber", "Rose",
                     "Cyan", "OrangeRed", "Black", "White"
                 })
            Assert.Contains($"Tag=\"{scheme}\"", xaml, StringComparison.Ordinal);

        foreach (var option in new[]
                 {
                     "IslandShowMascot", "IslandShowStatusLight", "IslandShowAgendaSummary",
                     "IslandShowNetworkSpeed", "IslandShowNetworkStatus", "IslandShowClock",
                     "IslandShowExpandIndicator"
                 })
            Assert.Contains($"x:Name=\"{option}\"", xaml, StringComparison.Ordinal);

        Assert.Contains("SelectionChanged=\"ThemeSelection_Changed\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CollapsedControlOptions_UseCurrentIslandVisualsWithoutVisibleLabels()
    {
        var (xaml, _) = SettingsFiles();
        var workspace = FindWorkspace();
        var controls = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Resources", "Controls.xaml"));

        Assert.Contains("x:Key=\"IslandVisualOption\"", xaml, StringComparison.Ordinal);
        var optionsStart = xaml.IndexOf(
            "<Border x:Name=\"CollapsedIslandOptions\"",
            StringComparison.Ordinal);
        var optionsEnd = xaml.IndexOf(
            "<CheckBox x:Name=\"TelemetryEnabled\"",
            optionsStart,
            StringComparison.Ordinal);
        Assert.True(optionsStart >= 0 && optionsEnd > optionsStart);
        var options = xaml[optionsStart..optionsEnd];

        Assert.Contains("Background=\"{DynamicResource Brush.Island}\"", options, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{DynamicResource Brush.Stroke}\"", options, StringComparison.Ordinal);
        Assert.Contains("Assets/island-mascot.png", options, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Icon.NetworkGlobe\"", controls, StringComparison.Ordinal);
        Assert.Contains("Data=\"{StaticResource Icon.NetworkGlobe}\"", options, StringComparison.Ordinal);
        Assert.DoesNotContain("M 3,8 L 7.5,13 L 10,3 L 15,8", options, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IslandVisualOption}\"", options, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"吉祥物\"", options, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"时间\"", options, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"吉祥物\"", options, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"事项状态点\"", options, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"事项与状态摘要\"", options, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"实时网速\"", options, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"网络状态点\"", options, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"时间\"", options, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"展开箭头\"", options, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_PreviewsThemeAndRestoresUnsavedSelectionWhenClosed()
    {
        var (_, source) = SettingsFiles();

        Assert.Contains("ThemeService theme", source, StringComparison.Ordinal);
        Assert.Contains("committedPreferences", source, StringComparison.Ordinal);
        Assert.Contains("theme.Preview(", source, StringComparison.Ordinal);
        Assert.Contains("void ThemeSelection_Changed", source, StringComparison.Ordinal);
        Assert.Contains("void LifeSettingsWindow_Closing", source, StringComparison.Ordinal);
        Assert.Contains("ThemeMode = ThemeModeSelector.SelectedValue", source, StringComparison.Ordinal);
        Assert.Contains("AccentScheme = AccentSchemeSelector.SelectedValue", source, StringComparison.Ordinal);
        Assert.Contains("IslandShowMascot = IslandShowMascot.IsChecked == true", source, StringComparison.Ordinal);
        Assert.Contains("IslandShowExpandIndicator = IslandShowExpandIndicator.IsChecked == true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_SaveConfirmsSuccessAndClosesTheWindow()
    {
        var (xaml, source) = SettingsFiles();
        var start = source.IndexOf("void Save_Click", StringComparison.Ordinal);
        var end = source.IndexOf("void Cancel_Click", start, StringComparison.Ordinal);
        var saveHandler = source[start..end];

        Assert.Contains("x:Name=\"SaveSuccessToast\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CancelButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SaveButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{DynamicResource Brush.Surface}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{DynamicResource Brush.Accent}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{DynamicResource Brush.AccentSoft}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"设置已保存\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"更改已应用\"", xaml, StringComparison.Ordinal);
        Assert.Contains("async void Save_Click", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Windows.MessageBox.Show(", saveHandler, StringComparison.Ordinal);
        Assert.Contains("SaveSuccessToast.Visibility = Visibility.Visible;", saveHandler, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(900);", saveHandler, StringComparison.Ordinal);
        Assert.Contains("Close();", saveHandler, StringComparison.Ordinal);
        Assert.True(
            saveHandler.IndexOf("SaveSuccessToast.Visibility = Visibility.Visible;", StringComparison.Ordinal) <
            saveHandler.IndexOf("await Task.Delay(900);", StringComparison.Ordinal));
        Assert.True(
            saveHandler.IndexOf("await Task.Delay(900);", StringComparison.Ordinal) <
            saveHandler.IndexOf("Close();", StringComparison.Ordinal));
    }

    static (string Xaml, string Source) SettingsFiles()
    {
        var workspace = FindWorkspace();
        return (
            File.ReadAllText(Path.Combine(
                workspace, "src", "ChronoIsle.App", "Views", "LifeSettingsWindow.xaml")),
            File.ReadAllText(Path.Combine(
                workspace, "src", "ChronoIsle.App", "Views", "LifeSettingsWindow.xaml.cs")));
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}
