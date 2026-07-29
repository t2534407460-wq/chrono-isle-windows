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

    public IslandNotificationWindow()
    {
        InitializeComponent();
        retractTimer.Tick += (_, _) => HideMessage();
        SourceInitialized += (_, _) => IslandWindowStyles.HideFromTaskView(this);
    }

    public void ShowMessage(SystemToastMessage message)
    {
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
        retractTimer.Start();
    }

    public void PositionNextTo(Rect anchor, Rect workArea, bool placeAbove)
    {
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

    void HideMessage()
    {
        retractTimer.Stop();
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
        base.OnClosed(e);
    }
}
