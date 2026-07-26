using Microsoft.Win32;
using System.Windows.Media;

namespace ChronoIsle.App.Services;

public enum AppThemeMode
{
    System,
    Dark,
    Light
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
        var requested = Parse(preferences.Load().ThemeMode);
        Apply(requested == AppThemeMode.System ? ReadSystemTheme() : requested);
    }

    public void Apply(AppThemeMode theme)
    {
        var application = System.Windows.Application.Current;
        if (application is null) return;
        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.BeginInvoke(() => Apply(theme));
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
        EffectiveTheme = theme;
        ThemeChanged?.Invoke(theme);
    }

    static AppThemeMode Parse(string? value) =>
        Enum.TryParse<AppThemeMode>(value, true, out var mode) ? mode : AppThemeMode.System;

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
