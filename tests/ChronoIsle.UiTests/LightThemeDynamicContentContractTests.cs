using System.IO;

namespace ChronoIsle.UiTests;

public sealed class LightThemeDynamicContentContractTests
{
    [Fact]
    public void DynamicallyCreatedIslandContent_UsesThemeResourcesInsteadOfDarkStructuralColors()
    {
        var workspace = FindWorkspace();
        var source = File.ReadAllText(Path.Combine(
            workspace, "src", "ChronoIsle.App", "Views", "LifeIslandWindow.xaml.cs"));

        Assert.Contains("static T SetThemeResource<T>(", source, StringComparison.Ordinal);
        Assert.Contains(
            "SetThemeResource(todayPanel, Border.BackgroundProperty, \"Brush.Card\");",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetThemeResource(suggestionCard, Border.BackgroundProperty, \"Brush.Card\");",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetThemeResource(countCard, Border.BackgroundProperty, \"Brush.Surface\");",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetThemeResource(row, Border.BackgroundProperty, \"Brush.Surface\");",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetThemeResource(button, Button.BackgroundProperty, \"Brush.AccentSoft\");",
            source,
            StringComparison.Ordinal);
        Assert.Contains("void Theme_Changed(AppThemeMode _)", source, StringComparison.Ordinal);
        Assert.Contains("Refresh();", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Background = new SolidColorBrush(Color.FromRgb(28, 28, 30))",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Background = new SolidColorBrush(Color.FromRgb(36, 36, 40))",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Background = new SolidColorBrush(Color.FromRgb(44, 44, 46))",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_ => Brushes.White",
            source,
            StringComparison.Ordinal);
    }

    static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ChronoIsle.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("ChronoIsle.sln was not found from the UI test host.");
    }
}
