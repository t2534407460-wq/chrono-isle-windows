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
            ["Brush.Window"] = "#0C0E10",
            ["Brush.Island"] = "#07090A",
            ["Brush.Card"] = "#14181B",
            ["Brush.Surface"] = "#1B2125",
            ["Brush.Control"] = "#242C31",
            ["Brush.Hover"] = "#303A41",
            ["Brush.TextPrimary"] = "#F2F5F7",
            ["Brush.TextSecondary"] = "#BAC5CE",
            ["Brush.TextTertiary"] = "#96A4AF",
            ["Brush.Accent"] = "#39C98B",
            ["Brush.AccentSoft"] = "#18392C",
            ["Brush.Success"] = "#39C98B",
            ["Brush.Warning"] = "#DFAF57",
            ["Brush.Danger"] = "#E76C72",
            ["Brush.DangerSoft"] = "#352025",
            ["Brush.Reminder"] = "#A99BCD",
            ["Brush.Stroke"] = "#303A40",
            ["Brush.StrokeSoft"] = "#242D33"
        };

    static readonly IReadOnlyDictionary<string, string> LightPalette =
        new Dictionary<string, string>
        {
            ["Brush.Window"] = "#EAF0F4",
            ["Brush.Island"] = "#FFFFFF",
            ["Brush.Card"] = "#FFFFFF",
            ["Brush.Surface"] = "#F2F5F8",
            ["Brush.Control"] = "#E4EBF0",
            ["Brush.Hover"] = "#D8E3EB",
            ["Brush.TextPrimary"] = "#1C2933",
            ["Brush.TextSecondary"] = "#4E606E",
            ["Brush.TextTertiary"] = "#566873",
            ["Brush.Accent"] = "#168A5F",
            ["Brush.AccentSoft"] = "#D9F1E7",
            ["Brush.Success"] = "#137F57",
            ["Brush.Warning"] = "#A66C14",
            ["Brush.Danger"] = "#B63843",
            ["Brush.DangerSoft"] = "#F9E5E7",
            ["Brush.Reminder"] = "#6F5BA7",
            ["Brush.Stroke"] = "#C5D1DA",
            ["Brush.StrokeSoft"] = "#CDD8E0"
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
        ApplyColor(application, "Brush.OnAccent", theme == AppThemeMode.Light ? "#FFFFFF" : "#07090A");
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
