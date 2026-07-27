using System.Text.Json;
using ChronoIsle.App;
using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class ThemePreferencesTests
{
    [Fact]
    public void LegacyPreferences_DefaultToEmeraldAndAgendaOnlyTelemetrySummary()
    {
        const string json = """{"WindowsNotifications":true,"AssistantPersona":"Direct"}""";

        var preferences = JsonSerializer.Deserialize<LifePreferences>(json);

        Assert.NotNull(preferences);
        Assert.Equal("Emerald", preferences.AccentScheme);
        Assert.True(preferences.IslandShowMascot);
        Assert.True(preferences.IslandShowStatusLight);
        Assert.True(preferences.IslandShowAgendaSummary);
        Assert.False(preferences.IslandShowNetworkSpeed);
        Assert.False(preferences.IslandShowNetworkStatus);
        Assert.False(preferences.IslandShowCpuUsage);
        Assert.False(preferences.IslandShowMemoryUsage);
        Assert.True(preferences.IslandShowClock);
        Assert.True(preferences.IslandShowExpandIndicator);
        Assert.True(preferences.IslandTopDockAutoFold);
    }

    [Theory]
    [InlineData("Emerald", AppAccentScheme.Emerald)]
    [InlineData("OceanBlue", AppAccentScheme.OceanBlue)]
    [InlineData("Violet", AppAccentScheme.Violet)]
    [InlineData("Amber", AppAccentScheme.Amber)]
    [InlineData("Rose", AppAccentScheme.Rose)]
    [InlineData("Cyan", AppAccentScheme.Cyan)]
    [InlineData("OrangeRed", AppAccentScheme.OrangeRed)]
    [InlineData("Black", AppAccentScheme.Black)]
    [InlineData("White", AppAccentScheme.White)]
    [InlineData("unknown", AppAccentScheme.Emerald)]
    [InlineData(null, AppAccentScheme.Emerald)]
    public void AccentSchemeParsing_RecognizesNineSchemesAndFallsBackToEmerald(
        string? value,
        AppAccentScheme expected)
    {
        Assert.Equal(expected, ThemeService.ParseAccent(value));
    }

    [Fact]
    public void MonochromeSchemes_UseAdaptiveHighContrastColors()
    {
        var darkBlack = ThemeService.AccentColors(AppThemeMode.Dark, AppAccentScheme.Black);
        var lightWhite = ThemeService.AccentColors(AppThemeMode.Light, AppAccentScheme.White);
        var lightBlack = ThemeService.AccentColors(AppThemeMode.Light, AppAccentScheme.Black);
        var darkWhite = ThemeService.AccentColors(AppThemeMode.Dark, AppAccentScheme.White);

        Assert.Equal("#9CA3A0", darkBlack.Accent);
        Assert.Equal("#6D7470", lightWhite.Accent);
        Assert.Equal("#202326", lightBlack.Accent);
        Assert.Equal("#F5F7F6", darkWhite.Accent);
    }

    [Theory]
    [InlineData(AppAccentScheme.Emerald)]
    [InlineData(AppAccentScheme.OceanBlue)]
    [InlineData(AppAccentScheme.Violet)]
    [InlineData(AppAccentScheme.Amber)]
    [InlineData(AppAccentScheme.Rose)]
    [InlineData(AppAccentScheme.Cyan)]
    [InlineData(AppAccentScheme.OrangeRed)]
    [InlineData(AppAccentScheme.Black)]
    [InlineData(AppAccentScheme.White)]
    public void AccentFilledControls_MeetNormalTextContrast(AppAccentScheme scheme)
    {
        var lightAccent = ThemeService.AccentColors(AppThemeMode.Light, scheme).Accent;
        var darkAccent = ThemeService.AccentColors(AppThemeMode.Dark, scheme).Accent;

        Assert.True(ContrastRatio(lightAccent, "#FFFFFF") >= 4.5, $"{scheme} light contrast");
        Assert.True(ContrastRatio(darkAccent, "#090B0C") >= 4.5, $"{scheme} dark contrast");
    }

    static double ContrastRatio(string foreground, string background)
    {
        var lighter = Math.Max(Luminance(foreground), Luminance(background));
        var darker = Math.Min(Luminance(foreground), Luminance(background));
        return (lighter + 0.05) / (darker + 0.05);
    }

    static double Luminance(string color)
    {
        var channels = new[]
        {
            Convert.ToInt32(color.Substring(1, 2), 16) / 255d,
            Convert.ToInt32(color.Substring(3, 2), 16) / 255d,
            Convert.ToInt32(color.Substring(5, 2), 16) / 255d
        };
        for (var index = 0; index < channels.Length; index++)
            channels[index] = channels[index] <= 0.04045
                ? channels[index] / 12.92
                : Math.Pow((channels[index] + 0.055) / 1.055, 2.4);
        return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
    }
}
