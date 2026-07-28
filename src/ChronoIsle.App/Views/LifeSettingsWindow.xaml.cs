using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Domain;
using ChronoIsle.App.Services.Commanding;
using ChronoIsle.App.Services.Reporting;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Sync;
using ChronoIsle.App.Services.Media;

namespace ChronoIsle.App.Views;

public partial class LifeSettingsWindow : Window
{
    readonly ProviderSettingsService settings;
    readonly LifePreferencesService preferences;
    readonly ReminderService reminders;
    readonly OpenAiChatService ai;
    readonly AutoStartService autoStart;
    readonly LyricsService lyrics;
    readonly ThemeService theme;
    readonly SqliteOnlineBackupService backups;
    readonly IcsExportService icsExport;
    readonly IcsImportService icsImport;
    bool loading;
    readonly AuditPrivacyService auditPrivacy;
    readonly string databasePath;
    readonly AssistantCommandPipeline commandPipeline;
    LifePreferences committedPreferences = LifePreferences.Default;
    bool themePreviewDirty;

    public LifeSettingsWindow(ProviderSettingsService settings, LifePreferencesService preferences, ReminderService reminders,
        OpenAiChatService ai, AutoStartService autoStart, LifeDataService data, AssistantCommandPipeline commandPipeline,
        LyricsService lyrics, ThemeService theme)
    {
        InitializeComponent();
        this.settings = settings;
        this.preferences = preferences;
        this.reminders = reminders;
        this.ai = ai;
        this.autoStart = autoStart;
        this.lyrics = lyrics;
        this.theme = theme;
        var provider = settings.Load();
        var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(data.DatabasePath);
        backups = new SqliteOnlineBackupService(runtime.ConnectionFactory, runtime.WriteQueue);
        icsExport = new IcsExportService(runtime.ConnectionFactory);
        icsImport = new IcsImportService(runtime.WriteQueue, new OccurrenceTimeResolver(new SystemTimeZoneCatalog()));
        Url.Text = provider.BaseUrl;
        Model.Text = provider.Model;
        auditPrivacy = new AuditPrivacyService(runtime.WriteQueue);
        this.commandPipeline = commandPipeline;
        databasePath = data.DatabasePath;
        Key.Password = provider.ApiKey;
        loading = true;
        var savedPreferences = preferences.Load();
        committedPreferences = savedPreferences;
        WindowsNotifications.IsChecked = savedPreferences.WindowsNotifications;
        MediaAutoTakeover.IsChecked = savedPreferences.MediaAutoTakeover;
        ThemeModeSelector.SelectedValue = ThemeService.Parse(savedPreferences.ThemeMode).ToString();
        AccentSchemeSelector.SelectedValue = ThemeService.ParseAccent(savedPreferences.AccentScheme).ToString();
        TelemetryEnabled.IsChecked = savedPreferences.TelemetryEnabled;
        ToastInboxEnabled.IsChecked = savedPreferences.ToastInboxEnabled;
        GlowBorderEnabled.IsChecked = savedPreferences.GlowBorderEnabled;
        IslandShowMascot.IsChecked = savedPreferences.IslandShowMascot;
        IslandShowStatusLight.IsChecked = savedPreferences.IslandShowStatusLight;
        IslandShowAgendaSummary.IsChecked = savedPreferences.IslandShowAgendaSummary;
        IslandShowNetworkSpeed.IsChecked = savedPreferences.IslandShowNetworkSpeed;
        IslandShowCpuUsage.IsChecked = savedPreferences.IslandShowCpuUsage;
        IslandShowMemoryUsage.IsChecked = savedPreferences.IslandShowMemoryUsage;
        IslandShowNetworkStatus.IsChecked = savedPreferences.IslandShowNetworkStatus;
        IslandShowClock.IsChecked = savedPreferences.IslandShowClock;
        IslandShowExpandIndicator.IsChecked = savedPreferences.IslandShowExpandIndicator;
        LyricsEnabled.IsChecked = savedPreferences.LyricsEnabled;
        MoveIslandDuringFullscreen.IsChecked = savedPreferences.MoveIslandDuringFullscreen;
        LyricsOffset.Value = savedPreferences.LyricsOffsetMs;
        Persona.SelectedValue = Enum.TryParse<AssistantPersona>(savedPreferences.AssistantPersona, out _) ? savedPreferences.AssistantPersona : "Direct";
        AutoStart.IsEnabled = autoStart.IsSupported;
        AutoStart.IsChecked = autoStart.IsEnabled;
        AutoStartStatus.Text = autoStart.Status;
        loading = false;
        RefreshAuditEntries();
    }

    void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
    ProviderSettings Value() => new(Url.Text.Trim(), Model.Text.Trim(), Key.Password);

    void ThemeSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (loading) return;
        themePreviewDirty = true;
        theme.Preview(
            ThemeService.Parse(ThemeModeSelector.SelectedValue as string),
            ThemeService.ParseAccent(AccentSchemeSelector.SelectedValue as string));
    }

    void LifeSettingsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!themePreviewDirty) return;
        theme.Preview(
            ThemeService.Parse(committedPreferences.ThemeMode),
            ThemeService.ParseAccent(committedPreferences.AccentScheme));
        themePreviewDirty = false;
    }

    void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        try
        {
            autoStart.SetEnabled(AutoStart.IsChecked == true);
            AutoStartStatus.Text = autoStart.Status;
        }
        catch (Exception exception)
        {
            loading = true;
            AutoStart.IsChecked = autoStart.IsEnabled;
            loading = false;
            AutoStartStatus.Text = $"无法更新开机自启：{exception.Message}";
        }
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        settings.Save(Value());
        var current = preferences.Load();
        var updated = current with
        {
            WindowsNotifications = WindowsNotifications.IsChecked == true,
            AssistantPersona = Persona.SelectedValue as string ?? "Direct",
            MediaAutoTakeover = MediaAutoTakeover.IsChecked == true,
            ThemeMode = ThemeModeSelector.SelectedValue as string ?? "System",
            AccentScheme = AccentSchemeSelector.SelectedValue as string ?? "Emerald",
            TelemetryEnabled = TelemetryEnabled.IsChecked == true,
            ToastInboxEnabled = ToastInboxEnabled.IsChecked == true,
            GlowBorderEnabled = GlowBorderEnabled.IsChecked == true,
            IslandShowMascot = IslandShowMascot.IsChecked == true,
            IslandShowStatusLight = IslandShowStatusLight.IsChecked == true,
            IslandShowAgendaSummary = IslandShowAgendaSummary.IsChecked == true,
            IslandShowNetworkSpeed = IslandShowNetworkSpeed.IsChecked == true,
            IslandShowCpuUsage = IslandShowCpuUsage.IsChecked == true,
            IslandShowMemoryUsage = IslandShowMemoryUsage.IsChecked == true,
            IslandShowNetworkStatus = IslandShowNetworkStatus.IsChecked == true,
            IslandShowClock = IslandShowClock.IsChecked == true,
            IslandShowExpandIndicator = IslandShowExpandIndicator.IsChecked == true,
            LyricsEnabled = LyricsEnabled.IsChecked == true,
            LyricsOffsetMs = (int)LyricsOffset.Value,
            MoveIslandDuringFullscreen = MoveIslandDuringFullscreen.IsChecked == true
        };
        preferences.Save(updated);
        committedPreferences = updated;
        themePreviewDirty = false;
        lyrics.Refresh();
        reminders.RefreshSchedule();
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        SaveSuccessToast.Visibility = Visibility.Visible;
        SaveSuccessToast.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        await Task.Delay(900);
        if (IsLoaded) Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();


    void RefreshAudit_Click(object sender, RoutedEventArgs e) => RefreshAuditEntries();

    void RefreshAuditEntries()
    {
        var entries = auditPrivacy.GetRecent();
        AuditEntries.Text = entries.Count == 0
            ? "暂无本地操作记录。"
            : string.Join(Environment.NewLine, entries.Select(entry =>
                $"{entry.CreatedAtUtc.ToLocalTime():MM-dd HH:mm} · {entry.Command} · {entry.Status}"));
    }

    void ClearAudit_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(this,
            "将删除本地操作记录，并脱敏原始输入、解析与确认详情。此操作无法撤销。是否继续？",
            "清理本地审计记录", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        try
        {
            commandPipeline.ClearLocalDetailedAudit();
            var removed = auditPrivacy.DeleteAll();
            RefreshAuditEntries();
            DataResult.Text = $"已清理 {removed} 条本地操作记录。";
        }
        catch (Exception exception) { DataResult.Text = $"清理审计记录失败：{exception.Message}"; }
    }

    void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "创建 ChronoIsle 便携备份",
            Filter = "ChronoIsle 备份 (*.db)|*.db|所有文件 (*.*)|*.*",
            FileName = $"ChronoIsle-{DateTime.Today:yyyyMMdd}.db",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            backups.Create(dialog.FileName, LocalBackupKind.UserPortable);
            DataResult.Text = "备份已创建。外部账户令牌未包含在备份中。";
        }
        catch (Exception exception) { DataResult.Text = $"创建备份失败：{exception.Message}"; }
    }

    void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "恢复 ChronoIsle 便携备份", Filter = "ChronoIsle 备份 (*.db)|*.db|所有文件 (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var confirm = System.Windows.MessageBox.Show(this,
            "恢复会合并备份中的事项到本地数据库；外部账户需要重新登录。是否继续？",
            "恢复便携备份", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        try
        {
            backups.RestorePortableBusinessData(dialog.FileName);
            reminders.RefreshSchedule();
            DataResult.Text = "备份业务数据已恢复，外部账户已标记为需要重新登录。";
        }
        catch (Exception exception) { DataResult.Text = $"恢复备份失败：{exception.Message}"; }
    }

    void ExportIcs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出 ICS 日历",
            Filter = "iCalendar (*.ics)|*.ics|所有文件 (*.*)|*.*",
            FileName = $"ChronoIsle-{DateTime.Today:yyyyMMdd}.ics",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, icsExport.Export());
            DataResult.Text = "ICS 已导出。";
        }
        catch (Exception exception) { DataResult.Text = $"导出 ICS 失败：{exception.Message}"; }
    }

    void ImportIcs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入 ICS 日历", Filter = "iCalendar (*.ics)|*.ics|所有文件 (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var result = icsImport.Import(File.ReadAllText(dialog.FileName));
            reminders.RefreshSchedule();
            DataResult.Text = result.ReadOnlyMirrorCount == 0
                ? $"已导入 {result.ImportedCount} 项 ICS 事项。"
                : $"已导入 {result.ImportedCount} 项，其中 {result.ReadOnlyMirrorCount} 项为只读镜像。";
        }
        catch (Exception exception) { DataResult.Text = $"导入 ICS 失败：{exception.Message}"; }
    }
    async void Test_Click(object sender, RoutedEventArgs e)
    {
        Result.Text = "正在测试连接…";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await ai.Test(Value());
            Result.Text = $"连接成功 · {stopwatch.ElapsedMilliseconds} ms";
        }
        catch (Exception exception) { Result.Text = $"连接失败：{exception.Message}"; }
    }
}
