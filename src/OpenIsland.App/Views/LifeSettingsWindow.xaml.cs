using System.Windows;
using OpenIsland.App.Services;

namespace OpenIsland.App.Views;

public partial class LifeSettingsWindow : Window
{
    readonly ProviderSettingsService settings;
    readonly LifePreferencesService preferences;
    readonly ReminderService reminders;
    readonly OpenAiChatService ai;
    readonly AutoStartService autoStart;
    bool loading;

    public LifeSettingsWindow(ProviderSettingsService settings, LifePreferencesService preferences, ReminderService reminders, OpenAiChatService ai, AutoStartService autoStart)
    {
        InitializeComponent();
        this.settings = settings;
        this.preferences = preferences;
        this.reminders = reminders;
        this.ai = ai;
        this.autoStart = autoStart;
        var provider = settings.Load();
        Url.Text = provider.BaseUrl;
        Model.Text = provider.Model;
        Key.Password = provider.ApiKey;
        loading = true;
        WindowsNotifications.IsChecked = preferences.Load().WindowsNotifications;
        AutoStart.IsEnabled = autoStart.IsSupported;
        AutoStart.IsChecked = autoStart.IsEnabled;
        AutoStartStatus.Text = autoStart.Status;
        loading = false;
    }

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
        preferences.Save(new LifePreferences(WindowsNotifications.IsChecked == true));
        reminders.RefreshSchedule();
        Result.Text = "设置已保存。";
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    async void Test_Click(object sender, RoutedEventArgs e)
    {
        Result.Text = "正在测试连接…";
        try
        {
            await ai.Test(Value());
            Result.Text = "连接成功。";
        }
        catch (Exception exception) { Result.Text = exception.Message; }
    }
}
