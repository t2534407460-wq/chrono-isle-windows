using System.Globalization;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ChronoIsle.App.Services;

namespace ChronoIsle.App.Views;

public partial class IslandNotificationWindow : Window
{
    const double Gap = 6;
    readonly DispatcherTimer retractTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    int animationVersion;
    SystemToastMessage? currentMessage;
    SystemToastInboxService? inbox;

    internal SystemToastMessage? CurrentMessage => currentMessage;

    public IslandNotificationWindow()
    {
        InitializeComponent();
        retractTimer.Tick += (_, _) => HideMessage();
        SourceInitialized += (_, _) => IslandWindowStyles.HideFromTaskView(this);
    }

    public void ShowMessage(SystemToastMessage message, SystemToastInboxService? inbox = null)
    {
        currentMessage = message;
        this.inbox = inbox;
        NotificationActionStatus.Visibility = Visibility.Collapsed;
        OpenAppButton.IsEnabled = NativeToastBannerService.IsValidAppId(message.AppUserModelId);
        NotificationTime.Text = message.CreatedAt.LocalDateTime.ToString("HH:mm");
        NotificationAppIcon.Source = message.Icon;
        NotificationAppIcon.Visibility = message.Icon is null ? Visibility.Collapsed : Visibility.Visible;
        NotificationAppBadge.Visibility = message.Icon is null ? Visibility.Visible : Visibility.Collapsed;
        var appName = string.IsNullOrWhiteSpace(message.AppName) ? "系统通知" : message.AppName.Trim();
        var title = string.IsNullOrWhiteSpace(message.Title) ? "新消息" : message.Title.Trim();
        var body = message.Body?.Trim() ?? string.Empty;
        var version = ++animationVersion;

        retractTimer.Stop();
        NotificationAppBadge.Text = StringInfo.GetNextTextElement(appName).ToUpperInvariant();
        NotificationAppName.Text = appName;
        NotificationTitle.Text = title;
        NotificationBody.Text = body;
        NotificationBody.Visibility = body.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        NotificationTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        NotificationTranslate.Y = -6;
        if (!IsVisible) Show();

        BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = SystemParameters.ClientAreaAnimation
                ? TimeSpan.FromMilliseconds(180)
                : TimeSpan.FromMilliseconds(1),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
        NotificationTranslate.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation
            {
                From = -6,
                To = 0,
                Duration = SystemParameters.ClientAreaAnimation
                    ? TimeSpan.FromMilliseconds(220)
                    : TimeSpan.FromMilliseconds(1),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (version != animationVersion) return;
            Opacity = 1;
            NotificationTranslate.Y = 0;
        }));
        if (!IsMouseOver) retractTimer.Start();
        if (inbox is not null && message.Icon is null) _ = LoadIconAsync(message, inbox);
    }

    async Task LoadIconAsync(SystemToastMessage message, SystemToastInboxService source)
    {
        var icon = await source.LoadIconAsync(message.Id);
        if (!ReferenceEquals(currentMessage, message) || icon is null) return;
        NotificationAppIcon.Source = icon;
        NotificationAppIcon.Visibility = Visibility.Visible;
        NotificationAppBadge.Visibility = Visibility.Collapsed;
    }

    void Notification_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => retractTimer.Stop();

    void Notification_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (IsVisible) retractTimer.Start();
    }

    void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        if (currentMessage is not { } message || !NativeToastBannerService.IsValidAppId(message.AppUserModelId)) return;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(@"shell:AppsFolder\" + message.AppUserModelId);
            System.Diagnostics.Process.Start(start)?.Dispose();
            HideMessage();
        }
        catch (Exception exception)
        {
            ToastInboxDiagnostics.Write("open-app-failed", message.Id, ToastInboxDiagnostics.Failure(exception));
            ShowActionFailure("无法打开应用，可在 Windows 通知中心查看。");
        }
    }

    void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        if (currentMessage is not { } message) return;
        if (inbox?.Dismiss(message.Id) == true) HideMessage();
        else ShowActionFailure("未能清除，请检查通知访问权限。");
    }

    void ShowActionFailure(string text)
    {
        NotificationActionStatus.Text = text;
        NotificationActionStatus.Visibility = Visibility.Visible;
    }

    public void PositionNextTo(Rect anchor, Rect workArea, bool placeAbove)
    {
        UpdateLayout();
        NotificationCard.Measure(new System.Windows.Size(Width, double.PositiveInfinity));
        var height = Math.Max(1, Math.Max(ActualHeight, NotificationCard.DesiredSize.Height));
        var targetLeft = anchor.Left + (anchor.Width - Width) / 2;
        var maximumLeft = Math.Max(workArea.Left, workArea.Right - Width);
        Left = Math.Clamp(targetLeft, workArea.Left, maximumLeft);
        var targetTop = placeAbove
            ? anchor.Top - height - Gap
            : anchor.Bottom + Gap;
        var maximumTop = Math.Max(workArea.Top, workArea.Bottom - height);
        Top = Math.Clamp(targetTop, workArea.Top, maximumTop);
    }

    public void HideMessage()
    {
        retractTimer.Stop();
        currentMessage = null;
        if (!IsVisible) return;
        var version = ++animationVersion;
        var animation = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = SystemParameters.ClientAreaAnimation
                ? TimeSpan.FromMilliseconds(130)
                : TimeSpan.FromMilliseconds(1),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (version != animationVersion) return;
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Hide();
        };
        BeginAnimation(OpacityProperty, animation);
    }

    protected override void OnClosed(EventArgs e)
    {
        retractTimer.Stop();
        currentMessage = null;
        animationVersion++;
        base.OnClosed(e);
    }
}
