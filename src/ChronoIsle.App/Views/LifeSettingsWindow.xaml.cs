using System.Windows;
using System.Windows.Input;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Domain;
using ChronoIsle.App.Services.Commanding;
using ChronoIsle.App.Services.Reporting;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Sync;

namespace ChronoIsle.App.Views;

public partial class LifeSettingsWindow : Window
{
    readonly ProviderSettingsService settings;
    readonly LifePreferencesService preferences;
    readonly ReminderService reminders;
    readonly OpenAiChatService ai;
    readonly AutoStartService autoStart;
    readonly SqliteOnlineBackupService backups;
    readonly IcsExportService icsExport;
    readonly IcsImportService icsImport;
    bool loading;
    readonly AuditPrivacyService auditPrivacy;
    readonly string databasePath;
    readonly AssistantCommandPipeline commandPipeline;

    public LifeSettingsWindow(ProviderSettingsService settings, LifePreferencesService preferences, ReminderService reminders, OpenAiChatService ai, AutoStartService autoStart, LifeDataService data, AssistantCommandPipeline commandPipeline)
    {
        InitializeComponent();
        this.settings = settings;
        this.preferences = preferences;
        this.reminders = reminders;
        this.ai = ai;
        this.autoStart = autoStart;
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
        WindowsNotifications.IsChecked = savedPreferences.WindowsNotifications;
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

    void Save_Click(object sender, RoutedEventArgs e)
    {
        settings.Save(Value());
        var current = preferences.Load();
        preferences.Save(current with
        {
            WindowsNotifications = WindowsNotifications.IsChecked == true,
            AssistantPersona = Persona.SelectedValue as string ?? "Direct"
        });
        reminders.RefreshSchedule();
        Result.Text = "设置已保存。";
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
