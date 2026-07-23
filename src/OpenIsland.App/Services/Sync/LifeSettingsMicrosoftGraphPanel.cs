using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using OpenIsland.App.Services.Sync;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using OpenIsland.App.Services.Persistence;
using Orientation = System.Windows.Controls.Orientation;

namespace OpenIsland.App.Views;

public partial class LifeSettingsWindow
{
    Border? graphPanel;
    readonly MicrosoftGraphSettingsStore graphSettings = new();
    static readonly IMicrosoftGraphAuthenticationService graphAuthentication = new MicrosoftGraphAuthenticationService();

    /// <summary>Adds the opt-in Microsoft sign-in panel without enabling any remote synchronization.</summary>
    public void EnableMicrosoftGraphSignIn()
    {
        if (graphPanel is not null) return;
        var root = Content as Grid;
        var stack = (root?.Children.OfType<ScrollViewer>().FirstOrDefault()?.Content as StackPanel);
        if (stack is null) return;

        var saved = graphSettings.Load();
        var clientId = new TextBox { Text = saved.ClientId, MinWidth = 260, Margin = new Thickness(0, 5, 0, 8) };
        var tenant = new TextBox { Text = saved.TenantId, MinWidth = 260, Margin = new Thickness(0, 5, 0, 10) };
        var result = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(saved.Username) ? "默认关闭：登录不会自动开始同步。" : $"上次已登录：{saved.Username}（需在本次启动中重新授权后才可同步）",
            Foreground = new SolidColorBrush(Color.FromRgb(165, 165, 171)), TextWrapping = TextWrapping.Wrap
        };
        var signIn = new Button { Content = "登录 Microsoft", Padding = new Thickness(12, 6, 12, 6) };
        signIn.Click += async (_, _) =>
        {
            signIn.IsEnabled = false;
            result.Text = "正在通过 Windows 账户管理器登录；不可用时会打开系统浏览器…";
            try
            {
                var response = await graphAuthentication.SignInAsync(new(clientId.Text.Trim(), tenant.Text.Trim()),
                    new WindowInteropHelper(this).Handle);
                if (!response.Succeeded)
                {
                    result.Foreground = Brushes.OrangeRed;
                    result.Text = response.Error;
                    return;
                }
                graphSettings.Save(new(clientId.Text.Trim(), string.IsNullOrWhiteSpace(tenant.Text) ? "common" : tenant.Text.Trim(),
                    response.Account!.HomeAccountId, response.Account.Username));
                result.Foreground = new SolidColorBrush(Color.FromRgb(157, 214, 157));
                result.Text = $"已登录 {response.Account.Username}（{(response.Account.Mode == GraphAuthenticationMode.WindowsBroker ? "WAM" : "系统浏览器")}）。同步仍保持关闭。";
            }
            finally { signIn.IsEnabled = true; }
        };
        var taskList = new TextBox { Text = "Open Island", MinWidth = 260, Margin = new Thickness(0, 5, 0, 8) };
        var pushToDo = new Button { Content = "单向推送本地待办", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        pushToDo.Click += async (_, _) =>
        {
            var account = graphSettings.Load();
            if (string.IsNullOrWhiteSpace(account.AccountId))
            {
                result.Foreground = Brushes.OrangeRed;
                result.Text = "请先在本次启动中完成 Microsoft 登录。";
                return;
            }
            pushToDo.IsEnabled = false;
            result.Foreground = new SolidColorBrush(Color.FromRgb(165, 165, 171));
            result.Text = "正在单向推送本地待办到 Microsoft To Do…";
            try
            {
                var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(databasePath);
                var service = new MicrosoftToDoPushService(runtime.ConnectionFactory,
                    new GraphSyncStore(runtime.ConnectionFactory, runtime.WriteQueue), graphAuthentication);
                var summary = await service.PushAllAsync(account.AccountId, string.IsNullOrWhiteSpace(taskList.Text) ? "Open Island" : taskList.Text.Trim());
                result.Foreground = summary.Succeeded ? new SolidColorBrush(Color.FromRgb(157, 214, 157)) : Brushes.OrangeRed;
                result.Text = summary.Succeeded ? $"已推送：新建 {summary.Created}，更新 {summary.Updated}，跳过 {summary.Skipped}。" : summary.Error;
            }
            finally { pushToDo.IsEnabled = true; }
        };
        var form = new StackPanel();
        var calendarId = new TextBox { MinWidth = 260, Margin = new Thickness(0, 5, 0, 8), ToolTip = "留空使用默认日历" };
        var pushCalendar = new Button { Content = "单向推送本地日程", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        pushCalendar.Click += async (_, _) =>
        {
            var account = graphSettings.Load();
            if (string.IsNullOrWhiteSpace(account.AccountId))
            {
                result.Foreground = Brushes.OrangeRed;
                result.Text = "请先在本次启动中完成 Microsoft 登录。";
                return;
            }
            pushCalendar.IsEnabled = false;
            result.Foreground = new SolidColorBrush(Color.FromRgb(165, 165, 171));
            result.Text = "正在单向推送可无损映射的本地日程到 Outlook…";
            try
            {
                var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(databasePath);
                var service = new OutlookCalendarPushService(runtime.ConnectionFactory,
                    new GraphSyncStore(runtime.ConnectionFactory, runtime.WriteQueue), graphAuthentication);
                var summary = await service.PushAllAsync(account.AccountId, calendarId.Text.Trim());
                result.Foreground = summary.Succeeded ? new SolidColorBrush(Color.FromRgb(157, 214, 157)) : Brushes.OrangeRed;
                result.Text = summary.Succeeded
                    ? $"已推送日程：新建 {summary.Created}，更新 {summary.Updated}，跳过 {summary.Skipped}" + (summary.SkippedReasons.Count == 0 ? "。" : $"。{summary.SkippedReasons[0]}")
                    : summary.Error;
            }
            finally { pushCalendar.IsEnabled = true; }
        };
        form.Children.Add(new TextBlock { Text = "Microsoft 同步（可选）", FontSize = 18, FontWeight = FontWeights.SemiBold });
        form.Children.Add(new TextBlock
        {
            Text = "填写你在 Azure 应用注册中创建的公共客户端 Client ID；应用不保存 Client Secret。需配置 WAM 重定向 URI 与 http://localhost 浏览器回退 URI。",
            Foreground = new SolidColorBrush(Color.FromRgb(165, 165, 171)), Margin = new Thickness(0, 5, 0, 8), TextWrapping = TextWrapping.Wrap
        });
        form.Children.Add(new TextBlock { Text = "Client ID" }); form.Children.Add(clientId);
        form.Children.Add(new TextBlock { Text = "Tenant（留空或 common 均可）" }); form.Children.Add(tenant);
        form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { signIn } });
        form.Children.Add(new TextBlock { Text = "To Do 目标清单（首次会创建）" }); form.Children.Add(taskList);
        form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { pushToDo } });
        form.Children.Add(result);
        form.Children.Add(new TextBlock { Text = "Outlook Calendar ID（留空使用默认日历）" }); form.Children.Add(calendarId);
        form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { pushCalendar } });
        graphPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(36, 36, 40)), CornerRadius = new CornerRadius(12), Padding = new Thickness(18),
            Margin = new Thickness(0, 14, 0, 0), Child = form
        };
        stack.Children.Add(graphPanel);
    }
}

internal static class LifeSettingsMicrosoftGraphPanelBootstrap
{
    static Timer? timer;

    [ModuleInitializer]
    internal static void Initialize() => timer = new Timer(_ => TryAttach(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    static void TryAttach()
    {
        var application = Application.Current;
        if (application?.Dispatcher.HasShutdownStarted != false) return;
        application.Dispatcher.BeginInvoke(() =>
        {
            foreach (var settings in application.Windows.OfType<LifeSettingsWindow>()) settings.EnableMicrosoftGraphSignIn();
        });
    }
}
