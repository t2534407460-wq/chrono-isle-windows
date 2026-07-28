using Microsoft.Win32;
using System.Windows.Media;

namespace ChronoIsle.App.Services;

public enum AppThemeMode
{
    System,
    Dark,
    Light
}

public enum AppAccentScheme
{
    Emerald,
    OceanBlue,
    Violet,
    Amber,
    Rose,
    Cyan,
    OrangeRed,
    Black,
    White
}

public sealed class ThemeService : IDisposable
{
    static readonly IReadOnlyDictionary<string, string> DarkPalette =
        new Dictionary<string, string>
        {
            ["Brush.Window"] = "#101214",
            ["Brush.Island"] = "#090B0C",
            ["Brush.Card"] = "#181B1D",
            ["Brush.Surface"] = "#212528",
            ["Brush.Control"] = "#292E32",
            ["Brush.Hover"] = "#343A3F",
            ["Brush.TextPrimary"] = "#F3F5F4",
            ["Brush.TextSecondary"] = "#B7BFBB",
            ["Brush.TextTertiary"] = "#7D8782",
            ["Brush.Accent"] = "#39C98B",
            ["Brush.AccentSoft"] = "#18392C",
            ["Brush.Success"] = "#39C98B",
            ["Brush.Warning"] = "#DFAF57",
            ["Brush.Danger"] = "#E76C72",
            ["Brush.Reminder"] = "#A99BCD",
            ["Brush.Stroke"] = "#38403C",
            ["Brush.StrokeSoft"] = "#242A27"
        };

    static readonly IReadOnlyDictionary<string, string> LightPalette =
        new Dictionary<string, string>
        {
            ["Brush.Window"] = "#F2F5F3",
            ["Brush.Island"] = "#FFFFFF",
            ["Brush.Card"] = "#FFFFFF",
            ["Brush.Surface"] = "#E7ECE9",
            ["Brush.Control"] = "#DDE5E1",
            ["Brush.Hover"] = "#D2DDD7",
            ["Brush.TextPrimary"] = "#151A17",
            ["Brush.TextSecondary"] = "#4D5953",
            ["Brush.TextTertiary"] = "#78847E",
            ["Brush.Accent"] = "#168A5F",
            ["Brush.AccentSoft"] = "#D9F1E7",
            ["Brush.Success"] = "#168A5F",
            ["Brush.Warning"] = "#A66C14",
            ["Brush.Danger"] = "#C54850",
            ["Brush.Reminder"] = "#6F5BA7",
            ["Brush.Stroke"] = "#BAC6C0",
            ["Brush.StrokeSoft"] = "#DDE4E0"
        };

    readonly LifePreferencesService preferences;
    bool disposed;

    public ThemeService(LifePreferencesService preferences) => this.preferences = preferences;

    public AppThemeMode EffectiveTheme { get; private set; } = AppThemeMode.Dark;
    public AppAccentScheme EffectiveAccentScheme { get; private set; } = AppAccentScheme.Emerald;
    public event Action<AppThemeMode>? ThemeChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        preferences.Changed += Preferences_Changed;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        ApplyFromPreferences();
    }

    void Preferences_Changed() => ApplyFromPreferences();

    void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Parse(preferences.Load().ThemeMode) == AppThemeMode.System)
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(ApplyFromPreferences);
    }

    public void ApplyFromPreferences()
    {
        var saved = preferences.Load();
        Preview(Parse(saved.ThemeMode), ParseAccent(saved.AccentScheme));
    }

    public void Preview(AppThemeMode requestedTheme, AppAccentScheme accentScheme) =>
        Apply(requestedTheme == AppThemeMode.System ? ReadSystemTheme() : requestedTheme, accentScheme);

    public void Apply(AppThemeMode theme) => Apply(theme, EffectiveAccentScheme);

    public void Apply(AppThemeMode theme, AppAccentScheme accentScheme)
    {
        var application = System.Windows.Application.Current;
        if (application is null) return;
        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.BeginInvoke(() => Apply(theme, accentScheme));
            return;
        }

        var palette = theme == AppThemeMode.Light ? LightPalette : DarkPalette;
        foreach (var (key, value) in palette)
        {
            if (application.TryFindResource(key) is not SolidColorBrush brush) continue;
            var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
            if (brush.IsFrozen) application.Resources[key] = new SolidColorBrush(color);
            else brush.Color = color;
        }
        var accent = AccentColors(theme, accentScheme);
        ApplyColor(application, "Brush.Accent", accent.Accent);
        ApplyColor(application, "Brush.AccentSoft", accent.AccentSoft);
        EffectiveTheme = theme;
        EffectiveAccentScheme = accentScheme;
        ThemeChanged?.Invoke(theme);
    }

    static void ApplyColor(System.Windows.Application application, string key, string value)
    {
        if (application.TryFindResource(key) is not SolidColorBrush brush) return;
        var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
        if (brush.IsFrozen) application.Resources[key] = new SolidColorBrush(color);
        else brush.Color = color;
    }

    internal static AppThemeMode Parse(string? value) =>
        Enum.TryParse<AppThemeMode>(value, true, out var mode) ? mode : AppThemeMode.System;

    internal static AppAccentScheme ParseAccent(string? value) =>
        Enum.TryParse<AppAccentScheme>(value, true, out var scheme) ? scheme : AppAccentScheme.Emerald;

    internal static (string Accent, string AccentSoft) AccentColors(
        AppThemeMode theme,
        AppAccentScheme scheme) =>
        (theme, scheme) switch
        {
            (AppThemeMode.Light, AppAccentScheme.Emerald) => ("#137F57", "#D9F1E7"),
            (AppThemeMode.Light, AppAccentScheme.OceanBlue) => ("#2468B4", "#DCEBFA"),
            (AppThemeMode.Light, AppAccentScheme.Violet) => ("#7057B8", "#EAE3FA"),
            (AppThemeMode.Light, AppAccentScheme.Amber) => ("#9A650D", "#F7E9C8"),
            (AppThemeMode.Light, AppAccentScheme.Rose) => ("#B83262", "#F8DDE7"),
            (AppThemeMode.Light, AppAccentScheme.Cyan) => ("#137F7C", "#D6F2F1"),
            (AppThemeMode.Light, AppAccentScheme.OrangeRed) => ("#C9482D", "#F8DED7"),
            (AppThemeMode.Light, AppAccentScheme.Black) => ("#202326", "#DADFDB"),
            (AppThemeMode.Light, AppAccentScheme.White) => ("#6D7470", "#E3E7E5"),
            (_, AppAccentScheme.OceanBlue) => ("#58A6FF", "#172F4A"),
            (_, AppAccentScheme.Violet) => ("#B39DFF", "#2C244A"),
            (_, AppAccentScheme.Amber) => ("#F0B44C", "#473419"),
            (_, AppAccentScheme.Rose) => ("#F07FA2", "#4A2230"),
            (_, AppAccentScheme.Cyan) => ("#4DD4D0", "#173D3C"),
            (_, AppAccentScheme.OrangeRed) => ("#FF8266", "#4A281F"),
            (_, AppAccentScheme.Black) => ("#9CA3A0", "#2B2F2D"),
            (_, AppAccentScheme.White) => ("#F5F7F6", "#303634"),
            _ => ("#39C98B", "#18392C")
        };

    static AppThemeMode ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0
                ? AppThemeMode.Light
                : AppThemeMode.Dark;
        }
        catch { return AppThemeMode.Dark; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        preferences.Changed -= Preferences_Changed;
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
    }
}
