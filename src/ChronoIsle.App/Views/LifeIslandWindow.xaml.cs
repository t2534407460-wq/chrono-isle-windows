using System.IO;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Globalization;
using ChronoIsle.App.Services.Domain;
using ChronoIsle.App.Services.State;
using ChronoIsle.App.Services.Productivity;
using ChronoIsle.App.Services.Reporting;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Media;
using ChronoIsle.App.ViewModels;

namespace ChronoIsle.App.Views;

public partial class LifeIslandWindow : Window
{
    const double CollapsedWidth = 294;
    const double ExpandedWidth = 620;
    const double SnapThreshold = 28;
    const double UnsnapThreshold = 48;
    const double TopDockVisibleHeight = 6;
    const double TaskbarIconPadding = 6;
    const double ContextMenuTaskbarProximity = 24;
    const int HeaderDoubleClickMilliseconds = 280;
    readonly IIslandStateCoordinator islandState;

    readonly TaskAttributesService taskAttributes;
    readonly LifeDataService data;
    readonly ReminderService reminders;
    readonly ChinaStatutoryHolidayCalendar holidays;
    readonly TodayDashboardService todayDashboard;
    readonly FocusService focus;
    readonly ReportService reports;
    readonly LifePreferencesService preferences;
    readonly ThemeService theme;
    readonly MediaSessionService media;
    readonly AudioSpectrumService audioSpectrum;
    readonly SystemTelemetryService telemetry;
    readonly ForegroundFpsService foregroundFps;
    readonly Action<ForegroundFpsSnapshot> foregroundFpsSnapshotChanged;
    readonly WinEventDelegate taskbarForegroundChanged;
    readonly NetworkSpeedTestService networkSpeedTest;
    readonly Action<NetworkSpeedTestSnapshot> networkSpeedTestSnapshotChanged;
    readonly LifeViewModel assistant;
    readonly NamingSuggestionService naming;
    readonly ObservableCollection<FormatRow> formatRows = [];
    CancellationTokenSource? namingRequestCancellation;
    NamingResult? result;
    NamingKind? resultKind;
    NamingFormatValue? recommendation;
    int candidateIndex;
    readonly DispatcherTimer focusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly RectangleGeometry taskbarClipGeometry = new();
    FocusSession? activeFocus;
    FocusCompletion? pendingFocusCompletion;
    string? focusTitle;
    Border? todayPanel;
    StackPanel? todayDashboardContent;
    readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer collapseTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer topDockHoverExitTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    readonly DispatcherTimer headerSingleClickTimer = new();
    DateTime displayedMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    DateTime selectedDate = DateTime.Today;
    DateTime nextArchiveSweep = DateTime.MinValue;
    int archiveSweepRunning;
    bool dragging;
    bool dragged;
    bool expanded;
    bool pointerHover;
    ExpandedPinState expandedPinState = ExpandedPinState.Normal;
    int windowBoundsAnimationVersion;
    double windowResizeCenterX = double.NaN;
    int topDockAnimationVersion;
    int expandedContentAnimationVersion;
    int expandedContentResizeVersion;
    int collapsedMediaTransitionVersion;
    int taskbarHeightAnimationVersion;
    bool taskbarHeightAnimationActive;
    bool updatingTaskbarHeaderAnchor;
    double taskbarHeaderAnchorTop;
    bool suppressDoubleClickMouseUp;
    bool topDockFolded;
    double topDockUnfoldedTop = double.NaN;
    byte[]? displayedArtworkBytes;
    ImageSource? displayedArtworkImage;
    bool collapsedMediaControlsVisible;
    bool collapsedMediaControlsPinned;
    bool networkSpeedTestSpinnerAnimating;
    bool networkSpeedTestGaugeSyncRendering;
    NetworkSpeedTestDisplayUnit networkSpeedTestDisplayUnit;
    bool isClosed;
    IslandNotificationWindow? notificationWindow;
    bool musicModeActive;
    IslandPlacement placement;
    string? taskbarMonitorDeviceName;
    double taskbarHorizontalRatio = 0.5;
    MonitorGeometry? taskbarDragGeometry;
    IReadOnlyList<Rect> taskbarDragOccupiedDips = Array.Empty<Rect>();
    System.Drawing.Point taskbarDragPointerStartPixels;
    double taskbarDragStartLeft;
    double taskbarDragStartTop;
    double taskbarDragPixelsPerDip = 1;
    double dragHeaderOffsetPixels;
    double dragHeaderHeightPixels;
    int taskbarDragGapIndex = -1;
    int taskbarGapAnimationVersion;
    bool taskbarCustomDragging;
    bool freeCustomDragging;
    bool taskbarGapAnimating;
    double taskbarGapAnimationTarget;
    string? reminderBannerKind;
    string? reminderBannerItemId;
    AgendaItem? reminderBannerItem;
    System.Drawing.Point dragStartScreenPixels;
    System.Drawing.Point lastHeaderMouseDownPixels;
    long lastHeaderMouseDownTick;
    System.Windows.Point quickActionMenuAnchor;
    bool addingReminder = true;
    bool fullscreenAvoiding;
    bool fullscreenFallbackHidden;
    LifePreferences collapsedPreferences = LifePreferences.Default;
    IslandPlacement fullscreenOriginalPlacement;
    double fullscreenOriginalLeft;
    double fullscreenOriginalTop;
    string? fullscreenOriginalTaskbarMonitor;
    double fullscreenOriginalTaskbarRatio;
    bool fullscreenOriginalTopDockFolded;
    double fullscreenOriginalTopDockUnfoldedTop;
    IntPtr taskbarForegroundEventHook;

    enum IslandPlacement { Free, Top, Taskbar }
    enum ExpandedPinState { Normal, KeepExpanded }
    enum NetworkSpeedTestDisplayUnit { Mbps, MegabytesPerSecond }
    readonly record struct MonitorGeometry(System.Windows.Forms.Screen Screen, Rect Bounds, Rect WorkArea, Rect? Taskbar);
    static readonly IntPtr HwndTopmost = new(-1);
    const uint SwpNoSize = 0x0001;
    const uint SwpNoMove = 0x0002;
    const uint SwpNoActivate = 0x0010;
    const uint EventSystemForeground = 0x0003;
    const uint WinEventOutOfContext = 0;
    const uint WinEventSkipOwnProcess = 0x0002;

    delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime);

    string NetworkSpeedTestRateUnit => networkSpeedTestDisplayUnit == NetworkSpeedTestDisplayUnit.Mbps
        ? "Mbps" : "MB/s";

    double GetNetworkSpeedTestDisplayRate(double rate) =>
        networkSpeedTestDisplayUnit == NetworkSpeedTestDisplayUnit.Mbps ? rate : rate / 8d;

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr window, System.Text.StringBuilder className, int maxCount);

    enum IslandQuickAction
    {
        AddTodo,
        StartFocus,
        ManageItems,
        Settings,
        PauseReminders,
        ToggleDoNotDisturb,
        ToggleMusicMode,
        ToggleTopDockAutoFold
    }
    public event EventHandler? OpenRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ManageRequested;
    public event EventHandler<ItemNavigationTarget>? ItemDetailsRequested;
    public event EventHandler<string>? ChatRequested;
    DateOnly? automaticWeeklyReportDate;
    bool nextWeekPlanVisible;

    public LifeIslandWindow(LifeDataService data, ReminderService reminders, ChinaStatutoryHolidayCalendar holidays,
        TodayDashboardService todayDashboard, FocusService focus, ReportService reports, IIslandStateCoordinator islandState,
        TaskAttributesService taskAttributes, LifePreferencesService preferences, ThemeService theme,
        MediaSessionService media, AudioSpectrumService audioSpectrum, SystemTelemetryService telemetry, ForegroundFpsService foregroundFps,
        LifeViewModel assistant, NetworkSpeedTestService networkSpeedTest, NamingSuggestionService naming)
    {
        InitializeComponent();
        this.naming = naming;
        FormatRows.ItemsSource = formatRows;
        this.data = data;
        this.reminders = reminders;
        this.holidays = holidays;
        this.todayDashboard = todayDashboard;
        this.focus = focus;
        this.reports = reports;
        this.islandState = islandState;
        focusTimer.Tick += (_, _) => RefreshFocusSummary();
        this.preferences = preferences;
        collapsedPreferences = preferences.Load();
        this.theme = theme;
        theme.ThemeChanged += Theme_Changed;
        this.media = media;
        this.audioSpectrum = audioSpectrum;
        audioSpectrum.SpectrumChanged += AudioSpectrum_SpectrumChanged;
        this.telemetry = telemetry;
        this.foregroundFps = foregroundFps;
        taskbarForegroundChanged = TaskbarForegroundChanged;
        foregroundFpsSnapshotChanged = snapshot =>
        {
            if (isClosed || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (isClosed || Dispatcher.HasShutdownStarted) return;
                UpdateForegroundFpsSummary(snapshot);
            });
        };
        foregroundFps.SnapshotChanged += foregroundFpsSnapshotChanged;
        this.networkSpeedTest = networkSpeedTest;
        networkSpeedTestSnapshotChanged = snapshot =>
        {
            if (isClosed || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (isClosed || Dispatcher.HasShutdownStarted) return;
                UpdateNetworkSpeedTestView(snapshot);
            });
        };
        networkSpeedTest.SnapshotChanged += networkSpeedTestSnapshotChanged;
        this.assistant = assistant;
        QuickAskTaskCard.DataContext = assistant;
        QuickAskKnowledgeMode.DataContext = assistant;
        QuickAskLegacyConfirmation.DataContext = assistant;
        media.SnapshotChanged += _ =>
        {
            audioSpectrum.RefreshCaptureDevice();
            Dispatcher.BeginInvoke(Refresh);
        };
        telemetry.SnapshotChanged += snapshot => Dispatcher.BeginInvoke(() => UpdateTelemetryView(snapshot));
        assistant.Messages.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(UpdateQuickAskView);
        assistant.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(UpdateQuickAskView);
        Header.ContextMenuOpening += Header_ContextMenuOpening;
        Header.ContextMenu = CreateQuickActionMenu();
        clockTimer.Tick += (_, _) => RefreshClockAndState();
        this.taskAttributes = taskAttributes;
        collapseTimer.Tick += (_, _) => AutoCollapse();
        topDockHoverExitTimer.Tick += (_, _) => ConfirmTopDockHoverExit();
        headerSingleClickTimer.Tick += (_, _) =>
        {
            headerSingleClickTimer.Stop();
            ToggleExpanded();
        };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(CollapseWhenForegroundMovesToAnotherProcess));
        IslandLayout.LayoutUpdated += (_, _) => MaintainTaskbarHeaderAnchor();
        MainBorder.SizeChanged += (_, _) => UpdateTaskbarClip();
        LocationChanged += (_, _) => PositionNotificationWindow();
        SizeChanged += (_, _) => PositionNotificationWindow();
        SourceInitialized += (_, _) =>
        {
            IslandWindowStyles.HideFromTaskView(this);
            taskbarForegroundEventHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, taskbarForegroundChanged, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        };
        Closed += (_, _) =>
        {
            isClosed = true;
            notificationWindow?.Close();
            if (taskbarForegroundEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(taskbarForegroundEventHook);
                taskbarForegroundEventHook = IntPtr.Zero;
            }
            namingRequestCancellation?.Cancel();
            foregroundFps.SnapshotChanged -= foregroundFpsSnapshotChanged;
            foregroundFps.Stop();
            networkSpeedTest.SnapshotChanged -= networkSpeedTestSnapshotChanged;
            networkSpeedTest.Cancel();
            StopNetworkSpeedTestSpinner();
            StopNetworkSpeedTestGaugeAnimation();
            clockTimer.Stop();
            focusTimer.Stop();
            collapseTimer.Stop();
            headerSingleClickTimer.Stop();
            topDockHoverExitTimer.Stop();
            theme.ThemeChanged -= Theme_Changed;
            audioSpectrum.SpectrumChanged -= AudioSpectrum_SpectrumChanged;
        };
        Loaded += (_, _) =>
        {
            InitializeQuickAdd();
            InitializeReminderActions();
            InitializeTodayDashboard();
            ConfigureGlowBorder();
            RestoreInitialPlacement();
            RefreshClockAndState();
            clockTimer.Start();
            focusTimer.Start();
        };
        preferences.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            ConfigureGlowBorder();
            UpdateTelemetryView(telemetry.Current);
            Refresh();
        });
        data.AgendaChanged += (_, _) => Dispatcher.BeginInvoke(Refresh);
        islandState.StateChanged += _ => Dispatcher.BeginInvoke(Refresh);
        reminders.DeferredSummaryReleased += (_, summary) => Dispatcher.BeginInvoke(() =>
        {
            if (summary.Total == 0) return;
            ReminderText.Text = $"勿扰期间有 {summary.Total} 个提醒：逾期 {summary.Overdue} 个，其中高优先级 {summary.High + summary.Urgent} 个。";
            ReminderBanner.Visibility = Visibility.Visible;
            Expand();
            Touch();
        });
        reminders.NotificationHealthChanged += (_, health) => Dispatcher.BeginInvoke(() =>
        {
            var now = DateTimeOffset.UtcNow;
            if (!health.IsDegraded)
            {
                islandState.Clear("notification:degraded", now);
                return;
            }
            islandState.Publish(new IslandStateSnapshot(
                "notification:degraded", 85,
                "\u63d0\u9192\u6295\u9012\u6301\u7eed\u5931\u8d25\uff1a\u8bf7\u68c0\u67e5 Windows \u901a\u77e5\u8bbe\u7f6e\u3002",
                null, TimeSpan.FromSeconds(2), 100, IslandAnimationLevel.Prominent), now);
            Expand();
        });
    }

    public void ShowReminder(string kind, string itemId, DateTime? occurrenceAt = null)
    {
        var item = data.FindAgendaItem(kind, itemId, occurrenceAt);
        if (item is not null)
        {
            selectedDate = item.StartsAt.Date;
            displayedMonth = new DateTime(selectedDate.Year, selectedDate.Month, 1);
            ReminderText.Text = $"提醒时间到：{item.Title}";
            ReminderBanner.Visibility = Visibility.Visible;
            reminderBannerKind = kind;
            reminderBannerItemId = itemId;
            reminderBannerItem = item;
        }
        Expand();
        Refresh();
        Touch();
    }

    void RestoreInitialPlacement()
    {
        // A normal pin state is deliberately not topmost. Restoring it inside the
        // taskbar would put the island behind the taskbar before it can be opened.
        PositionAtTopCenter();
    }

    void PositionAtTopCenter(System.Windows.Forms.Screen? screen = null)
    {
        placement = IslandPlacement.Free;
        ApplyPlacementVisuals();
        UpdateLayout();
        var area = TryGetMonitorGeometry(screen ?? ScreenForHeader(), out var geometry)
            ? geometry.WorkArea
            : SystemParameters.WorkArea;
        windowResizeCenterX = area.Left + area.Width / 2;
        Left = windowResizeCenterX - ActualWidth / 2;
        Top = area.Top + 10;
    }

    void ResetToDefaultPlacement()
    {
        dragging = false;
        dragged = false;
        taskbarCustomDragging = false;
        freeCustomDragging = false;
        StopTaskbarGapJump(false);
        ClearTaskbarDragConstraints();
        collapseTimer.Stop();
        headerSingleClickTimer.Stop();
        SetExpandedPinState(ExpandedPinState.Normal);
        expanded = false;
        pointerHover = false;

        ++expandedContentAnimationVersion;
        ++taskbarHeightAnimationVersion;
        ++windowBoundsAnimationVersion;
        taskbarHeightAnimationActive = false;
        BeginAnimation(WidthProperty, null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        ExpandedScrollViewer.BeginAnimation(MaxHeightProperty, null);
        ChevronRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
        Width = CollapsedWidthForContent();
        ExpandedScrollViewer.MinHeight = 0;
        ExpandedScrollViewer.MaxHeight = 0;
        ExpandedContent.Visibility = Visibility.Collapsed;
        ChevronRotate.Angle = 0;

        var initialScreen = System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
        PositionAtTopCenter(initialScreen);
        PersistTaskbarPlacement(false);
    }

    public void ShowSystemToast(SystemToastMessage message, SystemToastInboxService inbox)
    {
        var currentPreferences = preferences.Load();
        if (isClosed || !IsVisible || !inbox.IsRunning || !currentPreferences.ToastInboxEnabled ||
            currentPreferences.DoNotDisturbEnabled ||
            (currentPreferences.FullScreenSilentEnabled && fullscreenAvoiding)) return;
        notificationWindow ??= new IslandNotificationWindow();
        notificationWindow.ShowMessage(message, inbox);
        PositionNotificationWindow();
    }

    public void HideSystemToast() => notificationWindow?.HideMessage();

    void PositionNotificationWindow()
    {
        if (isClosed || !IsLoaded || notificationWindow?.IsVisible != true) return;
        var anchor = HeaderScreenRect();
        if (expanded)
            anchor = new Rect(Left, Top, ActualWidth, ActualHeight);
        var area = TryGetMonitorGeometry(ScreenForHeader(), out var geometry)
            ? geometry.WorkArea : SystemParameters.WorkArea;
        notificationWindow.PositionNextTo(anchor, area, placement == IslandPlacement.Taskbar);
    }

    System.Windows.Forms.Screen ScreenForHeader()
    {
        try
        {
            var center = Header.PointToScreen(new System.Windows.Point(Header.ActualWidth / 2, Header.ActualHeight / 2));
            return System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
                (int)Math.Round(center.X), (int)Math.Round(center.Y)));
        }
        catch (InvalidOperationException)
        {
            return System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
        }
    }

    Rect HeaderScreenRect()
    {
        var topLeftPixels = Header.PointToScreen(new System.Windows.Point(0, 0));
        var bottomRightPixels = Header.PointToScreen(new System.Windows.Point(Header.ActualWidth, Header.ActualHeight));
        var topLeft = PointFromScreen(topLeftPixels);
        var bottomRight = PointFromScreen(bottomRightPixels);
        return new Rect(
            Left + topLeft.X,
            Top + topLeft.Y,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);
    }

    static Rect ScreenPixelsRect(System.Drawing.Rectangle rectangle)
        => new(rectangle.Left, rectangle.Top, rectangle.Width, rectangle.Height);

    Rect ScreenRectangleToDip(System.Drawing.Rectangle rectangle)
    {
        var topLeft = PointFromScreen(new System.Windows.Point(rectangle.Left, rectangle.Top));
        var bottomRight = PointFromScreen(new System.Windows.Point(rectangle.Right, rectangle.Bottom));
        return new Rect(
            Left + topLeft.X,
            Top + topLeft.Y,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);
    }

    Rect ScreenRectangleToDip(Rect rectangle)
    {
        var topLeft = PointFromScreen(new System.Windows.Point(rectangle.Left, rectangle.Top));
        var bottomRight = PointFromScreen(new System.Windows.Point(rectangle.Right, rectangle.Bottom));
        return new Rect(
            Left + topLeft.X,
            Top + topLeft.Y,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);
    }

    bool TryGetMonitorGeometry(System.Windows.Forms.Screen screen, out MonitorGeometry geometry)
    {
        try
        {
            var bounds = ScreenRectangleToDip(screen.Bounds);
            var workArea = ScreenRectangleToDip(screen.WorkingArea);
            Rect? taskbar = IslandPlacementGeometry.TryGetBottomTaskbar(bounds, workArea, out var detected)
                ? detected
                : null;
            geometry = new MonitorGeometry(screen, bounds, workArea, taskbar);
            return true;
        }
        catch (InvalidOperationException)
        {
            geometry = default;
            return false;
        }
    }

    void ApplyPlacementVisuals(Rect? taskbar = null)
    {
        if (placement != IslandPlacement.Top) ResetTopDockFold();
        var taskbarMode = placement == IslandPlacement.Taskbar && taskbar is Rect;
        Grid.SetRow(Header, taskbarMode ? 1 : 0);
        Grid.SetRow(ExpandedScrollViewer, taskbarMode ? 0 : 1);
        Header.Height = taskbarMode
            ? Math.Min(43, Math.Max(30, taskbar!.Value.Height - 2))
            : 43;

        if (placement == IslandPlacement.Top)
        {
            Outer.Margin = new Thickness(0);
            MainBorder.CornerRadius = new CornerRadius(0, 0, 18, 18);
        }
        else if (taskbarMode)
        {
            Outer.Margin = new Thickness(0);
            MainBorder.CornerRadius = new CornerRadius(24);
        }
        else
        {
            Outer.Margin = new Thickness(8, 8, 8, 11);
            MainBorder.CornerRadius = new CornerRadius(24);
        }

        UpdateTaskbarClip();
    }

    void UpdateTaskbarClip()
    {
        if (placement != IslandPlacement.Taskbar ||
            !double.IsFinite(MainBorder.ActualWidth) ||
            !double.IsFinite(MainBorder.ActualHeight) ||
            MainBorder.ActualWidth <= 0 ||
            MainBorder.ActualHeight <= 0)
        {
            MainBorder.Clip = null;
            return;
        }

        var radius = MainBorder.CornerRadius.TopLeft;
        taskbarClipGeometry.Rect = new Rect(
            0,
            0,
            MainBorder.ActualWidth,
            MainBorder.ActualHeight);
        taskbarClipGeometry.RadiusX = radius;
        taskbarClipGeometry.RadiusY = radius;
        MainBorder.Clip = taskbarClipGeometry;
    }

    static IReadOnlyList<Rect> TaskbarOccupiedRectanglesPixels(System.Windows.Forms.Screen screen)
    {
        try
        {
            var taskbarCondition = new OrCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_SecondaryTrayWnd"));
            var taskbars = AutomationElement.RootElement.FindAll(TreeScope.Children, taskbarCondition);
            var screenBounds = ScreenPixelsRect(screen.Bounds);
            AutomationElement? targetTaskbar = null;
            var taskbarBounds = Rect.Empty;
            var bestIntersectionArea = 0d;
            for (var index = 0; index < taskbars.Count; index++)
            {
                var candidate = taskbars[index];
                var candidateBounds = candidate.Current.BoundingRectangle;
                var intersection = Rect.Intersect(candidateBounds, screenBounds);
                var area = intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
                if (area <= bestIntersectionArea) continue;
                bestIntersectionArea = area;
                targetTaskbar = candidate;
                taskbarBounds = intersection;
            }
            if (targetTaskbar is null || taskbarBounds.IsEmpty) return Array.Empty<Rect>();

            var iconCondition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            var controls = targetTaskbar.FindAll(TreeScope.Descendants, iconCondition);
            var occupied = new List<Rect>();
            var statusAreaLeft = double.PositiveInfinity;
            for (var index = 0; index < controls.Count; index++)
            {
                var control = controls[index];
                if (control.Current.IsOffscreen) continue;
                var rectangle = Rect.Intersect(control.Current.BoundingRectangle, taskbarBounds);
                if (rectangle.IsEmpty || rectangle.Width <= 0 || rectangle.Height <= 0) continue;
                occupied.Add(rectangle);

                var automationId = control.Current.AutomationId;
                var className = control.Current.ClassName;
                if (className.StartsWith("SystemTray.", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(automationId, "SystemTrayIcon", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(automationId, "NotifyItemIcon", StringComparison.OrdinalIgnoreCase))
                {
                    statusAreaLeft = Math.Min(statusAreaLeft, rectangle.Left);
                }
            }

            if (double.IsFinite(statusAreaLeft))
            {
                occupied.Add(new Rect(
                    statusAreaLeft,
                    taskbarBounds.Top,
                    taskbarBounds.Right - statusAreaLeft,
                    taskbarBounds.Height));
            }
            return occupied;
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return Array.Empty<Rect>();
        }
    }

    double TaskbarLeftForWidth(MonitorGeometry geometry, double width)
    {
        var desiredLeft = IslandPlacementGeometry.LeftForHorizontalRatio(
            taskbarHorizontalRatio,
            width,
            geometry.Bounds);
        var occupiedPixels = TaskbarOccupiedRectanglesPixels(geometry.Screen);
        if (occupiedPixels.Count == 0) return desiredLeft;
        var occupiedDips = occupiedPixels.Select(ScreenRectangleToDip).ToArray();
        return IslandPlacementGeometry.LeftAvoidingOccupiedRanges(
            desiredLeft,
            width,
            geometry.Bounds,
            occupiedDips,
            TaskbarIconPadding);
    }

    void ClampWindowToBounds(Rect bounds)
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        var clamped = IslandPlacementGeometry.ClampRectToBounds(new Rect(Left, Top, width, height), bounds);
        Left = clamped.Left;
        Top = clamped.Top;
        windowResizeCenterX = Left + width / 2;
    }

    void PositionAtTaskbar(MonitorGeometry geometry)
    {
        if (geometry.Taskbar is not Rect taskbar) return;
        ApplyPlacementVisuals(taskbar);
        UpdateLayout();
        var width = ActualWidth > 0 ? ActualWidth : Width;
        Left = TaskbarLeftForWidth(geometry, width);
        windowResizeCenterX = Left + width / 2;
        var headerOffset = Header.TranslatePoint(new System.Windows.Point(0, 0), this).Y;
        var targetHeaderTop = IslandPlacementGeometry.TaskbarHeaderTop(taskbar, Header.ActualHeight);
        Top = IslandPlacementGeometry.WindowTopForHeaderAnchor(targetHeaderTop, headerOffset);
        EnsureTaskbarTopmost();
    }

    void EnsureTaskbarTopmost()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    void TaskbarForegroundChanged(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (isClosed || Dispatcher.HasShutdownStarted || !IsTaskbarWindow(window)) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            if (isClosed || placement != IslandPlacement.Taskbar) return;
            EnsureTaskbarTopmost();
        }));
    }

    static bool IsTaskbarWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        var className = new System.Text.StringBuilder(32);
        if (GetClassName(window, className, className.Capacity) == 0) return false;
        return className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    void AlignTaskbarAfterLayout()
    {
        if (placement != IslandPlacement.Taskbar || string.IsNullOrWhiteSpace(taskbarMonitorDeviceName)) return;
        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceName, taskbarMonitorDeviceName, StringComparison.OrdinalIgnoreCase));
        if (screen is null || !TryGetMonitorGeometry(screen, out var geometry) || geometry.Taskbar is null) return;
        PositionAtTaskbar(geometry);
    }

    void ScheduleArchiveSweep()
    {
        if (Interlocked.Exchange(ref archiveSweepRunning, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                data.ArchiveCompletedAndOverdue();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to archive completed agenda items: {exception.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref archiveSweepRunning, 0);
            }
        });
    }
    void PersistTaskbarPlacement(bool docked)
    {
        try
        {
            var current = preferences.Load();
            var next = docked
                ? current with
                {
                    IslandTaskbarDocked = true,
                    IslandTaskbarMonitor = taskbarMonitorDeviceName,
                    IslandTaskbarHorizontalRatio = taskbarHorizontalRatio
                }
                : current with { IslandTaskbarDocked = false };
            if (current != next) preferences.Save(next);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to persist island placement: {exception.Message}");
        }
    }

    void RefreshClockAndState()
    {
        Clock.Text = DateTime.Now.ToString("HH:mm:ss");
        try { Refresh(); }
        catch (ObjectDisposedException exception)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to refresh island state: {exception.Message}");
        }
    }

    void Refresh()
    {
        if (isClosed) return;
        var currentPreferences = preferences.Load();
        collapsedPreferences = currentPreferences;
        ApplyCollapsedPreferences(currentPreferences);
        if (DateTime.Now >= nextArchiveSweep)
        {
            nextArchiveSweep = DateTime.Now.AddMinutes(1);
            ScheduleArchiveSweep();
        }
        if (reminderBannerKind is not null && reminderBannerItemId is not null &&
            data.FindAgendaItem(reminderBannerKind, reminderBannerItemId) is null)
        {
            ReminderBanner.Visibility = Visibility.Collapsed;
            reminderBannerKind = null;
            reminderBannerItemId = null;
            reminderBannerItem = null;
        }

        if (!TryRenderFocusSummary())
        {
            var indicator = data.GetIslandIndicatorState(DateTime.Now);
            StatusLight.Fill = IndicatorBrush(indicator);
            var now = DateTimeOffset.UtcNow;
            var transient = islandState.Current;
            if (transient?.ExpiresAt is not null && transient.ExpiresAt <= now)
            {
                islandState.Clear(transient.StateKey, now);
                transient = null;
            }
            var conflicts = CalendarConflictDetector.Find(data.AgendaFor(DateTime.Today));
            if (conflicts.Count > 0)
            {
                StatusLight.Fill = new SolidColorBrush(Color.FromRgb(255, 69, 58));
                ShowSummaryOverride($"日程冲突 · 今日 {conflicts.Count} 组重叠");
            }
            else if (transient is not null)
            {
                StatusLight.Fill = transient.StateKey == "ai:processing"
                    ? new SolidColorBrush(Color.FromRgb(94, 92, 230))
                    : new SolidColorBrush(Color.FromRgb(48, 209, 88));
                ShowSummaryOverride(transient.DisplayText);
            }
            else if (ReminderBanner.Visibility == Visibility.Visible)
            {
                StatusLight.Fill = new SolidColorBrush(Color.FromRgb(255, 159, 10));
                ShowSummaryOverride(reminderBannerItem is null ? ReminderText.Text : $"提醒 · {reminderBannerItem.Title}");
            }
            else if (currentPreferences.IslandShowMusicMode &&
                     media.Current is { IsPlaying: true, IsMusic: true } mediaSnapshot)
            {
                StatusLight.Fill = new SolidColorBrush(Color.FromRgb(50, 173, 230));
                ShowSummaryOverride($"♫ {mediaSnapshot.Title}{(string.IsNullOrWhiteSpace(mediaSnapshot.Artist) ? "" : $" · {mediaSnapshot.Artist}")}");
            }
            else
            {
                UpdateIdleSummary(currentPreferences);
            }
        }
        var currentMusic = currentPreferences.IslandShowMusicMode &&
                           media.Current is { IsMusic: true } musicSnapshot
            ? musicSnapshot : null;
        if (currentMusic?.IsPlaying == true) musicModeActive = true;
        if (currentMusic is null) musicModeActive = false;
        var collapsedMedia = musicModeActive ? currentMusic : null;
        UpdateCollapsedMediaView(collapsedMedia, currentPreferences);
        UpdateTelemetryView(telemetry.Current);
        if (collapsedMedia is not null) SetTopDockFolded(false);
        else RefreshTopDockAutoFold();
        ResizeCollapsedToContent();
        BuildCalendar();
        BuildDayAgenda();
        if (!HasOpenDropDown() && todayDashboardContent?.IsKeyboardFocusWithin != true)
            BuildTodayDashboard();
        ResizeExpandedToContent();
    }

    void ApplyCollapsedPreferences(LifePreferences currentPreferences)
    {
        MascotButton.Visibility = VisibilityFor(currentPreferences.IslandShowMascot);
        StatusLight.Visibility = VisibilityFor(currentPreferences.IslandShowStatusLight);
        MascotArea.Visibility = VisibilityFor(
            currentPreferences.IslandShowMascot || currentPreferences.IslandShowStatusLight);
        var showSummary =
            currentPreferences.IslandShowAgendaSummary ||
            currentPreferences.TelemetryEnabled;
        SetIdleSummaryWidgetVisibility(currentPreferences);
        NetworkStatusLight.Visibility = VisibilityFor(
            currentPreferences.IslandShowNetworkStatus && currentPreferences.TelemetryEnabled);
        NetworkLatencySummary.Visibility = VisibilityFor(
            currentPreferences.IslandShowNetworkLatency && currentPreferences.TelemetryEnabled);
        Clock.Visibility = VisibilityFor(currentPreferences.IslandShowClock);
        ExpandIndicator.Visibility = VisibilityFor(currentPreferences.IslandShowExpandIndicator);
        DefaultHeaderLeft.Visibility = VisibilityFor(
            MascotArea.Visibility == Visibility.Visible || showSummary);
        ClockGroup.Visibility = VisibilityFor(
            NetworkStatusLight.Visibility == Visibility.Visible ||
            NetworkLatencySummary.Visibility == Visibility.Visible ||
            Clock.Visibility == Visibility.Visible ||
            ExpandIndicator.Visibility == Visibility.Visible);
    }

    void SetIdleSummaryWidgetVisibility(LifePreferences currentPreferences)
    {
        Summary.Visibility = VisibilityFor(currentPreferences.IslandShowAgendaSummary);
        NetworkSpeedSummary.Visibility = VisibilityFor(
            currentPreferences.TelemetryEnabled && currentPreferences.IslandShowNetworkSpeed);
        CpuUsageSummary.Visibility = VisibilityFor(
            currentPreferences.TelemetryEnabled && currentPreferences.IslandShowCpuUsage);
        FpsSummary.Visibility = VisibilityFor(
            currentPreferences.TelemetryEnabled && currentPreferences.IslandShowFps);
        MemoryUsageSummary.Visibility = VisibilityFor(
            currentPreferences.TelemetryEnabled && currentPreferences.IslandShowMemoryUsage);
    }

    void ShowSummaryOverride(string text)
    {
        Summary.Text = text;
        Summary.Visibility = Visibility.Visible;
        NetworkSpeedSummary.Visibility = Visibility.Collapsed;
        CpuUsageSummary.Visibility = Visibility.Collapsed;
        FpsSummary.Visibility = Visibility.Collapsed;
        MemoryUsageSummary.Visibility = Visibility.Collapsed;
        DefaultHeaderLeft.Visibility = Visibility.Visible;
    }

    static Visibility VisibilityFor(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    static T SetThemeResource<T>(T element, DependencyProperty property, string resourceKey)
        where T : FrameworkElement
    {
        element.SetResourceReference(property, resourceKey);
        return element;
    }

    void UpdateIdleSummary(LifePreferences currentPreferences)
    {
        SetIdleSummaryWidgetVisibility(currentPreferences);
        Summary.Text = data.NextAgenda() is { } next
            ? $"下一项 · {next.StartsAt:HH:mm} {next.Title}"
            : "今天暂无安排";
        NetworkSpeedSummary.Text =
            $"↑ {FormatRate(telemetry.Current.UploadBytesPerSecond)}  ↓ {FormatRate(telemetry.Current.DownloadBytesPerSecond)}";
        CpuUsageSummary.Text = $"CPU {telemetry.Current.CpuPercent:0}%";
        UpdateForegroundFpsSummary(foregroundFps.Current);
        MemoryUsageSummary.Text = $"内存 {telemetry.Current.MemoryPercent:0}%";
        CpuUsageSummary.SetResourceReference(
            TextBlock.ForegroundProperty,
            TelemetrySummaryBrushKey(telemetry.Current.CpuPercent));
        MemoryUsageSummary.SetResourceReference(
            TextBlock.ForegroundProperty,
            TelemetrySummaryBrushKey(telemetry.Current.MemoryPercent));
    }

    void UpdateForegroundFpsSummary(ForegroundFpsSnapshot snapshot)
    {
        FpsSummary.Text = snapshot.FramesPerSecond is { } fps ? $"FPS {fps:0}" : "FPS --";
    }

    internal static string TelemetrySummaryBrushKey(double percent) =>
        percent > 90 ? "Brush.Danger" :
        percent > 70 ? "Brush.Warning" :
        "Brush.TextSecondary";

    internal static string NetworkStatusBrushKey(NetworkHealth health, long? latency) =>
        latency is null ? "Brush.Danger" : health switch
        {
            NetworkHealth.Connected => "Brush.Success",
            NetworkHealth.Unstable => "Brush.Warning",
            _ => "Brush.Danger"
        };

    internal static System.Windows.Media.Brush IndicatorBrush(IslandIndicatorState state) => state switch
    {
        IslandIndicatorState.OverdueTodo => new SolidColorBrush(Color.FromRgb(255, 69, 58)),
        IslandIndicatorState.DueSoonTodo => new SolidColorBrush(Color.FromRgb(255, 159, 10)),
        IslandIndicatorState.PendingTodo => new SolidColorBrush(Color.FromRgb(255, 214, 10)),
        IslandIndicatorState.ReminderOnly => new SolidColorBrush(Color.FromRgb(10, 132, 255)),
        _ => new SolidColorBrush(Color.FromRgb(48, 209, 88))
    };

    internal static string IndicatorDescription(IslandIndicatorState state) => state switch
    {
        IslandIndicatorState.OverdueTodo => "红色：有待办已超过设置的超时宽限。",
        IslandIndicatorState.DueSoonTodo => "橙色：有未完成待办，将在未来 1 小时内到期。",
        IslandIndicatorState.PendingTodo => "黄色：有未完成待办，且不在未来 1 小时内到期，也未逾期。",
        IslandIndicatorState.ReminderOnly => "蓝色：有尚未到点的提醒或今日日程，且没有待办。",
        _ => "绿色：没有待办，当前空闲。"
    };

    void RefreshFocusSummary()
    {
        var hadFocus = activeFocus is not null;
        if (!TryRenderFocusSummary() && hadFocus) Refresh();
    }

    bool TryRenderFocusSummary()
    {
        var current = focus.RestoreActive();
        if (current is null)
        {
            activeFocus = null;
            focusTitle = null;
            return false;
        }
        if (activeFocus?.Id != current.Id)
        {
            activeFocus = current;
            focusTitle = data.Todos().FirstOrDefault(item => item.Id == current.ItemId)?.Title ?? "待办";
        }
        var now = DateTimeOffset.UtcNow;
        var pausedSeconds = current.AccumulatedPausedSeconds +
            (current.PausedAtUtc is null ? 0 : Math.Max(0, (int)Math.Floor((now - current.PausedAtUtc.Value).TotalSeconds)));
        var remaining = current.IntendedMinutes * 60 -
            (int)Math.Floor((now - current.StartedAtUtc).TotalSeconds) + pausedSeconds;
        StatusLight.Fill = remaining > 0
            ? new SolidColorBrush(Color.FromRgb(174, 174, 178))
            : new SolidColorBrush(Color.FromRgb(255, 159, 10));
        ShowSummaryOverride(current.IsPaused
            ? $"专注已暂停 · {focusTitle}"
            : remaining > 0
            ? $"专注中 · {focusTitle} {remaining / 60:00}:{remaining % 60:00}"
            : $"专注完成 · {focusTitle}");
        return true;
    }

    System.Windows.Media.Brush CalendarForeground(bool inMonth, OfficialCalendarDay officialDay) =>
        !inMonth ? (System.Windows.Media.Brush)FindResource("Brush.TextTertiary") : officialDay.Kind switch
        {
            OfficialCalendarDayKind.StatutoryHoliday => (System.Windows.Media.Brush)FindResource("Brush.Danger"),
            OfficialCalendarDayKind.AdjustedWorkday => (System.Windows.Media.Brush)FindResource("Brush.Warning"),
            _ => (System.Windows.Media.Brush)FindResource("Brush.TextPrimary")
        };

    void InitializeReminderActions()
    {
        if (ReminderBanner.Child is not DockPanel dock) return;
        var close = dock.Children.OfType<Button>().FirstOrDefault();
        if (close is not null) dock.Children.Remove(close);
        dock.Children.Remove(ReminderText);

        var header = new DockPanel();
        if (close is not null)
        {
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
        }
        ReminderText.FontSize = 12;
        ReminderText.Margin = new Thickness(0, 0, 8, 0);
        header.Children.Add(ReminderText);

        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(ReminderAction("完成", CompleteReminderBanner));
        actions.Children.Add(ReminderAction("10分钟", () => SnoozeReminderBanner(DateTime.Now.AddMinutes(10))));
        actions.Children.Add(ReminderAction("1小时", () => SnoozeReminderBanner(DateTime.Now.AddHours(1))));
        actions.Children.Add(ReminderAction("今晚", () => SnoozeReminderBanner(TonightAt())));
        actions.Children.Add(ReminderAction("明天", () => SnoozeReminderBanner(DateTime.Today.AddDays(1).AddHours(9))));
        actions.Children.Add(ReminderAction("跳过本次", SkipReminderBanner));
        actions.Children.Add(ReminderAction("自定义", CustomSnoozeReminderBanner));
        actions.Children.Add(ReminderAction("改时间", RescheduleReminderBanner));
        ReminderBanner.Child = new StackPanel { Children = { header, actions } };
    }

    Button ReminderAction(string label, Action action)
    {
        var button = new Button
        {
            Content = label,
            Style = (Style)FindResource("IslandIcon"),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 5, 5),
            FontSize = 12
        };
        button.Click += (_, _) => action();
        return button;
    }

    static DateTime TonightAt()
    {
        var tonight = DateTime.Today.AddHours(20);
        return DateTime.Now < tonight ? tonight : tonight.AddDays(1);
    }

    void SnoozeReminderBanner(DateTime remindAt)
    {
        var item = CurrentReminderBannerItem();
        if (item is null) { ClearReminderBanner(); return; }
        try
        {
            data.SnoozeReminder(item, remindAt);
            reminders.RefreshSchedule();
            ClearReminderBanner();
        }
        catch (Exception exception)
        {
            ReminderText.Text = $"稍后提醒失败：{exception.Message}";
        }
        Touch();
    }

    AgendaItem? CurrentReminderBannerItem() => reminderBannerItem ?? (reminderBannerKind is null || reminderBannerItemId is null
        ? null : data.FindAgendaItem(reminderBannerKind, reminderBannerItemId));

    void CustomSnoozeReminderBanner()
    {
        var time = PromptReminderTime("自定义稍后提醒");
        if (time is not null) SnoozeReminderBanner(time.Value);
    }

    void RescheduleReminderBanner()
    {
        var item = CurrentReminderBannerItem();
        if (item is null) { ClearReminderBanner(); return; }
        var time = PromptReminderTime("修改任务时间");
        if (time is null) return;
        try
        {
            if (item.Kind == "recurring") data.RescheduleRecurringOccurrence(item, time.Value);
            else data.RescheduleAgenda(item, time.Value);
            reminders.RefreshSchedule();
            ClearReminderBanner();
        }
        catch (Exception exception)
        {
            ReminderText.Text = $"修改任务时间失败：{exception.Message}";
        }
        Touch();
    }

    DateTime? PromptReminderTime(string title)
    {
        var input = new TextBox
        {
            Text = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            Margin = new Thickness(0, 8, 0, 4), Padding = new Thickness(7), MinWidth = 230
        };
        var error = SetThemeResource(new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap }, TextBlock.ForegroundProperty, "Brush.Danger");
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "请输入明确时间（yyyy-MM-dd HH:mm）", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(input);
        panel.Children.Add(error);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var dialog = DialogLayout.Create(title, this, panel, actions, 420, 290);
        var cancel = new Button { Content = "取消", Style = (Style)FindResource("Button.Secondary"), Padding = new Thickness(10, 4, 10, 4) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var confirm = new Button { Content = "确定", Style = (Style)FindResource("Button.Primary"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
        confirm.Click += (_, _) =>
        {
            if (!DateTime.TryParseExact(input.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            { error.Text = "请输入 yyyy-MM-dd HH:mm 格式。"; return; }
            if (parsed <= DateTime.Now)
            { error.Text = "时间必须晚于现在。"; return; }
            dialog.Tag = parsed;
            dialog.DialogResult = true;
        };
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);

        return dialog.ShowDialog() == true ? (DateTime?)dialog.Tag : null;
    }

    void CompleteReminderBanner()
    {
        if (reminderBannerItem?.Kind == "todo") data.Complete(reminderBannerItem.Id);
        reminders.RefreshSchedule();
        ClearReminderBanner();
        Touch();
    }

    void SkipReminderBanner()
    {
        var item = CurrentReminderBannerItem();
        if (item?.Kind == "recurring")
        {
            try { data.SkipRecurringOccurrence(item); }
            catch (Exception exception) { ReminderText.Text = $"跳过本次失败：{exception.Message}"; return; }
        }
        reminders.RefreshSchedule();
        ClearReminderBanner();
        Touch();
    }
    void ClearReminderBanner()
    {
        ReminderBanner.Visibility = Visibility.Collapsed;
        reminderBannerKind = null;
        reminderBannerItemId = null;
        reminderBannerItem = null;
        // Dismissing a banner changes the user's current context. Rebuild the
        // compact summary immediately instead of leaving an expired reminder
        // title visible until the next one-minute clock tick.
        Refresh();
    }

    void InitializeTodayDashboard()
    {
        todayDashboardContent = new StackPanel();
        var panel = new StackPanel();
        panel.Children.Add(todayDashboardContent);
        todayPanel = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(12, 0, 12, 12),
            Padding = new Thickness(16),
            Child = panel
        };
        SetThemeResource(todayPanel, Border.BackgroundProperty, "Brush.Card");
        SetThemeResource(todayPanel, Border.BorderBrushProperty, "Brush.StrokeSoft");
        RenderOptions.SetClearTypeHint(todayPanel, ClearTypeHint.Enabled);
        ExpandedContent.Children.Insert(3, todayPanel);
        todayPanel.Visibility = Visibility.Collapsed;
        CalendarPanel.Visibility = Visibility.Visible;
        BuildTodayDashboard();
    }

    void BuildTodayDashboard()
    {
        if (todayDashboardContent is null) return;
        var snapshot = todayDashboard.GetSnapshot(DateTimeOffset.Now);
        todayDashboardContent.Children.Clear();
        var runningFocus = focus.RestoreActive();
        if (runningFocus is not null)
        {
            activeFocus = runningFocus;
            focusTitle ??= data.Todos().FirstOrDefault(item => item.Id == runningFocus.ItemId)?.Title ?? "待办";
            var focusRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            var endFocus = new Button { Content = "结束专注", Style = (Style)FindResource("IslandType"), Background = new SolidColorBrush(Color.FromRgb(74, 37, 40)), Foreground = Brushes.White };
            var pauseFocus = new Button { Content = runningFocus.IsPaused ? "继续专注" : "暂停专注", Style = (Style)FindResource("IslandType"), Margin = new Thickness(6, 0, 0, 0) };
            var deepDnd = new Button { Content = reminders.IsDoNotDisturbEnabled ? "退出深度勿扰" : "深度勿扰", Style = (Style)FindResource("IslandType"), Margin = new Thickness(6, 0, 0, 0) };
            endFocus.Click += (_, _) => EndActiveFocus();
            pauseFocus.Click += (_, _) => ToggleFocusPause();
            deepDnd.Click += (_, _) =>
            {
                reminders.SetDoNotDisturb(!reminders.IsDoNotDisturbEnabled);
                BuildTodayDashboard();
            };
            focusRow.Children.Add(endFocus);
            focusRow.Children.Add(pauseFocus);
            focusRow.Children.Add(deepDnd);
            focusRow.Children.Add(new TextBlock { Text = $"正在专注：{focusTitle}", Foreground = (System.Windows.Media.Brush)FindResource("Brush.Success"), VerticalAlignment = VerticalAlignment.Center });
            todayDashboardContent.Children.Add(focusRow);
        }
        if (pendingFocusCompletion is not null)
        {
            var pending = pendingFocusCompletion;
            var completionRow = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            completionRow.Children.Add(new TextBlock { Text = $"专注结束（实际 {pending.Session.ActualMinutes} 分钟）。要完成对应待办吗？", Foreground = (System.Windows.Media.Brush)FindResource("Brush.Warning"), TextWrapping = TextWrapping.Wrap });
            var choices = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
            var complete = new Button { Content = "完成任务", Style = (Style)FindResource("IslandType"), Background = new SolidColorBrush(Color.FromRgb(48, 107, 64)), Foreground = Brushes.White };
            complete.Click += (_, _) => CompleteFocusedTodo();
            var keep = new Button { Content = "保留未完成", Style = (Style)FindResource("IslandType"), Margin = new Thickness(6, 0, 0, 0) };
            keep.Click += (_, _) => KeepFocusedTodoPending();
            choices.Children.Add(complete);
            choices.Children.Add(keep);
            completionRow.Children.Add(choices);
            todayDashboardContent.Children.Add(completionRow);
        }
        var conflicts = CalendarConflictDetector.Find(data.AgendaFor(DateTime.Today));
        if (conflicts.Count > 0)
        {
            todayDashboardContent.Children.Add(new TextBlock { Text = $"日程冲突：今日有 {conflicts.Count} 组重叠日程", Foreground = (System.Windows.Media.Brush)FindResource("Brush.Danger"), Margin = new Thickness(0, 5, 0, 2), FontWeight = FontWeights.SemiBold });
            foreach (var conflict in conflicts.Take(2))
                todayDashboardContent.Children.Add(new TextBlock { Text = $"{conflict.First.StartsAt:HH:mm}–{conflict.First.EndsAt:HH:mm}  {conflict.First.Title} / {conflict.Second.Title}", Foreground = (System.Windows.Media.Brush)FindResource("Brush.Warning"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        }

        var quickActions = new WrapPanel { Margin = new Thickness(0, 0, 0, 9) };
        foreach (var action in new[] { IslandQuickAction.AddTodo, IslandQuickAction.StartFocus, IslandQuickAction.ManageItems })
        {
            var button = new Button { Content = QuickActionLabel(action), Style = (Style)FindResource("IslandQuick") };
            button.Click += (_, _) => RunQuickAction(action);
            quickActions.Children.Add(button);
        }
        todayDashboardContent.Children.Add(quickActions);

        var quickRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var quickInput = new TextBox
        {
            MinHeight = 38,
            Style = (Style)FindResource("IslandTextInput"),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var quickSubmit = new Button { Content = "发送", Style = (Style)FindResource("IslandPrimary"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
        quickInput.TextChanged += (_, _) => Touch();
        quickSubmit.Click += (_, _) => SubmitQuickNaturalLanguage(quickInput);
        quickInput.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key != Key.Enter) return;
            eventArgs.Handled = true;
            SubmitQuickNaturalLanguage(quickInput);
        };
        DockPanel.SetDock(quickSubmit, Dock.Right);
        quickRow.Children.Add(quickSubmit);
        quickRow.Children.Add(quickInput);
        todayDashboardContent.Children.Add(quickRow);

        var counts = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 13) };
        counts.Children.Add(DashboardCount("今日", snapshot.Today, snapshot.GeneratedAtUtc));
        counts.Children.Add(DashboardCount("逾期", snapshot.Overdue, snapshot.GeneratedAtUtc));
        counts.Children.Add(DashboardCount("待整理", snapshot.Inbox, snapshot.GeneratedAtUtc));
        todayDashboardContent.Children.Add(counts);
        var overview = new Grid { Margin = new Thickness(0, 3, 0, 10) };
        overview.ColumnDefinitions.Add(new ColumnDefinition());
        overview.ColumnDefinitions.Add(new ColumnDefinition());
        var nextItems = snapshot.NextAction is { } nextAction ? new[] { nextAction } : Array.Empty<TodayDashboardItem>();
        var nextCard = DashboardOverviewCard("下一行动", nextItems, "暂无已安排事项", Color.FromRgb(87, 202, 130));
        var overdueCard = DashboardOverviewCard("逾期事项", snapshot.Overdue, "没有逾期事项", Color.FromRgb(255, 105, 97));
        Grid.SetColumn(nextCard, 0);
        Grid.SetColumn(overdueCard, 1);
        overview.Children.Add(nextCard);
        overview.Children.Add(overdueCard);
        todayDashboardContent.Children.Add(overview);
        AddDashboardSection("待整理 / Inbox", snapshot.Inbox, includeInboxActions: true);
        var all = snapshot.Today.Concat(snapshot.Overdue).Concat(snapshot.Inbox).GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First());
        var suggestions = snapshot.SuggestedItemIds.Where(all.ContainsKey).Select(id => all[id].Title).ToArray();
        var localNow = DateTimeOffset.Now;
        var weekStart = DateTime.Today.AddDays(-((int)DateTime.Today.DayOfWeek + 6) % 7);
        var weekPeriod = new ReportPeriod(ReportPeriodKind.Weekly,
            new DateTimeOffset(weekStart, TimeZoneInfo.Local.GetUtcOffset(weekStart)),
            new DateTimeOffset(weekStart.AddDays(7), TimeZoneInfo.Local.GetUtcOffset(weekStart.AddDays(7))));
        var weeklyFacts = reports.Generate(weekPeriod, "facts-v2").Facts;
        todayDashboardContent.Children.Add(SetThemeResource(
            new TextBlock { Text = $"本周复盘：完成 {weeklyFacts.CompletedCount} · 逾期 {weeklyFacts.OverdueCount} · 高优先级 {weeklyFacts.HighPriorityCount}", Margin = new Thickness(0, 8, 0, 0), FontSize = 12 },
            TextBlock.ForegroundProperty,
            "Brush.TextSecondary"));
        var suggestionTitle = SetThemeResource(
            new TextBlock { Text = "AI 今日建议（本地排序）", FontSize = 12, FontWeight = FontWeights.SemiBold },
            TextBlock.ForegroundProperty,
            "Brush.TextSecondary");
        var suggestionBody = SetThemeResource(
            new TextBlock
            {
                Text = suggestions.Length == 0 ? "暂时没有需要优先处理的事项。" : $"优先处理 {string.Join("、", suggestions)}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
                FontSize = 12
            },
            TextBlock.ForegroundProperty,
            "Brush.TextPrimary");
        var suggestionCard = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(9), Margin = new Thickness(0, 6, 0, 0),
            Child = new StackPanel
            {
                Children =
                {
                    suggestionTitle,
                    suggestionBody
                }
            }
        };
        SetThemeResource(suggestionCard, Border.BackgroundProperty, "Brush.Card");
        SetThemeResource(suggestionCard, Border.BorderBrushProperty, "Brush.Stroke");
        todayDashboardContent.Children.Add(suggestionCard);
    }

    void SubmitQuickNaturalLanguage(TextBox input)
    {
        var text = input.Text.Trim();
        if (text.Length == 0) return;
        input.Clear();
        ChatRequested?.Invoke(this, text);
    }

    ContextMenu CreateQuickActionMenu()
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("IslandContextMenu")
        };
        var contextActions = new[]
        {
            IslandQuickAction.ManageItems,
            IslandQuickAction.Settings,
            IslandQuickAction.PauseReminders,
            IslandQuickAction.ToggleDoNotDisturb,
            IslandQuickAction.ToggleMusicMode,
            IslandQuickAction.ToggleTopDockAutoFold
        };
        foreach (var action in contextActions)
        {
            if (menu.Items.Count > 0 && StartsQuickActionGroup(action))
            {
                menu.Items.Add(new Separator
                {
                    Style = (Style)FindResource("IslandContextMenuSeparator")
                });
            }

            var item = new MenuItem
            {
                Header = CreateQuickActionHeader(action),
                Style = (Style)FindResource("IslandContextMenuItem"),
                Tag = action,
                IsCheckable = action is IslandQuickAction.ToggleDoNotDisturb
                    or IslandQuickAction.ToggleMusicMode
                    or IslandQuickAction.ToggleTopDockAutoFold
            };
            item.Click += (_, _) => RunQuickAction(action);
            menu.Items.Add(item);
        }
        return menu;
    }

    void Header_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Header.ContextMenu is not { } menu) return;
        var currentPreferences = preferences.Load();
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is not IslandQuickAction action) continue;
            item.IsChecked = action switch
            {
                IslandQuickAction.ToggleDoNotDisturb => reminders.IsDoNotDisturbEnabled,
                IslandQuickAction.ToggleMusicMode => currentPreferences.IslandShowMusicMode,
                IslandQuickAction.ToggleTopDockAutoFold => currentPreferences.IslandTopDockAutoFold,
                _ => false
            };
        }
        var cursor = System.Windows.Forms.Cursor.Position;
        quickActionMenuAnchor = Header.PointFromScreen(
            new System.Windows.Point(cursor.X, cursor.Y));
        menu.PlacementTarget = Header;
        if (ShouldPlaceQuickActionMenuAboveTaskbar())
        {
            menu.Placement = PlacementMode.Custom;
            menu.CustomPopupPlacementCallback = PlaceQuickActionMenuAboveHeader;
        }
        else
        {
            menu.Placement = PlacementMode.MousePoint;
            menu.CustomPopupPlacementCallback = null;
        }
    }

    bool ShouldPlaceQuickActionMenuAboveTaskbar()
    {
        if (placement == IslandPlacement.Taskbar) return true;
        var screen = ScreenForHeader();
        if (!TryGetMonitorGeometry(screen, out var geometry) || geometry.Taskbar is not Rect taskbar)
            return false;
        return HeaderScreenRect().Bottom >= taskbar.Top - ContextMenuTaskbarProximity;
    }

    CustomPopupPlacement[] PlaceQuickActionMenuAboveHeader(
        System.Windows.Size popupSize,
        System.Windows.Size targetSize,
        System.Windows.Point offset)
    {
        var maximumLeft = Math.Max(0, targetSize.Width - popupSize.Width);
        var left = Math.Clamp(quickActionMenuAnchor.X - 18, 0, maximumLeft);
        return
        [
            new CustomPopupPlacement(
                new System.Windows.Point(left, -popupSize.Height - 6),
                PopupPrimaryAxis.Horizontal)
        ];
    }

    static bool StartsQuickActionGroup(IslandQuickAction action)
        => action is IslandQuickAction.PauseReminders;

    static Grid CreateQuickActionHeader(IslandQuickAction action)
    {
        var text = QuickActionLabel(action);
        var separator = text.IndexOf(' ');
        var icon = new TextBlock
        {
            Text = separator >= 0 ? text[..separator] : string.Empty,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = separator >= 0 ? text[(separator + 1)..] : text,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(icon);
        header.Children.Add(label);
        return header;
    }

    static string QuickActionLabel(IslandQuickAction action) => action switch
    {
        IslandQuickAction.AddTodo => "＋ 新建事项",
        IslandQuickAction.StartFocus => "◎ 专注模式",
        IslandQuickAction.ManageItems => "☰ 事项工作台",
        IslandQuickAction.Settings => "⚙ 设置",
        IslandQuickAction.PauseReminders => "Ⅱ 暂停提醒",
        IslandQuickAction.ToggleDoNotDisturb => "◐ 勿扰模式",
        IslandQuickAction.ToggleMusicMode => "♫ 显示音乐模式",
        IslandQuickAction.ToggleTopDockAutoFold => "⌃ 顶部吸附自动收缩",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    void RunQuickAction(IslandQuickAction action)
    {
        switch (action)
        {
            case IslandQuickAction.AddTodo:
                addingReminder = false; UpdateQuickAddType(); ShowCalendarDashboard();
                Dispatcher.BeginInvoke(QuickAddInput.Focus); break;
            case IslandQuickAction.StartFocus:
                try
                {
                    var candidate = taskAttributes.Recommend(60, EnergyLevel.High, DateTimeOffset.UtcNow).FirstOrDefault();
                    if (candidate is null) throw new InvalidOperationException("请先为一个待办设置预计分钟数，再开始专注。");
                    activeFocus = focus.Start(candidate.ItemId, 25);
                    focusTitle = candidate.Title;
                    ShowTodayDashboard();
                }
                catch (Exception exception)
                {
                    ReminderText.Text = $"无法开始专注：{exception.Message}";
                    ReminderBanner.Visibility = Visibility.Visible;
                    Expand();
                }
                break;
            case IslandQuickAction.ManageItems: OpenManagement(); break;
            case IslandQuickAction.Settings: OpenSettings(); break;
            case IslandQuickAction.PauseReminders: reminders.SetDoNotDisturb(true); BuildTodayDashboard(); break;
            case IslandQuickAction.ToggleDoNotDisturb: reminders.SetDoNotDisturb(!reminders.IsDoNotDisturbEnabled); BuildTodayDashboard(); break;
            case IslandQuickAction.ToggleMusicMode: ToggleMusicMode(); break;
            case IslandQuickAction.ToggleTopDockAutoFold: ToggleTopDockAutoFold(); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
        Touch();
    }

    void ToggleMusicMode()
    {
        var current = preferences.Load();
        collapsedPreferences = current with { IslandShowMusicMode = !current.IslandShowMusicMode };
        preferences.Save(collapsedPreferences);
        if (!collapsedPreferences.IslandShowMusicMode) musicModeActive = false;
        Refresh();
    }

    void ToggleTopDockAutoFold()
    {
        var current = preferences.Load();
        var enabled = !current.IslandTopDockAutoFold;
        collapsedPreferences = current with { IslandTopDockAutoFold = enabled };
        preferences.Save(collapsedPreferences);
        topDockHoverExitTimer.Stop();
        if (!enabled)
        {
            SetTopDockFolded(false);
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(RefreshTopDockAutoFold));
    }

    void ToggleFocusPause()
    {
        var current = activeFocus ?? focus.RestoreActive();
        if (current is null) return;
        try
        {
            activeFocus = current.IsPaused ? focus.Resume(current.Id) : focus.Pause(current.Id);
            Refresh();
        }
        catch (Exception exception)
        {
            ReminderText.Text = $"专注状态更新失败：{exception.Message}";
            ReminderBanner.Visibility = Visibility.Visible;
        }
    }

    void EndActiveFocus()
    {
        if (activeFocus is null) return;
        try
        {
            pendingFocusCompletion = focus.End(activeFocus.Id);
            activeFocus = null;
            focusTitle = null;
            Refresh();
        }
        catch (Exception exception)
        {
            ReminderText.Text = $"结束专注失败：{exception.Message}";
            ReminderBanner.Visibility = Visibility.Visible;
        }
    }

    void CompleteFocusedTodo()
    {
        if (pendingFocusCompletion is null) return;
        data.Complete(pendingFocusCompletion.Session.ItemId);
        pendingFocusCompletion = null;
        Refresh();
    }

    void KeepFocusedTodoPending()
    {
        pendingFocusCompletion = null;
        Refresh();
    }

    void AddDashboardSection(string title, IReadOnlyList<TodayDashboardItem> items, bool includeInboxActions = false)
    {
        if (todayDashboardContent is null || items.Count == 0) return;
        todayDashboardContent.Children.Add(SetThemeResource(
            new TextBlock { Text = title, FontSize = 12, Margin = new Thickness(0, 3, 0, 3) },
            TextBlock.ForegroundProperty,
            "Brush.TextTertiary"));
        foreach (var item in items.Take(3))
        {
            if (!includeInboxActions)
            {
                todayDashboardContent.Children.Add(SetThemeResource(
                    new TextBlock { Text = $"{DashboardTime(item)}  {item.Title}", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 0, 3), FontSize = 12 },
                    TextBlock.ForegroundProperty,
                    "Brush.TextPrimary"));
                continue;
            }

            var card = new StackPanel { Margin = new Thickness(2, 0, 0, 6) };
            card.Children.Add(SetThemeResource(
                new TextBlock
                {
                    Text = item.IsReadOnly ? $"{item.Title}（只读副本）" : item.Title,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = 12
                },
                TextBlock.ForegroundProperty,
                "Brush.TextPrimary"));
            var controls = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
            var when = new TextBox
            {
                Text = NextDashboardSchedule().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                Width = 112,
                Height = 25,
                Padding = new Thickness(5, 2, 5, 2),
                Style = (Style)FindResource("IslandTextInput")
            };
            controls.Children.Add(when);
            controls.Children.Add(DashboardAction("安排", () =>
            {
                if (!DateTime.TryParseExact(when.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var local))
                {
                    ShowDashboardMessage("请输入 yyyy-MM-dd HH:mm 格式的明确时间。");
                    return;
                }
                ScheduleInbox(item, local);
            }));
            controls.Children.Add(DashboardAction("设为今天", () =>
            {
                var today = NextTodaySlot();
                if (today is null) ShowDashboardMessage("今天已没有可安排时段，请输入明确时间。");
                else ScheduleInbox(item, today.Value);
            }));
            controls.Children.Add(DashboardAction("设为本周", () =>
            {
                var thisWeek = NextThisWeekSlot();
                if (thisWeek is null) ShowDashboardMessage("本周已没有可安排时段，请输入明确时间。");
                else ScheduleInbox(item, thisWeek.Value);
            }));
            controls.Children.Add(DashboardAction("忽略", () => MutateInbox(item, deleteLocalCopy: false), !item.IsReadOnly));
            controls.Children.Add(DashboardAction("删除副本", () => MutateInbox(item, deleteLocalCopy: true)));
            card.Children.Add(controls);
            todayDashboardContent.Children.Add(card);
        }
    }

    Button DashboardAction(string label, Action action, bool enabled = true)
    {
        var button = new Button
        {
            Content = label,
            Style = (Style)FindResource("IslandType"),
            Margin = new Thickness(4, 0, 0, 3),
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 11,
            IsEnabled = enabled
        };
        button.Click += (_, _) => action();
        return button;
    }

    void ScheduleInbox(TodayDashboardItem item, DateTime local)
    {
        if (local <= DateTime.Now)
        {
            ShowDashboardMessage("安排时间必须晚于现在。");
            return;
        }
        var zones = new SystemTimeZoneCatalog();
        if (!zones.TryResolveIana(zones.LocalIanaTimeZoneId, out _, out var windows))
        {
            ShowDashboardMessage("当前系统时区无法解析，未安排事项。");
            return;
        }
        var wallClock = new TemporalValue(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), null,
            zones.LocalIanaTimeZoneId, windows, TimeSemantics.ZonedWallClock);
        var due = wallClock with { UtcInstant = new OccurrenceTimeResolver(zones).ResolveSingleLocalTime(wallClock) };
        ShowInboxResult(todayDashboard.ScheduleInbox(item.Id, item.RowVersion, due));
    }

    void MutateInbox(TodayDashboardItem item, bool deleteLocalCopy) =>
        ShowInboxResult(deleteLocalCopy
            ? todayDashboard.DeleteLocalCopy(item.Id, item.RowVersion)
            : todayDashboard.Ignore(item.Id, item.RowVersion));

    void ShowInboxResult(InboxMutationResult result)
    {
        if (result == InboxMutationResult.Succeeded)
        {
            Refresh();
            return;
        }
        ShowDashboardMessage(result switch
        {
            InboxMutationResult.ReadOnly => "只读外部副本不能修改，可删除本地副本。",
            InboxMutationResult.ConcurrentConflict => "事项刚刚发生变化，请刷新后再试。",
            InboxMutationResult.NotInboxItem => "该事项已不在待整理列表中。",
            _ => "没有找到该待整理事项。"
        });
    }

    void ShowDashboardMessage(string message)
    {
        ReminderText.Text = message;
        ReminderBanner.Visibility = Visibility.Visible;
        Touch();
    }

    static DateTime NextDashboardSchedule()
    {
        var now = DateTime.Now;
        return now.AddHours(1).AddMinutes(-now.Minute).AddSeconds(-now.Second);
    }

    static DateTime? NextTodaySlot()
    {
        var now = DateTime.Now;
        foreach (var hour in new[] { 10, 14, 20, 23 })
        {
            var candidate = now.Date.AddHours(hour);
            if (candidate > now.AddMinutes(5)) return candidate;
        }
        return null;
    }

    static DateTime? NextThisWeekSlot()
    {
        var now = DateTime.Now;
        var endOfWeek = now.Date.AddDays(6 - ((int)now.DayOfWeek + 6) % 7);
        for (var day = now.Date; day <= endOfWeek; day = day.AddDays(1))
            foreach (var hour in new[] { 10, 14, 20 })
            {
                var candidate = day.AddHours(hour);
                if (candidate > now.AddMinutes(5)) return candidate;
            }
        return null;
    }

    Border DashboardCount(string label, IReadOnlyList<TodayDashboardItem> items, DateTimeOffset nowUtc)
    {
        var labelText = SetThemeResource(
            new TextBlock
            {
                Text = label,
                FontSize = 11,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            },
            TextBlock.ForegroundProperty,
            "Brush.TextSecondary");
        var countCard = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 5, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = items.Count.ToString(), Foreground = (System.Windows.Media.Brush)FindResource(label == "逾期" ? "Brush.Danger" : label == "待整理" ? "Brush.Warning" : "Brush.TextPrimary"), FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center },
                    labelText
                }
            }
        };
        SetThemeResource(countCard, Border.BackgroundProperty, "Brush.Surface");
        countCard.MouseLeftButtonUp += (_, _) => ShowDashboardDetails(label, items, nowUtc);
        return countCard;
    }

    void ShowDashboardDetails(string label, IReadOnlyList<TodayDashboardItem> items, DateTimeOffset nowUtc)
    {
        var content = new StackPanel();
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var dialog = DialogLayout.Create($"{label}详情", IsVisible ? this : null, content, actions, 540, 560);
        content.Children.Add(SetThemeResource(
            new TextBlock { Text = $"{label}事项（{items.Count}）", FontWeight = FontWeights.SemiBold, FontSize = 16 },
            TextBlock.ForegroundProperty,
            "Brush.TextPrimary"));
        content.Children.Add(SetThemeResource(
            new TextBlock { Text = DashboardDetailEmptyText(label), FontSize = 12, Margin = new Thickness(0, 5, 0, 10), TextWrapping = TextWrapping.Wrap },
            TextBlock.ForegroundProperty,
            "Brush.TextSecondary"));

        var rows = new StackPanel();
        if (items.Count == 0)
        {
            rows.Children.Add(SetThemeResource(
                new TextBlock { Text = DashboardEmptyState(label), FontSize = 12, TextWrapping = TextWrapping.Wrap },
                TextBlock.ForegroundProperty,
                "Brush.TextTertiary"));
        }
        else
        {
            foreach (var item in items)
            {
                var itemButton = new Button
                {
                    Style = (Style)FindResource("IslandType"),
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Content = new TextBlock { Text = DashboardDetailText(label, item, nowUtc), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left }
                };
                itemButton.Click += (_, _) =>
                {
                    dialog.Close();
                    OpenItemDetails(ItemNavigationTarget.From(item.Id, item.Kind));
                };
                rows.Children.Add(itemButton);
            }
        }
        content.Children.Add(rows);
        var close = new Button { Content = "关闭", Style = (Style)FindResource("Button.Secondary"), MinWidth = 88, IsCancel = true };
        close.Click += (_, _) => dialog.Close();
        actions.Children.Add(close);
        dialog.ShowDialog();
    }

    static string DashboardDetailEmptyText(string label) => label switch
    {
        "逾期" => "逾期只统计超过截止时间及宽限期仍未完成的普通待办；提醒和长期事项不计入。",
        "待整理" => "待整理仅包含尚未安排时间的普通待办。",
        _ => "点击任一事项可进入详情。"
    };

    static string DashboardEmptyState(string label) => label switch
    {
        "逾期" => "当前没有逾期待办。提醒即使已经触发，也不会显示为逾期。",
        "待整理" => "当前没有未安排时间的普通待办。",
        _ => "今天没有已安排事项。"
    };

    static string DashboardDetailText(string label, TodayDashboardItem item, DateTimeOffset nowUtc)
    {
        var schedule = item.ScheduledAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未安排时间";
        if (label != "逾期" || item.ScheduledAtUtc is null)
            return $"{item.Title}\n{DashboardKind(item.Kind)} · {schedule} · {TaskDisplayLabels.Priority(item.Priority)}";

        var overdueAt = item.ScheduledAtUtc.Value.AddMinutes(item.OverdueGraceMinutes);
        var overdueFor = nowUtc - overdueAt;
        return $"{item.Title}\n原因：未完成待办超过截止时间及 {item.OverdueGraceMinutes} 分钟宽限期\n截止：{schedule}\n自 {overdueAt.ToLocalTime():yyyy-MM-dd HH:mm} 起逾期 · 已逾期 {DashboardElapsed(overdueFor)}";
    }

    static string DashboardKind(LifeItemKind kind) => kind switch
    {
        LifeItemKind.Reminder => "提醒",
        LifeItemKind.Event => "日程",
        LifeItemKind.LongTerm => "长期事项",
        _ => "待办"
    };

    static string DashboardElapsed(TimeSpan value) => value.TotalDays >= 1
        ? $"{(int)value.TotalDays} 天 {value.Hours} 小时"
        : value.TotalHours >= 1 ? $"{(int)value.TotalHours} 小时 {value.Minutes} 分钟" : $"{Math.Max(0, value.Minutes)} 分钟";

    Border DashboardOverviewCard(string title, IReadOnlyList<TodayDashboardItem> items, string emptyText, Color accent)
    {
        var content = new StackPanel();
        content.Children.Add(SetThemeResource(
            new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12 },
            TextBlock.ForegroundProperty,
            "Brush.TextPrimary"));
        if (items.Count == 0)
            content.Children.Add(SetThemeResource(
                new TextBlock { Text = emptyText, FontSize = 12, Margin = new Thickness(0, 9, 0, 0), TextWrapping = TextWrapping.Wrap },
                TextBlock.ForegroundProperty,
                "Brush.TextTertiary"));
        else
            foreach (var item in items.Take(2))
            {
                var itemText = SetThemeResource(
                    new TextBlock { Text = $"{DashboardTime(item)}  {item.Title}", FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Cursor = System.Windows.Input.Cursors.Hand },
                    TextBlock.ForegroundProperty,
                    "Brush.TextPrimary");
                var target = ItemNavigationTarget.From(item.Id, item.Kind);
                itemText.MouseLeftButtonUp += (_, _) => OpenItemDetails(target);
                content.Children.Add(itemText);
            }
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12), Margin = new Thickness(0, 0, 8, 0), Child = content, Tag = accent };
        SetThemeResource(card, Border.BackgroundProperty, "Brush.Card");
        SetThemeResource(card, Border.BorderBrushProperty, "Brush.Stroke");
        return card;
    }

    void OpenItemDetails(ItemNavigationTarget target) => ItemDetailsRequested?.Invoke(this, target);

    static string DashboardTime(TodayDashboardItem item) => item.ScheduledAtUtc?.ToLocalTime().ToString("HH:mm") ?? "待安排";

    public void CollapsePanel() => Collapse();

    public void OpenDefaultExpanded()
    {
        ResetToDefaultPlacement();
        Show();
        Expand();
        Touch();
    }

    public void OpenTodayPanel()
    {
        Show();
        Expand();
        ShowTodayDashboard();
        Touch();
    }

    public void OpenCalendarPanel()
    {
        Show();
        Expand();
        ShowCalendarDashboard();
        Touch();
    }

    public void OpenNamingTool()
    {
        Show();
        Expand();
        ShowToolsDashboard();
        ShowNamingTool();
        NamingContentScroller.ScrollToTop();
        MeaningInput.Focus();
        Touch();
    }

    void ShowTodayDashboard()
    {
        if (todayPanel is null) return;
        todayPanel.Visibility = Visibility.Visible;
        if (weeklyReportPanel is not null) weeklyReportPanel.Visibility = Visibility.Visible;
        if (weeklyHistoryPanel is not null) weeklyHistoryPanel.Visibility = Visibility.Visible;
        CalendarPanel.Visibility = Visibility.Collapsed;
        TelemetryPanel.Visibility = Visibility.Collapsed;
        QuickAskPanel.Visibility = Visibility.Collapsed;
        ToolsPanel.Visibility = Visibility.Collapsed;
        SelectDashboardTab(TodayDashboardTab);
        BuildTodayDashboard();
        Touch();
    }
    void TodayTab_Click(object sender, RoutedEventArgs e) => ShowTodayDashboard();

    void CalendarTab_Click(object sender, RoutedEventArgs e) => ShowCalendarDashboard();
    void StatusTab_Click(object sender, RoutedEventArgs e) => ShowTelemetryDashboard();
    void QuickAskTab_Click(object sender, RoutedEventArgs e) => ShowQuickAskDashboard();
    void ToolsTab_Click(object sender, RoutedEventArgs e) => ShowToolsDashboard();
    void NamingToolTab_Click(object sender, RoutedEventArgs e) => ShowNamingTool();
    void NetworkSpeedTestToolTab_Click(object sender, RoutedEventArgs e) => ShowNetworkSpeedTestTool();

    void NetworkSpeedTestUnitButton_Click(object sender, RoutedEventArgs e)
    {
        networkSpeedTestDisplayUnit = networkSpeedTestDisplayUnit == NetworkSpeedTestDisplayUnit.Mbps
            ? NetworkSpeedTestDisplayUnit.MegabytesPerSecond
            : NetworkSpeedTestDisplayUnit.Mbps;
        UpdateNetworkSpeedTestView(networkSpeedTest.Current);
    }

    async void NetworkSpeedTestStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (networkSpeedTest.Current.IsRunning)
        {
            networkSpeedTest.Cancel();
            return;
        }

        await networkSpeedTest.StartAsync();
    }

    void ShowCalendarDashboard()
    {
        if (todayPanel is null) return;
        todayPanel.Visibility = Visibility.Collapsed;
        if (weeklyReportPanel is not null) weeklyReportPanel.Visibility = Visibility.Collapsed;
        if (weeklyHistoryPanel is not null) weeklyHistoryPanel.Visibility = Visibility.Collapsed;
        CalendarPanel.Visibility = Visibility.Visible;
        TelemetryPanel.Visibility = Visibility.Collapsed;
        QuickAskPanel.Visibility = Visibility.Collapsed;
        ToolsPanel.Visibility = Visibility.Collapsed;
        SelectDashboardTab(CalendarDashboardTab);
        Touch();
    }

    void ShowTelemetryDashboard()
    {
        if (todayPanel is null) return;
        todayPanel.Visibility = Visibility.Collapsed;
        if (weeklyReportPanel is not null) weeklyReportPanel.Visibility = Visibility.Collapsed;
        if (weeklyHistoryPanel is not null) weeklyHistoryPanel.Visibility = Visibility.Collapsed;
        CalendarPanel.Visibility = Visibility.Collapsed;
        TelemetryPanel.Visibility = Visibility.Visible;
        QuickAskPanel.Visibility = Visibility.Collapsed;
        ToolsPanel.Visibility = Visibility.Collapsed;
        SelectDashboardTab(StatusDashboardTab);
        UpdateTelemetryView(telemetry.Current);
        Touch();
    }

    void ShowQuickAskDashboard()
    {
        if (todayPanel is null) return;
        todayPanel.Visibility = Visibility.Collapsed;
        if (weeklyReportPanel is not null) weeklyReportPanel.Visibility = Visibility.Collapsed;
        if (weeklyHistoryPanel is not null) weeklyHistoryPanel.Visibility = Visibility.Collapsed;
        CalendarPanel.Visibility = Visibility.Collapsed;
        TelemetryPanel.Visibility = Visibility.Collapsed;
        QuickAskPanel.Visibility = Visibility.Visible;
        ToolsPanel.Visibility = Visibility.Collapsed;
        SelectDashboardTab(QuickAskDashboardTab);
        UpdateQuickAskView();
        Touch();
    }

    void ShowToolsDashboard()
    {
        if (todayPanel is null) return;
        todayPanel.Visibility = Visibility.Collapsed;
        if (weeklyReportPanel is not null) weeklyReportPanel.Visibility = Visibility.Collapsed;
        if (weeklyHistoryPanel is not null) weeklyHistoryPanel.Visibility = Visibility.Collapsed;
        CalendarPanel.Visibility = Visibility.Collapsed;
        TelemetryPanel.Visibility = Visibility.Collapsed;
        QuickAskPanel.Visibility = Visibility.Collapsed;
        ToolsPanel.Visibility = Visibility.Visible;
        SelectDashboardTab(ToolsDashboardTab);
        ShowNamingTool();
    }

    void ShowNamingTool()
    {
        NamingToolPanel.Visibility = Visibility.Visible;
        NetworkSpeedTestToolPanel.Visibility = Visibility.Collapsed;
          CaseConverterToolPanel.Visibility = Visibility.Collapsed;
        SelectToolTab(NamingToolTab);
        MeaningInputCharCount.Text = $"{MeaningInput.Text.Length}/30";
        Touch();
    }

    void MeaningInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        MeaningInputCharCount.Text = $"{MeaningInput.Text.Length}/30";
    }

    async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (namingRequestCancellation is not null)
        {
            namingRequestCancellation.Cancel();
            return;
        }

        var meaning = MeaningInput.Text.Trim();
        if (meaning.Length == 0)
        {
            ShowStatus("请输入想表达的中文含义。", StatusKind.Error);
            MeaningInput.Focus();
            return;
        }

        using var cancellation = new CancellationTokenSource();
        namingRequestCancellation = cancellation;
        SetBusy(true);
        HideStatus();
        try
        {
            var kind = SelectedKind();
            result = await naming.GenerateAsync(kind, meaning, cancellation.Token);
            resultKind = kind;
            candidateIndex = 0;
            RenderCandidate();
            EmptyState.Visibility = Visibility.Collapsed;
            ResultCard.Visibility = Visibility.Visible;
            ShowStatus("已生成 3 个候选，可使用左右按钮切换。", StatusKind.Success);
        }
        catch (ArgumentException exception)
        {
            ShowStatus(exception.Message, StatusKind.Error);
        }
        catch (NamingServiceException exception)
        {
            ShowStatus(exception.Message, StatusKind.Error);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ShowStatus("已取消生成，可修改内容后重新生成。", StatusKind.Success);
        }
        catch (TaskCanceledException)
        {
            ShowStatus("模型请求超时，请检查网络后重试。", StatusKind.Error);
        }
        catch (HttpRequestException)
        {
            ShowStatus("无法连接模型服务，请检查网络和模型设置后重试。", StatusKind.Error);
        }
        catch (InvalidOperationException exception)
        {
            var message = exception.Message.Contains("API Key", StringComparison.OrdinalIgnoreCase)
                ? "请先在设置中填写 API Key。"
                : "模型请求失败，请检查模型地址、模型名称和 API Key 后重试。";
            ShowStatus(message, StatusKind.Error);
        }
        catch
        {
            ShowStatus("生成命名时发生错误，请重试。", StatusKind.Error);
        }
        finally
        {
            if (ReferenceEquals(namingRequestCancellation, cancellation)) namingRequestCancellation = null;
            SetBusy(false);
        }
    }

    void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (result is null) return;
        candidateIndex = (candidateIndex - 1 + result.Candidates.Count) % result.Candidates.Count;
        RenderCandidate();
        HideStatus();
    }

    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (result is null) return;
        candidateIndex = (candidateIndex + 1) % result.Candidates.Count;
        RenderCandidate();
        HideStatus();
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FormatRow row }) Copy(row.Label, row.Value);
    }

    void CopyRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (recommendation is not null) Copy(recommendation.Label, recommendation.Value);
    }

    void Copy(string label, string value)
    {
        try
        {
            System.Windows.Clipboard.SetText(value);
            if (CaseConverterToolPanel.Visibility != Visibility.Visible)
                  ShowStatus($"已复制“{label}”：{value}", StatusKind.Success);
              if (CaseConverterToolPanel.Visibility == Visibility.Visible)
              {
                    CaseConverterStatus.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Success");
                  CaseConverterStatus.Text = $"已复制“{label}”：{value}";
                  CaseConverterStatus.Visibility = Visibility.Visible;
              }
        }
        catch
        {
            if (CaseConverterToolPanel.Visibility != Visibility.Visible)
                  ShowStatus("剪贴板暂时被其他程序占用，请稍后重试。", StatusKind.Error);
              if (CaseConverterToolPanel.Visibility == Visibility.Visible)
              {
                    CaseConverterStatus.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
                  CaseConverterStatus.Text = "剪贴板暂时被其他程序占用，请稍后重试。";
                  CaseConverterStatus.Visibility = Visibility.Visible;
              }
        }
    }

    NamingKind SelectedKind() =>
        KindSelector.SelectedItem is ComboBoxItem { Tag: NamingKind kind }
            ? kind
            : NamingKind.Variable;

    void RenderCandidate()
    {
        if (result is null || result.Candidates.Count == 0) return;
        var candidate = result.Candidates[candidateIndex];
        CandidatePhrase.Text = candidate.Phrase;
        CandidateDescription.Text = candidate.Description;
        CandidateCounter.Text = $"{candidateIndex + 1} / {result.Candidates.Count}";
        recommendation = NamingFormatter.RecommendedName(resultKind ?? SelectedKind(), candidate.Words);
        RecommendedNameLabel.Text = recommendation.Label;
        RecommendedNameValue.Text = recommendation.Value;
        RecommendedHint.Text = resultKind == NamingKind.DatabaseTable
            ? "数据库表名按规范固定使用 TBLCUS_ 前缀"
            : "按当前名称类型推荐的默认写法";
        formatRows.Clear();
        foreach (var format in candidate.Formats)
            formatRows.Add(new FormatRow(
                format.Label,
                format.Value,
                $"NamingCopy{format.Kind}",
                $"复制{format.Label}命名"));
    }

    void SetBusy(bool busy)
    {
        MeaningInput.IsEnabled = !busy;
        KindSelector.IsEnabled = !busy;
        GenerateButton.IsEnabled = true;
        GenerateButton.Content = busy ? "取消生成" : "生成命名";
        PreviousButton.IsEnabled = !busy;
        NextButton.IsEnabled = !busy;
        LoadingPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            var animation = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(.85))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            LoadingRotation.BeginAnimation(RotateTransform.AngleProperty, animation);
        }
        else
        {
            LoadingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    void ShowStatus(string message, StatusKind kind)
    {
        StatusText.Text = message;
        StatusNotice.Style = (Style)FindResource(kind == StatusKind.Success ? "Notice.Success" : "Notice.Error");
        StatusIcon.Stroke = (System.Windows.Media.Brush)FindResource(
            kind == StatusKind.Success ? "Brush.Success" : "Brush.Danger");
        StatusIcon.Data = Geometry.Parse(kind == StatusKind.Success
            ? "M2,7 L6,11 L14,2"
            : "M12,3 L22,21 L2,21 Z M12,9 L12,14 M12,17 L12,18");
        StatusNotice.Visibility = Visibility.Visible;
    }

    void HideStatus() => StatusNotice.Visibility = Visibility.Collapsed;

    enum StatusKind { Success, Error }

    sealed record FormatRow(
        string Label,
        string Value,
        string AutomationId,
        string CopyAccessibleName);

    void ShowNetworkSpeedTestTool()
    {
        NamingToolPanel.Visibility = Visibility.Collapsed;
          CaseConverterToolPanel.Visibility = Visibility.Collapsed;
        NetworkSpeedTestToolPanel.Visibility = Visibility.Visible;
        SelectToolTab(NetworkSpeedTestToolTab);
        UpdateNetworkSpeedTestView(networkSpeedTest.Current);
        Touch();
    }

    void SelectToolTab(Button selected)
    {
        foreach (var tab in new[] { NamingToolTab, CaseConverterToolTab, NetworkSpeedTestToolTab })
        {
            var active = ReferenceEquals(tab, selected);
            tab.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, active ? "Brush.AccentSoft" : "Brush.Control");
            tab.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, active ? "Brush.TextPrimary" : "Brush.TextSecondary");
            tab.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, active ? "Brush.Accent" : "Brush.Stroke");
        }
        ResizeExpandedToContent();
    }

      void CaseConverterToolTab_Click(object sender, RoutedEventArgs e) => ShowCaseConverterTool();

      void ShowCaseConverterTool()
      {
          NamingToolPanel.Visibility = Visibility.Collapsed;
          NetworkSpeedTestToolPanel.Visibility = Visibility.Collapsed;
          CaseConverterToolPanel.Visibility = Visibility.Visible;
          SelectToolTab(CaseConverterToolTab);
            ShowCaseConverterPage(CasePageBasicPanel, CasePageBasic);
          UpdateCaseConverter();
            if (CaseConverterStatus is not null) CaseConverterStatus.Visibility = Visibility.Collapsed;
          Touch();
            CaseConverterInput.Focus();
      }

      void CaseConverterInput_TextChanged(object sender, TextChangedEventArgs e)
      {
          if (CaseConverterCharCount is not null)
                CaseConverterCharCount.Text = $"{CaseConverterInput.Text.Length}/200";
          UpdateCaseConverter();
            if (CaseConverterStatus is not null) CaseConverterStatus.Visibility = Visibility.Collapsed;
          Touch();
      }

        void CaseConverterClear_Click(object sender, RoutedEventArgs e)
        {
            CaseConverterInput.Clear();
            if (CaseConverterStatus is not null) CaseConverterStatus.Visibility = Visibility.Collapsed;
            CaseConverterInput.Focus();
              Touch();
        }

      void CasePageBasic_Click(object sender, RoutedEventArgs e) =>
          ShowCaseConverterPage(CasePageBasicPanel, CasePageBasic);

      void CasePageCamel_Click(object sender, RoutedEventArgs e) =>
          ShowCaseConverterPage(CasePageCamelPanel, CasePageCamel);

      void CasePageSeparator_Click(object sender, RoutedEventArgs e) =>
          ShowCaseConverterPage(CasePageSeparatorPanel, CasePageSeparator);

      void ShowCaseConverterPage(StackPanel panel, Button selected)
      {
          CasePageBasicPanel.Visibility = ReferenceEquals(panel, CasePageBasicPanel) ? Visibility.Visible : Visibility.Collapsed;
          CasePageCamelPanel.Visibility = ReferenceEquals(panel, CasePageCamelPanel) ? Visibility.Visible : Visibility.Collapsed;
          CasePageSeparatorPanel.Visibility = ReferenceEquals(panel, CasePageSeparatorPanel) ? Visibility.Visible : Visibility.Collapsed;

          foreach (var page in new[] { CasePageBasic, CasePageCamel, CasePageSeparator })
          {
              var active = ReferenceEquals(page, selected);
              page.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, active ? "Brush.AccentSoft" : "Brush.Control");
              page.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, active ? "Brush.TextPrimary" : "Brush.TextSecondary");
              page.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, active ? "Brush.Accent" : "Brush.Stroke");
          }

          ResizeExpandedToContent();
            Touch();
      }

      void UpdateCaseConverter()
      {
            if (CaseUppercaseValue is null) return;
          var text = CaseConverterInput.Text;
          var words = CaseConverterWords(text);

          CaseUppercaseValue.Text = text.ToUpperInvariant();
          CaseLowercaseValue.Text = text.ToLowerInvariant();
          CaseCapitalizeFirstValue.Text = CapitalizeFirst(text.ToLowerInvariant());
          CaseTitleCaseValue.Text = string.Join(' ', text.Split(
                new[] { ' ', '_', '-', '.', '/', '\\', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries)
                .Select(word => CapitalizeFirst(word.ToLowerInvariant())));
          CaseToggleValue.Text = ToggleCase(text);
          CaseReverseValue.Text = new string(text.Reverse().ToArray());

          CaseCamelValue.Text = JoinCase(words, firstLower: true);
          CasePascalValue.Text = JoinCase(words, firstLower: false);

          CaseSnakeValue.Text = string.Join('_', words.Select(word => word.ToLowerInvariant()));
          CaseUpperSnakeValue.Text = string.Join('_', words.Select(word => word.ToUpperInvariant()));
          CaseKebabValue.Text = string.Join('-', words.Select(word => word.ToLowerInvariant()));
          CaseUpperKebabValue.Text = string.Join('-', words.Select(word => word.ToUpperInvariant()));
          CaseDotValue.Text = string.Join('.', words.Select(word => word.ToLowerInvariant()));
          CasePathValue.Text = string.Join('/', words.Select(word => word.ToLowerInvariant()));
      }

      static IReadOnlyList<string> CaseConverterWords(string text)
      {
          var words = new List<string>();
          var current = new System.Text.StringBuilder();

          void Flush()
          {
              if (current.Length == 0) return;
              words.Add(current.ToString());
              current.Clear();
          }

          for (var index = 0; index < text.Length; index++)
          {
              var ch = text[index];
              if (!char.IsLetterOrDigit(ch))
              {
                  Flush();
                  continue;
              }

              if (current.Length > 0)
              {
                  var previous = current[current.Length - 1];
                  var next = index + 1 < text.Length ? text[index + 1] : '\0';

                  if (char.IsUpper(ch) && (char.IsLower(previous) || char.IsDigit(previous)))
                  {
                      Flush();
                  }
                  else if (char.IsUpper(ch) && char.IsUpper(previous) && char.IsLower(next))
                  {
                      Flush();
                  }
              }

              current.Append(ch);
          }

          Flush();
          return words;
      }

      static string CapitalizeFirst(string word) =>
          string.IsNullOrEmpty(word) ? word : char.ToUpperInvariant(word[0]) + word[1..];

      static string JoinCase(IReadOnlyList<string> words, bool firstLower)
      {
          if (words.Count == 0) return string.Empty;
          var parts = words.Select(word => CapitalizeFirst(word.ToLowerInvariant())).ToArray();
          if (firstLower) parts[0] = parts[0].ToLowerInvariant();
          return string.Concat(parts);
      }

      static string ToggleCase(string text) =>
          new string(text.Select(ch =>
              char.IsUpper(ch) ? char.ToLowerInvariant(ch) :
              char.IsLower(ch) ? char.ToUpperInvariant(ch) :
              ch).ToArray());

      void CopyCaseConverter_Click(object sender, RoutedEventArgs e)
      {
          if (sender is Button { Tag: string textBoxName } button &&
              FindName(textBoxName) is TextBox textBox)
          {
              Copy(button.ToolTip?.ToString() ?? textBoxName, textBox.Text);
          }
      }


    void SelectDashboardTab(Button selected)
    {
        foreach (var tab in new[] { TodayDashboardTab, CalendarDashboardTab, StatusDashboardTab, QuickAskDashboardTab, ToolsDashboardTab })
        {
            var active = ReferenceEquals(tab, selected);
            tab.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, active ? "Brush.AccentSoft" : "Brush.Control");
            tab.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, active ? "Brush.TextPrimary" : "Brush.TextSecondary");
            tab.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, active ? "Brush.Accent" : "Brush.Stroke");
        }
        ResizeExpandedToContent();
    }

    void UpdateNetworkSpeedTestView(NetworkSpeedTestSnapshot snapshot)
    {
        var platformLatencies = snapshot.Platforms.ToDictionary(
            platform => platform.Key,
            platform => platform.Milliseconds,
            StringComparer.Ordinal);
        NetworkSpeedTestPhaseText.Text = snapshot.Status;
        NetworkSpeedTestGaugeUnit.Text = NetworkSpeedTestRateUnit;
        NetworkSpeedTestDownloadValue.Text = FormatNetworkSpeedTestRate(snapshot.DownloadMegabitsPerSecond);
        NetworkSpeedTestUploadValue.Text = FormatNetworkSpeedTestRate(snapshot.UploadMegabitsPerSecond);
        NetworkSpeedTestNodeLatencyValue.Text = FormatNetworkSpeedTestLatency(snapshot.NodeLatencyMilliseconds);
        NetworkSpeedTestLeagueLatency.Text = FormatNetworkSpeedTestLatency(platformLatencies.GetValueOrDefault("league"));
        NetworkSpeedTestDouyinLatency.Text = FormatNetworkSpeedTestLatency(platformLatencies.GetValueOrDefault("douyin"));
        NetworkSpeedTestJdLatency.Text = FormatNetworkSpeedTestLatency(platformLatencies.GetValueOrDefault("jd"));
        NetworkSpeedTestCtripLatency.Text = FormatNetworkSpeedTestLatency(platformLatencies.GetValueOrDefault("ctrip"));
        NetworkSpeedTestToutiaoLatency.Text = FormatNetworkSpeedTestLatency(platformLatencies.GetValueOrDefault("toutiao"));
        NetworkSpeedTestStartButton.Content = snapshot.IsRunning
            ? "取消测速"
            : snapshot.Phase == NetworkSpeedTestPhase.Idle ? "开始测速" : "重新测速";

        var gaugeRate = snapshot.Phase switch
        {
            NetworkSpeedTestPhase.MeasuringDownload => snapshot.DownloadMegabitsPerSecond,
            NetworkSpeedTestPhase.MeasuringUpload => snapshot.UploadMegabitsPerSecond,
            _ => null
        };
        if (gaugeRate is { } rate && double.IsFinite(rate))
        {
            NetworkSpeedTestGaugeValue.Text = GetNetworkSpeedTestDisplayRate(rate).ToString("0.00", CultureInfo.InvariantCulture);
            AnimateNetworkSpeedTestGauge(rate);
        }
        else if (snapshot.Phase is NetworkSpeedTestPhase.Idle or NetworkSpeedTestPhase.SelectingNode or NetworkSpeedTestPhase.MeasuringPlatforms)
        {
            NetworkSpeedTestGaugeValue.Text = "--";
            ResetNetworkSpeedTestGauge();
        }
        else
        {
            StopNetworkSpeedTestGaugeAnimation();
        }

        if (snapshot.Phase is NetworkSpeedTestPhase.SelectingNode or NetworkSpeedTestPhase.MeasuringPlatforms)
            StartNetworkSpeedTestSpinner();
        else
            StopNetworkSpeedTestSpinner();
    }

    string FormatNetworkSpeedTestRate(double? rate) =>
        rate is { } value && double.IsFinite(value)
            ? $"{GetNetworkSpeedTestDisplayRate(value).ToString("0.00", CultureInfo.InvariantCulture)} {NetworkSpeedTestRateUnit}"
            : "未测得";

    static string FormatNetworkSpeedTestLatency(long? milliseconds) =>
        milliseconds is { } value ? $"{value} ms" : "未测得";

    static double GetNetworkSpeedTestGaugeAngle(double rate)
    {
        rate = Math.Clamp(rate, 0, 500);
        return rate <= 100
            ? -90 + rate / 100d * 156
            : 66 + Math.Log(1 + rate - 100) / Math.Log(401) * 24;
    }

    static Point GetNetworkSpeedTestGaugeProgressPoint(double rate)
    {
        return GetNetworkSpeedTestGaugeProgressPointForAngle(GetNetworkSpeedTestGaugeAngle(rate));
    }

    static Point GetNetworkSpeedTestGaugeProgressPointForAngle(double angle)
    {
        var radians = Math.PI / 180d * angle;
        return new Point(118 + 98 * Math.Sin(radians), 122 - 98 * Math.Cos(radians));
    }

    void AnimateNetworkSpeedTestGauge(double rate)
    {
        var target = GetNetworkSpeedTestGaugeAngle(rate);
        if (!SystemParameters.ClientAreaAnimation)
        {
            StopNetworkSpeedTestGaugeAnimation();
            NetworkSpeedTestGaugeNeedleRotation.Angle = target;
            NetworkSpeedTestGaugeProgressArc.Point = GetNetworkSpeedTestGaugeProgressPointForAngle(target);
            return;
        }

        var current = NetworkSpeedTestGaugeNeedleRotation.Angle;
        NetworkSpeedTestGaugeNeedleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        NetworkSpeedTestGaugeNeedleRotation.Angle = current;
        NetworkSpeedTestGaugeProgressArc.Point = GetNetworkSpeedTestGaugeProgressPointForAngle(current);
        StartNetworkSpeedTestGaugeProgressSynchronization();
        var animation = new DoubleAnimation
        {
            From = current,
            To = target,
            Duration = TimeSpan.FromMilliseconds(180),
            FillBehavior = FillBehavior.HoldEnd
        };
        NetworkSpeedTestGaugeNeedleRotation.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    void ResetNetworkSpeedTestGauge()
    {
        StopNetworkSpeedTestGaugeAnimation();
        NetworkSpeedTestGaugeNeedleRotation.Angle = -90;
        NetworkSpeedTestGaugeProgressArc.Point = new Point(20, 122);
    }

    void StopNetworkSpeedTestGaugeAnimation()
    {
        var current = NetworkSpeedTestGaugeNeedleRotation.Angle;
        NetworkSpeedTestGaugeNeedleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        NetworkSpeedTestGaugeNeedleRotation.Angle = current;
        NetworkSpeedTestGaugeProgressArc.Point = GetNetworkSpeedTestGaugeProgressPointForAngle(current);
        StopNetworkSpeedTestGaugeProgressSynchronization();
    }

    void StartNetworkSpeedTestGaugeProgressSynchronization()
    {
        if (networkSpeedTestGaugeSyncRendering) return;
        CompositionTarget.Rendering += NetworkSpeedTestGaugeProgress_Rendering;
        networkSpeedTestGaugeSyncRendering = true;
    }

    void StopNetworkSpeedTestGaugeProgressSynchronization()
    {
        if (!networkSpeedTestGaugeSyncRendering) return;
        CompositionTarget.Rendering -= NetworkSpeedTestGaugeProgress_Rendering;
        networkSpeedTestGaugeSyncRendering = false;
    }

    void NetworkSpeedTestGaugeProgress_Rendering(object? sender, EventArgs e)
    {
        NetworkSpeedTestGaugeProgressArc.Point =
            GetNetworkSpeedTestGaugeProgressPointForAngle(NetworkSpeedTestGaugeNeedleRotation.Angle);
    }

    void StartNetworkSpeedTestSpinner()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            StopNetworkSpeedTestSpinner();
            return;
        }
        if (networkSpeedTestSpinnerAnimating) return;

        NetworkSpeedTestSpinnerPath.StrokeDashOffset = 0;
        NetworkSpeedTestSpinnerPath.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation
        {
            From = 0,
            To = -14,
            Duration = TimeSpan.FromMilliseconds(900),
            RepeatBehavior = RepeatBehavior.Forever
        });
        networkSpeedTestSpinnerAnimating = true;
    }

    void StopNetworkSpeedTestSpinner()
    {
        NetworkSpeedTestSpinnerPath.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        NetworkSpeedTestSpinnerPath.StrokeDashOffset = 0;
        networkSpeedTestSpinnerAnimating = false;
    }

    void UpdateTelemetryView(SystemTelemetrySnapshot snapshot)
    {
        var enabled = preferences.Load().TelemetryEnabled;
        var label = enabled
            ? snapshot.NetworkHealth switch
            {
                NetworkHealth.Connected => "网络连接正常",
                NetworkHealth.Unstable => "网络波动大",
                NetworkHealth.Offline => "网络中断",
                _ => "网络中断"
            }
            : "系统监控已关闭";
        var brushKey = enabled
            ? NetworkStatusBrushKey(snapshot.NetworkHealth, snapshot.LatencyMilliseconds)
            : "Brush.TextTertiary";
        NetworkStatusGlyph.SetResourceReference(Shape.StrokeProperty, brushKey);
        TelemetryStatusLight.SetResourceReference(Shape.FillProperty, brushKey);
        TelemetryNetworkStatus.Text = label;
        TelemetryLatency.Text = enabled && snapshot.LatencyMilliseconds is { } value ? $"{value} ms" : "-- ms";
        NetworkLatencySummary.Text = TelemetryLatency.Text;
        TelemetryUploadSpeed.Text = FormatRate(snapshot.UploadBytesPerSecond);
        TelemetryDownloadSpeed.Text = FormatRate(snapshot.DownloadBytesPerSecond);
        TelemetryCpuText.Text = $"{snapshot.CpuPercent:0}%";
        TelemetryCpuProgress.Value = snapshot.CpuPercent;
        TelemetryMemoryText.Text = $"{snapshot.MemoryPercent:0}%";
        TelemetryMemoryProgress.Value = snapshot.MemoryPercent;
        TelemetryTodayTraffic.Text = $"↑ {FormatBytes(snapshot.TodayUploadedBytes)}  ↓ {FormatBytes(snapshot.TodayDownloadedBytes)}";
        TelemetryMonthTraffic.Text = $"↑ {FormatBytes(snapshot.MonthUploadedBytes)}  ↓ {FormatBytes(snapshot.MonthDownloadedBytes)}";

        TelemetryHistoryList.Children.Clear();
        foreach (var day in snapshot.RecentDays.OrderByDescending(item => item.Day))
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var date = new TextBlock { Text = day.Day.ToString("MM-dd"), FontSize = 11 };
            date.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
            var uploaded = new TextBlock { Text = $"↑ {FormatBytes(day.UploadedBytes)}", FontSize = 11 };
            uploaded.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            Grid.SetColumn(uploaded, 1);
            var downloaded = new TextBlock { Text = $"↓ {FormatBytes(day.DownloadedBytes)}", FontSize = 11 };
            downloaded.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            Grid.SetColumn(downloaded, 2);
            row.Children.Add(date);
            row.Children.Add(uploaded);
            row.Children.Add(downloaded);
            TelemetryHistoryList.Children.Add(row);
        }
        if (TelemetryPanel.Visibility == Visibility.Visible) ResizeExpandedToContent();
    }

    static string FormatRate(double bytesPerSecond) => $"{FormatBytes(bytesPerSecond)}/s";

    static string FormatBytes(double bytes)
    {
        var value = Math.Max(0, bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = unit == 0 || value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {units[unit]}";
    }

    void Theme_Changed(AppThemeMode _) => Dispatcher.BeginInvoke(() =>
    {
        ConfigureGlowBorder();
        Refresh();
        UpdateQuickAddType();
    });

    double CollapsedHeaderHeight()
    {
        if (placement != IslandPlacement.Taskbar) return 43;
        return TryGetMonitorGeometry(ScreenForHeader(), out var geometry) &&
               geometry.Taskbar is Rect taskbar
            ? Math.Min(43, Math.Max(30, taskbar.Height - 2))
            : Math.Min(43, Math.Max(30, Header.Height));
    }

    void UpdateHeaderTextRendering(bool allowClearType = true)
    {
        // ClearType needs an opaque text backdrop; the folded status strip has none.
        var opaque = allowClearType && MainBorder.Opacity >= 1 &&
            MainBorder.Background is SolidColorBrush brush &&
            brush.Color.A == byte.MaxValue && brush.Opacity >= 1;
        RenderOptions.SetClearTypeHint(Header, opaque ? ClearTypeHint.Enabled : ClearTypeHint.Auto);
        TextOptions.SetTextRenderingMode(Header, opaque ? TextRenderingMode.ClearType : TextRenderingMode.Auto);
    }

    void UpdateIslandBackground()
    {
        if (TopDockStatusLight.Visibility == Visibility.Visible) return;
        if (!expanded && theme.EffectiveTheme == AppThemeMode.Dark)
            MainBorder.Background = Brushes.Black;
        else
            MainBorder.SetResourceReference(Border.BackgroundProperty, "Brush.Island");
    }

    void ConfigureGlowBorder()
    {
        UpdateIslandBackground();
        UpdateHeaderTextRendering();
        if (!preferences.Load().GlowBorderEnabled || !SystemParameters.ClientAreaAnimation)
        {
            var light = theme.EffectiveTheme == AppThemeMode.Light;
            var rimColor = light ? Color.FromRgb(54, 68, 78) : Color.FromRgb(215, 227, 235);
            var rim = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 1)
            };
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(light ? (byte)72 : (byte)112, rimColor.R, rimColor.G, rimColor.B), 0));
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(18, rimColor.R, rimColor.G, rimColor.B), .38));
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(8, rimColor.R, rimColor.G, rimColor.B), .62));
            rim.GradientStops.Add(new GradientStop(Color.FromArgb(light ? (byte)44 : (byte)64, rimColor.R, rimColor.G, rimColor.B), 1));
            rim.Freeze();
            IslandOutline.BorderBrush = rim;
            var softRim = rim.Clone();
            softRim.Opacity = .22;
            softRim.Freeze();
            IslandStaticRim.BorderBrush = softRim;
            IslandStaticRim.Visibility = TopDockStatusLight.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        IslandStaticRim.Visibility = Visibility.Collapsed;
        var stroke = (FindResource("Brush.Stroke") as SolidColorBrush)?.Color ?? Color.FromRgb(56, 64, 60);
        var accent = (FindResource("Brush.Accent") as SolidColorBrush)?.Color ?? Color.FromRgb(57, 201, 139);
        var glow = Color.FromArgb(170, accent.R, accent.G, accent.B);
        var gradient = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            RelativeTransform = new RotateTransform(0, .5, .5)
        };
        gradient.GradientStops.Add(new GradientStop(stroke, 0));
        gradient.GradientStops.Add(new GradientStop(glow, .45));
        gradient.GradientStops.Add(new GradientStop(stroke, 1));
        IslandOutline.BorderBrush = gradient;
        ((RotateTransform)gradient.RelativeTransform).BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(8),
                RepeatBehavior = RepeatBehavior.Forever
            });
    }

    void UpdateQuickAskView()
    {
        QuickAskStatus.Text = assistant.Status;
        QuickAskLegacyConfirmation.Visibility = assistant.HasPendingAction ? Visibility.Visible : Visibility.Collapsed;
        QuickAskLegacyConfirmation.IsEnabled = !assistant.IsSending;
        QuickAskSend.IsEnabled = !assistant.IsSending && !string.IsNullOrWhiteSpace(QuickAskInput.Text);
        QuickAskStop.Visibility = assistant.IsSending ? Visibility.Visible : Visibility.Collapsed;
        var latest = assistant.Messages.LastOrDefault(message => message.Role == "assistant");
        if (latest is not null) QuickAskAnswer.Markdown = latest.Content;
        ResizeExpandedToContent();
    }

    async void QuickAskSend_Click(object sender, RoutedEventArgs e)
    {
        var text = QuickAskInput.Text.Trim();
        if (text.Length == 0 || assistant.IsSending) return;
        QuickAskInput.Clear();
        QuickAskAnswer.Markdown = string.Empty;
        UpdateQuickAskView();
        await assistant.SubmitQuickAskAsync(text);
        UpdateQuickAskView();
    }

    void QuickAskStop_Click(object sender, RoutedEventArgs e)
    {
        if (assistant.StopGeneratingCommand.CanExecute(null))
            assistant.StopGeneratingCommand.Execute(null);
    }

    void QuickAskInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!pointerHover && expanded) ScheduleMouseLeaveCollapse();
        else Touch();
        if (QuickAskSend is not null) UpdateQuickAskView();
    }

    void QuickAskInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        QuickAskSend_Click(QuickAskSend, new RoutedEventArgs());
    }

    void NewQuickAsk_Click(object sender, RoutedEventArgs e)
    {
        if (assistant.IsSending) return;
        assistant.BeginQuickAskConversation();
        QuickAskAnswer.Markdown = "输入一个问题，或描述想创建、查询、修改的事项。";
        QuickAskInput.Clear();
        UpdateQuickAskView();
    }

    void OpenFullChat_Click(object sender, RoutedEventArgs e) => OpenAssistant();

    void UpdateCollapsedMediaView(MediaSessionSnapshot? snapshot, LifePreferences currentPreferences)
    {
        var takeover = snapshot is not null;
        if (takeover)
        {
            DefaultHeaderLeft.Visibility = Visibility.Collapsed;
            ClockGroup.Visibility = Visibility.Collapsed;
        }
        else
            ApplyCollapsedPreferences(currentPreferences);
        CollapsedMedia.Visibility = takeover ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot is null)
        {
            collapsedMediaControlsPinned = false;
            SetCollapsedMediaControlsVisible(false);
            return;
        }

        UpdateArtwork(snapshot.Artwork);
        CollapsedMediaTitle.Text = snapshot.Title;
        CollapsedMediaArtist.Text = string.IsNullOrWhiteSpace(snapshot.Artist)
            ? snapshot.SourceAppId
            : snapshot.Artist;
        UpdateMediaPlayPauseIcons(snapshot.IsPlaying);
        SetCollapsedMediaControlsVisible(collapsedMediaControlsVisible);
    }

    void UpdateMediaPlayPauseIcons(bool isPlaying)
    {
        var playVisibility = isPlaying ? Visibility.Collapsed : Visibility.Visible;
        var pauseVisibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;
        CollapsedMediaPlayIcon.Visibility = playVisibility;
        CollapsedMediaPauseIcon.Visibility = pauseVisibility;
    }

    void UpdateArtwork(byte[]? artworkBytes)
    {
        if (!ReferenceEquals(displayedArtworkBytes, artworkBytes))
        {
            displayedArtworkBytes = artworkBytes;
            displayedArtworkImage = CreateArtworkImage(artworkBytes);
        }

        CollapsedMediaArtwork.Source = displayedArtworkImage;
        var placeholderVisibility = displayedArtworkImage is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        CollapsedMediaArtworkPlaceholder.Visibility = placeholderVisibility;
    }


    void AudioSpectrum_SpectrumChanged(double[] spectrum)
    {
        if (isClosed) return;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() => UpdateAudioSpectrum(spectrum)));
    }

    void UpdateAudioSpectrum(IReadOnlyList<double> spectrum)
    {
        if (isClosed || spectrum.Count < 5) return;
        var bars = CollapsedSpectrumBars();
        var heights = SpectrumDisplayHeights(spectrum, bars.Length);
        for (var index = 0; index < bars.Length; index++)
            bars[index].Height = heights[index];
    }

    internal static double[] SpectrumDisplayHeights(IReadOnlyList<double> spectrum, int barCount)
    {
        var heights = new double[barCount];
        if (barCount == 0) return heights;
        var peak = spectrum.Count == 0 ? 0 : spectrum.Max();
        if (peak < 0.0025)
        {
            Array.Fill(heights, 4);
            return heights;
        }

        var activity = Math.Clamp(Math.Log10(1 + peak * 30) / Math.Log10(31), 0, 1);
        var globalAmplitude = 0.28 + 0.72 * Math.Pow(activity, .55);
        for (var index = 0; index < barCount; index++)
        {
            var sourcePosition = barCount == 1 ? 0 : index * (spectrum.Count - 1d) / (barCount - 1d);
            var lower = (int)Math.Floor(sourcePosition);
            var upper = Math.Min(spectrum.Count - 1, lower + 1);
            var level = spectrum[lower] + (spectrum[upper] - spectrum[lower]) * (sourcePosition - lower);
            var relativeEnergy = Math.Clamp(level / peak, 0, 1);
            var barAmplitude = 0.4 + 0.6 * Math.Pow(relativeEnergy, .62);
            heights[index] = 4 + globalAmplitude * barAmplitude * 16;
        }
        return heights;
    }

    Border[] CollapsedSpectrumBars() =>
    [
        CollapsedSpectrum0,
        CollapsedSpectrum1,
        CollapsedSpectrum2,
        CollapsedSpectrum3,
        CollapsedSpectrum4
    ];

    static ImageSource? CreateArtworkImage(byte[]? artworkBytes)
    {
        if (artworkBytes is null || artworkBytes.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(artworkBytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Media artwork decode failed: {exception.Message}");
            return null;
        }
    }

    void SetCollapsedMediaControlsVisible(bool visible)
    {
        visible &= CollapsedMedia.Visibility == Visibility.Visible &&
                   media.Current is { IsMusic: true };
        if (visible == collapsedMediaControlsVisible) return;
        collapsedMediaTransitionVersion++;
        collapsedMediaControlsVisible = visible;
        ResetCollapsedMediaTransitionVisuals();
        CollapsedMediaTrackButton.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        CollapsedMediaControls.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    void AnimateCollapsedMediaControls(bool visible)
    {
        visible &= CollapsedMedia.Visibility == Visibility.Visible &&
                   media.Current is { IsMusic: true };
        if (visible == collapsedMediaControlsVisible) return;

        var version = ++collapsedMediaTransitionVersion;
        collapsedMediaControlsVisible = visible;
        ResetCollapsedMediaTransitionVisuals();
        CollapsedMediaTrackButton.Visibility = Visibility.Visible;

        if (visible)
        {
            CollapsedMediaControls.Opacity = 0;
            CollapsedMediaControlsTranslate.Y = 12;
            CollapsedMediaControls.Visibility = Visibility.Visible;
            CollapsedMediaTrackButton.BeginAnimation(
                OpacityProperty,
                Transition(1, 0, 90),
                HandoffBehavior.SnapshotAndReplace);
            var trackLift = Transition(0, -5, 90);
            trackLift.Completed += (_, _) =>
            {
                if (version == collapsedMediaTransitionVersion && collapsedMediaControlsVisible)
                    CollapsedMediaTrackButton.Visibility = Visibility.Collapsed;
            };
            CollapsedMediaTrackTranslate.BeginAnimation(
                TranslateTransform.YProperty,
                trackLift,
                HandoffBehavior.SnapshotAndReplace);
            CollapsedMediaControls.BeginAnimation(
                OpacityProperty,
                Transition(0, 1, 160),
                HandoffBehavior.SnapshotAndReplace);
            CollapsedMediaControlsTranslate.BeginAnimation(
                TranslateTransform.YProperty,
                Transition(12, 0, 160),
                HandoffBehavior.SnapshotAndReplace);
            return;
        }

        CollapsedMediaControls.Visibility = Visibility.Visible;
        CollapsedMediaControls.BeginAnimation(
            OpacityProperty,
            Transition(1, 0, 140),
            HandoffBehavior.SnapshotAndReplace);
        var controlsLift = Transition(0, -12, 140);
        controlsLift.Completed += (_, _) =>
        {
            if (version != collapsedMediaTransitionVersion || collapsedMediaControlsVisible) return;
            CollapsedMediaControls.Visibility = Visibility.Collapsed;
            ResetCollapsedMediaTransitionVisuals();
            CollapsedMediaTrackButton.Visibility = Visibility.Visible;
        };
        CollapsedMediaControlsTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            controlsLift,
            HandoffBehavior.SnapshotAndReplace);
        CollapsedMediaTrackButton.BeginAnimation(
            OpacityProperty,
            Transition(0, 1, 140, 20),
            HandoffBehavior.SnapshotAndReplace);
        CollapsedMediaTrackTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            Transition(4, 0, 140, 20),
            HandoffBehavior.SnapshotAndReplace);
    }

    static DoubleAnimation Transition(double from, double to, int durationMilliseconds, int delayMilliseconds = 0) => new()
    {
        From = from,
        To = to,
        BeginTime = TimeSpan.FromMilliseconds(delayMilliseconds),
        Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
    };

    void ResetCollapsedMediaTransitionVisuals()
    {
        CollapsedMediaTrackButton.BeginAnimation(OpacityProperty, null);
        CollapsedMediaControls.BeginAnimation(OpacityProperty, null);
        CollapsedMediaTrackTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        CollapsedMediaControlsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        CollapsedMediaPrevious.BeginAnimation(OpacityProperty, null);
        CollapsedMediaPlayPause.BeginAnimation(OpacityProperty, null);
        CollapsedMediaNext.BeginAnimation(OpacityProperty, null);
        CollapsedMediaPreviousTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        CollapsedMediaPlayPauseTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        CollapsedMediaNextTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        CollapsedMediaTrackButton.Opacity = 1;
        CollapsedMediaControls.Opacity = 1;
        CollapsedMediaTrackTranslate.Y = 0;
        CollapsedMediaControlsTranslate.Y = 0;
        CollapsedMediaPreviousTranslate.Y = 0;
        CollapsedMediaPlayPauseTranslate.Y = 0;
        CollapsedMediaNextTranslate.Y = 0;
    }

    void CollapsedMediaTrack_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) =>
        AnimateCollapsedMediaControls(true);

    void CollapsedMediaTrack_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (Header.IsMouseCaptured) return;
        if (!collapsedMediaControlsPinned) AnimateCollapsedMediaControls(false);
    }

    void RestoreCollapsedMediaHoverAfterHeaderCapture()
    {
        if (!collapsedMediaControlsPinned && !CollapsedMediaTrack.IsMouseOver)
            AnimateCollapsedMediaControls(false);
    }

    void CollapsedMediaTrackButton_Click(object sender, RoutedEventArgs e)
    {
        collapsedMediaControlsPinned = true;
        SetCollapsedMediaControlsVisible(true);
        e.Handled = true;
    }

    async void MediaPrevious_Click(object sender, RoutedEventArgs e)
    {
        collapsedMediaControlsPinned = true;
        SetCollapsedMediaControlsVisible(true);
        await media.PreviousAsync();
    }
    async void MediaPlayPause_Click(object sender, RoutedEventArgs e)
    {
        collapsedMediaControlsPinned = true;
        SetCollapsedMediaControlsVisible(true);
        await media.TogglePlayPauseAsync();
    }
    async void MediaNext_Click(object sender, RoutedEventArgs e)
    {
        collapsedMediaControlsPinned = true;
        SetCollapsedMediaControlsVisible(true);
        await media.NextAsync();
    }

    public void SetFullscreenAvoidance(FullscreenWindowInfo? context)
    {
        if (isClosed) return;
        if (!preferences.Load().MoveIslandDuringFullscreen || context is null)
        {
            RestoreAfterFullscreen();
            return;
        }

        if (!fullscreenAvoiding)
        {
            fullscreenAvoiding = true;
            fullscreenOriginalPlacement = placement;
            fullscreenOriginalLeft = Left;
            fullscreenOriginalTop = Top;
            fullscreenOriginalTaskbarMonitor = taskbarMonitorDeviceName;
            fullscreenOriginalTaskbarRatio = taskbarHorizontalRatio;
            fullscreenOriginalTopDockFolded = topDockFolded;
            fullscreenOriginalTopDockUnfoldedTop = topDockUnfoldedTop;
        }

        var destination = System.Windows.Forms.Screen.AllScreens
            .Where(screen => !string.Equals(screen.DeviceName, context.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(screen => screen.Primary)
            .FirstOrDefault();
        if (destination is null)
        {
            fullscreenFallbackHidden = true;
            Hide();
            return;
        }

        fullscreenFallbackHidden = false;
        if (!IsVisible) Show();
        Collapse();
        PositionAtTopCenter(destination);
    }

    void RestoreAfterFullscreen()
    {
        if (!fullscreenAvoiding) return;
        fullscreenAvoiding = false;
        placement = fullscreenOriginalPlacement;
        taskbarMonitorDeviceName = fullscreenOriginalTaskbarMonitor;
        taskbarHorizontalRatio = fullscreenOriginalTaskbarRatio;
        if (placement == IslandPlacement.Taskbar &&
            System.Windows.Forms.Screen.AllScreens.FirstOrDefault(screen =>
                string.Equals(screen.DeviceName, taskbarMonitorDeviceName, StringComparison.OrdinalIgnoreCase)) is { } taskbarScreen &&
            TryGetMonitorGeometry(taskbarScreen, out var geometry) && geometry.Taskbar is not null)
        {
            ApplyPlacementVisuals(geometry.Taskbar);
            PositionAtTaskbar(geometry);
        }
        else
        {
            ApplyPlacementVisuals();
            windowResizeCenterX = double.NaN;
            Left = fullscreenOriginalLeft;
            Top = fullscreenOriginalTop;
            RestoreTopDockFoldAfterFullscreen();
        }
        if (fullscreenFallbackHidden && !IsVisible) Show();
        fullscreenFallbackHidden = false;
    }

    void RestoreTopDockFoldAfterFullscreen()
    {
        if (placement != IslandPlacement.Top || !fullscreenOriginalTopDockFolded) return;

        Top = double.IsFinite(fullscreenOriginalTopDockUnfoldedTop)
            ? fullscreenOriginalTopDockUnfoldedTop
            : fullscreenOriginalTop;
        pointerHover = false;
        topDockFolded = false;
        topDockUnfoldedTop = double.NaN;
        SetTopDockFolded(true);
    }

    void BuildCalendar()
    {
        MonthTitle.Text = displayedMonth.ToString("yyyy 年 M 月");
        CalendarGrid.Children.Clear();
        var first = displayedMonth;
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var cursor = first.AddDays(-offset);
        for (var index = 0; index < 42; index++, cursor = cursor.AddDays(1))
        {
            var day = cursor;
            var indicator = data.GetCalendarIndicatorState(day, DateTime.Now);
            var inMonth = day.Month == displayedMonth.Month;
            var officialDay = holidays.Get(day);
            var selected = day.Date == selectedDate.Date;
            var button = new Button
            {
                Tag = day,
                Height = 38,
                Style = (Style)FindResource("IslandDay")
            };
            if (selected)
            {
                SetThemeResource(button, Button.BackgroundProperty, "Brush.AccentSoft");
                SetThemeResource(button, Button.ForegroundProperty, "Brush.TextPrimary");
            }
            else
            {
                button.Background = Brushes.Transparent;
                button.Foreground = CalendarForeground(inMonth, officialDay);
            }
            var content = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock { Text = day.Day.ToString(), HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Foreground = button.Foreground });
            var metadata = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            if (officialDay.IsHoliday || officialDay.IsAdjustedWorkday)
            {
                metadata.Children.Add(new TextBlock
                {
                    Text = officialDay.IsHoliday ? "\u4F11" : "\u73ED",
                    Foreground = button.Foreground,
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold
                });
            }
            if (indicator != IslandIndicatorState.Idle)
                metadata.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = IndicatorBrush(indicator), Stroke = selected ? button.Foreground : null, StrokeThickness = selected ? 1 : 0, Margin = new Thickness(officialDay.Kind == OfficialCalendarDayKind.None ? 0 : 3, 3, 0, 0) });
            if (metadata.Children.Count > 0)
                content.Children.Add(metadata);
            button.Content = content;
            button.Click += Day_Click;
            CalendarGrid.Children.Add(button);
        }
    }

    void BuildDayAgenda()
    {
        SelectedDateTitle.Text = selectedDate.Date == DateTime.Today ? "今天的安排" : selectedDate.ToString("M 月 d 日");
        var officialDay = holidays.Get(selectedDate);
        if (officialDay.IsHoliday)
            SelectedDateTitle.Text += $" \u00B7 {officialDay.Name}\uFF08\u6CD5\u5B9A\u8282\u5047\u65E5\uFF09";
        else if (officialDay.IsAdjustedWorkday)
            SelectedDateTitle.Text += " \u00B7 \u8C03\u4F11\u4E0A\u73ED";
        UpdateQuickAddDate();
        DayAgendaList.Children.Clear();
        var items = data.AgendaFor(selectedDate);
        if (items.Count == 0)
        {
            DayAgendaList.Children.Add(SetThemeResource(
                new TextBlock { Text = "这一天还没有安排", FontSize = 12 },
                TextBlock.ForegroundProperty,
                "Brush.TextTertiary"));
            return;
        }

        foreach (var item in items)
        {
            var row = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 5), Cursor = System.Windows.Input.Cursors.Hand };
            SetThemeResource(row, Border.BackgroundProperty, "Brush.Surface");
            var target = ItemNavigationTarget.From(item);
            row.MouseLeftButtonUp += (_, eventArgs) =>
            {
                if (IsInteractiveSource(eventArgs.OriginalSource as DependencyObject)) return;
                OpenItemDetails(target);
            };
            var panel = new DockPanel { LastChildFill = false };
            if (item.Kind == "todo")
            {
                var complete = new Button { Content = item.IsCompleted ? "✓" : "", Style = (Style)FindResource("IslandComplete"), Background = item.IsCompleted ? new SolidColorBrush(Color.FromRgb(70, 101, 75)) : Brushes.Transparent, BorderBrush = item.IsCompleted ? new SolidColorBrush(Color.FromRgb(110, 185, 119)) : new SolidColorBrush(Color.FromRgb(123, 125, 133)), Tag = item };
                complete.Click += CompleteTodo_Click;
                DockPanel.SetDock(complete, Dock.Left);
                panel.Children.Add(complete);
            }
            var copy = new StackPanel { Margin = new Thickness(item.Kind == "todo" ? 8 : 0, 0, 0, 0) };
            var indicator = data.GetAgendaItemIndicatorState(item, DateTime.Now);
            var time = item.Kind == "event" && item.EndsAt is not null ? $"{item.StartsAt:HH:mm}–{item.EndsAt:HH:mm}" : item.StartsAt.TimeOfDay == TimeSpan.Zero ? "待办" : item.StartsAt.ToString("HH:mm");
            var timeRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            timeRow.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = IndicatorBrush(indicator), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            timeRow.Children.Add(new TextBlock { Text = time, Foreground = (System.Windows.Media.Brush)FindResource("Brush.Success"), FontSize = 12 });
            copy.Children.Add(timeRow);
            copy.Children.Add(SetThemeResource(
                new TextBlock { Text = item.Title, TextDecorations = item.IsCompleted ? TextDecorations.Strikethrough : null },
                TextBlock.ForegroundProperty,
                item.IsCompleted ? "Brush.TextTertiary" : "Brush.TextPrimary"));
            panel.Children.Add(copy);
            var remove = new Button { Margin = new Thickness(10, 0, 0, 0), Style = (Style)FindResource("IslandDeleteButton"), Tag = item };
            var deleteIcon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 2 2 L 10 10 M 10 2 L 2 10"), StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            deleteIcon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = remove });
            remove.Content = new Viewbox { Width = 12, Height = 12, Child = deleteIcon };
            remove.Click += DeleteAgenda_Click;
            DockPanel.SetDock(remove, Dock.Right);
            panel.Children.Add(remove);
            row.Child = panel;
            DayAgendaList.Children.Add(row);
        }
    }

    void PrepareTaskbarDragConstraints()
    {
        taskbarDragGeometry = null;
        taskbarDragOccupiedDips = Array.Empty<Rect>();
        taskbarDragGapIndex = -1;
        dragHeaderOffsetPixels = 0;
        dragHeaderHeightPixels = 0;
        taskbarDragPointerStartPixels = System.Windows.Forms.Cursor.Position;
        taskbarDragStartLeft = Left;
        taskbarDragStartTop = Top;
        var dpi = VisualTreeHelper.GetDpi(this);
        taskbarDragPixelsPerDip = Math.Max(0.1, dpi.DpiScaleX);
        if (placement != IslandPlacement.Taskbar) return;

        try
        {
            var screen = ScreenForHeader();
            if (!TryGetMonitorGeometry(screen, out var geometry) || geometry.Taskbar is null) return;
            var windowTop = PointToScreen(new System.Windows.Point(0, 0)).Y;
            var headerTop = Header.PointToScreen(new System.Windows.Point(0, 0)).Y;
            var headerBottom = Header.PointToScreen(new System.Windows.Point(0, Header.ActualHeight)).Y;
            var occupiedPixels = TaskbarOccupiedRectanglesPixels(screen);
            taskbarDragGeometry = geometry;
            taskbarDragOccupiedDips = occupiedPixels.Select(ScreenRectangleToDip).ToArray();
            dragHeaderOffsetPixels = headerTop - windowTop;
            dragHeaderHeightPixels = Math.Max(0, headerBottom - headerTop);
            taskbarDragPixelsPerDip = Header.ActualHeight > 0
                ? Math.Max(0.1, dragHeaderHeightPixels / Header.ActualHeight)
                : taskbarDragPixelsPerDip;
            taskbarDragGapIndex = IslandPlacementGeometry.PlaceAvoidingOccupiedRanges(
                Left,
                ActualWidth,
                geometry.Bounds,
                taskbarDragOccupiedDips,
                TaskbarIconPadding).GapIndex;
        }
        catch (InvalidOperationException)
        {
            taskbarDragGeometry = null;
            taskbarDragOccupiedDips = Array.Empty<Rect>();
        }
    }

    void ClearTaskbarDragConstraints()
    {
        taskbarDragGeometry = null;
        taskbarDragOccupiedDips = Array.Empty<Rect>();
        taskbarDragGapIndex = -1;
        taskbarDragPixelsPerDip = 1;
        dragHeaderOffsetPixels = 0;
        dragHeaderHeightPixels = 0;
    }

    void BeginDirectDrag()
    {
        StopTaskbarGapJump(false);
        var currentLeft = Left;
        var currentTop = Top;
        var currentWidth = Width;
        windowResizeCenterX = double.NaN;
        ++windowBoundsAnimationVersion;
        BeginAnimation(WidthProperty, null);
        Width = currentWidth;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left = currentLeft;
        Top = currentTop;
        taskbarDragStartLeft = currentLeft;
        taskbarDragStartTop = currentTop;
    }

    void UpdateFreeCustomDrag()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        if (!TryGetMonitorGeometry(screen, out var geometry)) return;

        var deltaX = (cursor.X - taskbarDragPointerStartPixels.X) / taskbarDragPixelsPerDip;
        var deltaY = (cursor.Y - taskbarDragPointerStartPixels.Y) / taskbarDragPixelsPerDip;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;

        var constrained = IslandPlacementGeometry.ClampRectToBounds(
            new Rect(taskbarDragStartLeft + deltaX, taskbarDragStartTop + deltaY, width, height),
            geometry.Bounds);
        Left = constrained.Left;
        Top = constrained.Top;
    }

    void AnimateTaskbarGapJump(double targetLeft)
    {
        var currentLeft = Left;
        if (taskbarGapAnimating)
        {
            ++taskbarGapAnimationVersion;
            BeginAnimation(LeftProperty, null);
            Left = currentLeft;
        }

        var version = ++taskbarGapAnimationVersion;
        taskbarGapAnimating = true;
        taskbarGapAnimationTarget = targetLeft;
        var animation = new DoubleAnimation
        {
            From = currentLeft,
            To = targetLeft,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (version != taskbarGapAnimationVersion) return;
            BeginAnimation(LeftProperty, null);
            Left = taskbarGapAnimationTarget;
            taskbarGapAnimating = false;
        };
        BeginAnimation(LeftProperty, animation);
    }

    void StopTaskbarGapJump(bool finishAtTarget)
    {
        if (!taskbarGapAnimating) return;
        var left = finishAtTarget ? taskbarGapAnimationTarget : Left;
        ++taskbarGapAnimationVersion;
        BeginAnimation(LeftProperty, null);
        Left = left;
        taskbarGapAnimating = false;
    }

    bool UpdateTaskbarCustomDrag()
    {
        if (taskbarDragGeometry is not MonitorGeometry geometry || geometry.Taskbar is not Rect taskbar)
            return false;

        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        if (!string.Equals(screen.DeviceName, geometry.Screen.DeviceName, StringComparison.OrdinalIgnoreCase))
            return false;

        var deltaX = (cursor.X - taskbarDragPointerStartPixels.X) / taskbarDragPixelsPerDip;
        var deltaY = (cursor.Y - taskbarDragPointerStartPixels.Y) / taskbarDragPixelsPerDip;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return false;

        var desired = IslandPlacementGeometry.ClampRectToBounds(
            new Rect(taskbarDragStartLeft + deltaX, taskbarDragStartTop + deltaY, width, height),
            geometry.Bounds);
        var headerOffset = dragHeaderOffsetPixels / taskbarDragPixelsPerDip;
        var targetHeaderTop = IslandPlacementGeometry.TaskbarHeaderTop(taskbar, Header.ActualHeight);
        var desiredHeaderTop = desired.Top + headerOffset;
        if (!IslandPlacementGeometry.ShouldSnap(
                desiredHeaderTop - targetHeaderTop,
                true,
                SnapThreshold,
                UnsnapThreshold))
        {
            return false;
        }

        Top = IslandPlacementGeometry.WindowTopForHeaderAnchor(targetHeaderTop, headerOffset);
        var horizontalPlacement = IslandPlacementGeometry.PlaceAvoidingOccupiedRanges(
            desired.Left,
            width,
            geometry.Bounds,
            taskbarDragOccupiedDips,
            TaskbarIconPadding);

        if (horizontalPlacement.GapIndex < 0)
        {
            StopTaskbarGapJump(false);
            taskbarDragGapIndex = -1;
            Left = horizontalPlacement.Left;
            return true;
        }

        if (taskbarDragGapIndex >= 0 && horizontalPlacement.GapIndex != taskbarDragGapIndex)
        {
            taskbarDragGapIndex = horizontalPlacement.GapIndex;
            AnimateTaskbarGapJump(horizontalPlacement.Left);
            return true;
        }

        taskbarDragGapIndex = horizontalPlacement.GapIndex;
        if (!taskbarGapAnimating) Left = horizontalPlacement.Left;
        return true;
    }

    void ContinueTaskbarDragDirectly()
    {
        StopTaskbarGapJump(false);
        taskbarCustomDragging = false;
        freeCustomDragging = true;
        if (placement == IslandPlacement.Taskbar)
        {
            PersistTaskbarPlacement(false);
            placement = IslandPlacement.Free;
            ApplyPlacementVisuals();
            UpdateLayout();
        }
        UpdateFreeCustomDrag();
    }

    void FinishFreeCustomDrag()
    {
        freeCustomDragging = false;
        dragging = false;
        Header.ReleaseMouseCapture();
        UpdatePlacementAfterDrag();
        ClearTaskbarDragConstraints();
    }

    void FinishTaskbarCustomDrag()
    {
        StopTaskbarGapJump(true);
        taskbarCustomDragging = false;
        dragging = false;
        Header.ReleaseMouseCapture();
        UpdatePlacementAfterDrag();
        ClearTaskbarDragConstraints();
    }

    bool IsHeaderDoubleClick(System.Drawing.Point pointer)
    {
        var now = Environment.TickCount64;
        var elapsed = now - lastHeaderMouseDownTick;
        var doubleClickSize = System.Windows.Forms.SystemInformation.DoubleClickSize;
        var closeToPrevious =
            Math.Abs(pointer.X - lastHeaderMouseDownPixels.X) <= Math.Max(1, doubleClickSize.Width / 2) &&
            Math.Abs(pointer.Y - lastHeaderMouseDownPixels.Y) <= Math.Max(1, doubleClickSize.Height / 2);
        var detected =
            lastHeaderMouseDownTick > 0 &&
            elapsed >= 0 &&
            elapsed <= HeaderDoubleClickMilliseconds &&
            closeToPrevious;

        lastHeaderMouseDownTick = detected ? 0 : now;
        lastHeaderMouseDownPixels = pointer;
        return detected;
    }

    void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsCollapsedMediaControlSource(e.OriginalSource as DependencyObject)) return;
        if (IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        var pointer = System.Windows.Forms.Cursor.Position;
        var doubleClick = IsHeaderDoubleClick(pointer);
        headerSingleClickTimer.Stop();
        if (doubleClick)
        {
            suppressDoubleClickMouseUp = true;
            dragging = false;
            dragged = false;
            freeCustomDragging = false;
            Header.CaptureMouse();
            ResetToDefaultPlacement();
            e.Handled = true;
            return;
        }

        Touch();
        dragging = true;
        dragged = false;
        dragStartScreenPixels = pointer;
        Header.CaptureMouse();
        PrepareTaskbarDragConstraints();
    }

    void Header_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (freeCustomDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                FinishFreeCustomDrag();
                return;
            }
            UpdateFreeCustomDrag();
            return;
        }

        if (taskbarCustomDragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                FinishTaskbarCustomDrag();
                return;
            }
            if (UpdateTaskbarCustomDrag()) return;
            ContinueTaskbarDragDirectly();
            return;
        }

        if (!dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var pointer = System.Windows.Forms.Cursor.Position;
        var dragSize = System.Windows.Forms.SystemInformation.DragSize;
        if (Math.Abs(pointer.X - dragStartScreenPixels.X) <= Math.Max(5, dragSize.Width / 2) &&
            Math.Abs(pointer.Y - dragStartScreenPixels.Y) <= Math.Max(5, dragSize.Height / 2))
            return;
        dragged = true;
        dragging = false;
        BeginDirectDrag();
        if (placement == IslandPlacement.Taskbar)
        {
            taskbarCustomDragging = true;
            if (UpdateTaskbarCustomDrag()) return;
            ContinueTaskbarDragDirectly();
            return;
        }
        freeCustomDragging = true;
        UpdateFreeCustomDrag();
    }

    void Header_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsCollapsedMediaControlSource(e.OriginalSource as DependencyObject)) return;
        if (suppressDoubleClickMouseUp)
        {
            suppressDoubleClickMouseUp = false;
            dragging = false;
            dragged = false;
            freeCustomDragging = false;
            Header.ReleaseMouseCapture();
            ClearTaskbarDragConstraints();
            e.Handled = true;
            return;
        }
        if (freeCustomDragging)
        {
            FinishFreeCustomDrag();
            e.Handled = true;
            return;
        }
        if (taskbarCustomDragging)
        {
            FinishTaskbarCustomDrag();
            e.Handled = true;
            return;
        }
        if (IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        if (dragging && !dragged)
        {
            ScheduleHeaderSingleClick();
            e.Handled = true;
        }
        dragging = false;
        Header.ReleaseMouseCapture();
        RestoreCollapsedMediaHoverAfterHeaderCapture();
        if (dragged) UpdatePlacementAfterDrag();
        ClearTaskbarDragConstraints();
    }

    static bool IsInteractiveSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = current switch
        {
            FrameworkElement element when element.Parent is not null => element.Parent,
            FrameworkContentElement element => element.Parent,
            _ => VisualTreeHelper.GetParent(current)
        })
        {
            if (current is Button or TextBox or System.Windows.Controls.ComboBox or System.Windows.Controls.Primitives.ScrollBar) return true;
        }
        return false;
    }

    static bool IsCollapsedMediaControlSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = current switch
        {
            FrameworkElement element when element.Parent is not null => element.Parent,
            FrameworkContentElement element => element.Parent,
            _ => VisualTreeHelper.GetParent(current)
        })
        {
            if (current is FrameworkElement
                {
                    Name: "CollapsedMediaTrackButton" or "CollapsedMediaPrevious" or
                    "CollapsedMediaPlayPause" or "CollapsedMediaNext"
                })
                return true;
        }
        return false;
    }

    void CollapseWhenForegroundMovesToAnotherProcess()
    {
        if (HasOpenDropDown())
        {
            collapseTimer.Stop();
            return;
        }
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero) return;

        GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);
        if (foregroundProcessId == 0 || foregroundProcessId == (uint)Environment.ProcessId) return;

        headerSingleClickTimer.Stop();
        if (expanded) AutoCollapse();
    }

    void ScheduleHeaderSingleClick()
    {
        headerSingleClickTimer.Stop();
        headerSingleClickTimer.Interval = TimeSpan.FromMilliseconds(
            HeaderDoubleClickMilliseconds);
        headerSingleClickTimer.Start();
    }

    void ToggleExpanded()
    {
        if (expanded) Collapse();
        else Expand();
    }

    void Expand()
    {
        if (expanded) return;
        SetTopDockFolded(false);
        DashboardTabs.Visibility = Visibility.Visible;
        ExpandedScrollViewer.ScrollToTop();
        expanded = true;
        AnimateExpandedState(true);
        Touch();
        if (placement == IslandPlacement.Top && !pointerHover)
            collapseTimer.Start();
    }

    void ExpandedPin_Click(object sender, RoutedEventArgs e) =>
        SetExpandedPinState(expandedPinState == ExpandedPinState.Normal
            ? ExpandedPinState.KeepExpanded
            : ExpandedPinState.Normal);

    void SetExpandedPinState(ExpandedPinState state)
    {
        expandedPinState = state;
        var keepExpanded = state == ExpandedPinState.KeepExpanded;
        ExpandedPinOutline.Visibility = keepExpanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandedPinSolid.Visibility = keepExpanded ? Visibility.Visible : Visibility.Collapsed;
        SetThemeResource(
            ExpandedPinOutline,
            System.Windows.Shapes.Path.StrokeProperty,
            keepExpanded ? "Brush.Accent" : "Brush.TextSecondary");
        SetThemeResource(
            ExpandedPinSolid,
            System.Windows.Shapes.Path.FillProperty,
            keepExpanded ? "Brush.Accent" : "Brush.TextSecondary");
        if (keepExpanded)
            collapseTimer.Stop();
        else if (expanded && !pointerHover) ScheduleMouseLeaveCollapse();
    }

    void AutoCollapse()
    {
        if (expandedPinState == ExpandedPinState.Normal) Collapse();
    }

    void Collapse()
    {
        SetExpandedPinState(ExpandedPinState.Normal);
        if (!expanded) return;
        expanded = false;
        AnimateExpandedState(false);
        collapseTimer.Stop();
    }

    void MaintainTaskbarHeaderAnchor()
    {
        if (!taskbarHeightAnimationActive || updatingTaskbarHeaderAnchor) return;
        if (placement != IslandPlacement.Taskbar)
        {
            taskbarHeightAnimationActive = false;
            return;
        }

        try
        {
            var headerOffset = Header.TranslatePoint(new System.Windows.Point(0, 0), this).Y;
            var targetTop = IslandPlacementGeometry.WindowTopForHeaderAnchor(
                taskbarHeaderAnchorTop,
                headerOffset);
            if (!double.IsFinite(targetTop) || Math.Abs(Top - targetTop) < 0.05) return;
            updatingTaskbarHeaderAnchor = true;
            Top = targetTop;
        }
        catch (InvalidOperationException)
        {
            taskbarHeightAnimationActive = false;
        }
        finally
        {
            updatingTaskbarHeaderAnchor = false;
        }
    }

    double AvailableExpandedContentHeight(bool hasGeometry, MonitorGeometry geometry, Rect headerRect)
    {
        var maximum = hasGeometry
            ? placement == IslandPlacement.Taskbar
                ? IslandPlacementGeometry.TaskbarExpandedContentMaxHeight(
                    geometry.Bounds,
                    geometry.Taskbar,
                    headerRect.Top,
                    Header.ActualHeight,
                    12)
                : geometry.WorkArea.Bottom - headerRect.Bottom - 12
            : SystemParameters.WorkArea.Bottom - headerRect.Bottom - 12;
        return double.IsFinite(maximum)
            ? Math.Max(0, maximum) : 0;
    }

    void ResizeExpandedToContent()
    {
        if (!expanded || ExpandedContent.Visibility != Visibility.Visible) return;
        var resizeVersion = ++expandedContentResizeVersion;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (resizeVersion != expandedContentResizeVersion ||
                !expanded ||
                ExpandedContent.Visibility != Visibility.Visible)
                return;

            ExpandedScrollViewer.BeginAnimation(MaxHeightProperty, null);
            ExpandedScrollViewer.MinHeight = 0;
            ExpandedContent.Measure(new System.Windows.Size(ExpandedWidth, double.PositiveInfinity));
            var hasGeometry = TryGetMonitorGeometry(ScreenForHeader(), out var geometry);
            var targetHeight = Math.Min(
                ExpandedContent.DesiredSize.Height,
                AvailableExpandedContentHeight(hasGeometry, geometry, HeaderScreenRect()));
            ExpandedScrollViewer.MaxHeight = targetHeight;
            if (placement == IslandPlacement.Taskbar)
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(AlignTaskbarAfterLayout));
        }));
    }

    void AnimateExpandedState(bool expand)
    {
        UpdateIslandBackground();
        var contentAnimationVersion = ++expandedContentAnimationVersion;
        var duration = SystemParameters.ClientAreaAnimation
            ? TimeSpan.FromMilliseconds(ExpansionDurationMilliseconds(
                placement == IslandPlacement.Taskbar,
                expand))
            : TimeSpan.FromMilliseconds(1);
        var taskbarExpansion = expand && placement == IslandPlacement.Taskbar;
        IEasingFunction easing = expand
            ? taskbarExpansion
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new BackEase { Amplitude = .16, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseInOut };
        var hasGeometry = TryGetMonitorGeometry(ScreenForHeader(), out var geometry);
        var fromWidth = ActualWidth > 0 && double.IsFinite(ActualWidth)
            ? ActualWidth : Width;
        if (!double.IsFinite(fromWidth) || fromWidth <= 0) fromWidth = CollapsedWidth;
        var targetWidth = expand ? ExpandedWidth : CollapsedWidthForContent();
        if (!double.IsFinite(targetWidth) || targetWidth <= 0) targetWidth = CollapsedWidth;
        var desiredLeft = WindowResizeCenter(fromWidth) - targetWidth / 2;
        var targetLeft = placement == IslandPlacement.Taskbar && hasGeometry
            ? TaskbarLeftForWidth(geometry, targetWidth)
            : hasGeometry
                ? IslandPlacementGeometry.ClampLeft(desiredLeft, targetWidth, geometry.Bounds)
                : desiredLeft;
        var fromHeight = ExpandedScrollViewer.ActualHeight;
        var headerRect = HeaderScreenRect();
        if (!double.IsFinite(fromHeight) || fromHeight < 0) fromHeight = 0;

        var targetHeight = 0d;
        var targetMinimumHeight = 0d;
        if (expand)
        {
            ExpandedScrollViewer.BeginAnimation(MaxHeightProperty, null);
            ExpandedScrollViewer.MinHeight = 0;
            ExpandedScrollViewer.MaxHeight = fromHeight;
            ExpandedContent.Visibility = Visibility.Visible;
            ExpandedContent.Measure(new System.Windows.Size(ExpandedWidth, double.PositiveInfinity));
            var contentMaxHeight = AvailableExpandedContentHeight(hasGeometry, geometry, headerRect);
            targetHeight = Math.Min(ExpandedContent.DesiredSize.Height, contentMaxHeight);
            if (!double.IsFinite(targetHeight) || targetHeight < 0) targetHeight = 0;
        }
        else ExpandedScrollViewer.MinHeight = 0;

        var heightAnimationVersion = ++taskbarHeightAnimationVersion;
        taskbarHeightAnimationActive =
            placement == IslandPlacement.Taskbar &&
            hasGeometry;
        if (taskbarHeightAnimationActive)
        {
            taskbarHeaderAnchorTop = geometry.Taskbar is Rect taskbar
                ? IslandPlacementGeometry.TaskbarHeaderTop(taskbar, Header.ActualHeight)
                : headerRect.Top;
            UpdateLayout();
            MaintainTaskbarHeaderAnchor();
        }

        AnimateWindowBounds(targetWidth, targetLeft, duration, easing);

        var contentAnimation = new DoubleAnimation { From = fromHeight, To = targetHeight, Duration = duration, EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        contentAnimation.Completed += (_, _) =>
        {
            if (contentAnimationVersion != expandedContentAnimationVersion) return;
            ExpandedScrollViewer.BeginAnimation(MaxHeightProperty, null);
            ExpandedScrollViewer.MaxHeight = targetHeight;
            if (expand) ExpandedScrollViewer.MinHeight = targetMinimumHeight;
            else ExpandedContent.Visibility = Visibility.Collapsed;
            if (!expand && placement == IslandPlacement.Top && !pointerHover)
                SetTopDockFolded(true);
            if (heightAnimationVersion == taskbarHeightAnimationVersion && taskbarHeightAnimationActive)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    if (heightAnimationVersion != taskbarHeightAnimationVersion) return;
                    MaintainTaskbarHeaderAnchor();
                    taskbarHeightAnimationActive = false;
                    if (placement == IslandPlacement.Taskbar) AlignTaskbarAfterLayout();
                }));
            }
            else if (placement == IslandPlacement.Taskbar)
            {
                Dispatcher.BeginInvoke(AlignTaskbarAfterLayout);
            }
        };
        ExpandedScrollViewer.BeginAnimation(MaxHeightProperty, contentAnimation);

        var chevronAnimation = new DoubleAnimation { To = expand ? 90 : 0, Duration = duration, EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        chevronAnimation.Completed += (_, _) =>
        {
            if (contentAnimationVersion != expandedContentAnimationVersion) return;
            ChevronRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            ChevronRotate.Angle = expand ? 90 : 0;
        };
        ChevronRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, chevronAnimation);
    }

    internal static int ExpansionDurationMilliseconds(bool taskbarDocked, bool expand) =>
        expand ? taskbarDocked ? 140 : 230 : 190;

    double WindowResizeCenter(double currentWidth)
    {
        // Native window moves round Left to pixels; never feed that error into the next resize.
        if (!double.IsFinite(windowResizeCenterX))
            windowResizeCenterX = (double.IsFinite(Left) ? Left : 0) + currentWidth / 2;
        return windowResizeCenterX;
    }

    void AnimateWindowBounds(double targetWidth, double targetLeft, TimeSpan duration, IEasingFunction easing)
    {
        windowResizeCenterX = targetLeft + targetWidth / 2;
        var version = ++windowBoundsAnimationVersion;
        AnimateWindowProperty(WidthProperty, targetWidth, duration, easing, version);
        AnimateWindowProperty(LeftProperty, targetLeft, duration, easing, version);
    }

    void AnimateWindowProperty(DependencyProperty property, double target, TimeSpan duration, IEasingFunction easing, int version)
    {
        if (!double.IsFinite(target)) return;
        var from = (double)GetValue(property);
        if (!double.IsFinite(from))
        {
            BeginAnimation(property, null);
            SetValue(property, target);
            return;
        }
        var animation = new DoubleAnimation { From = from, To = target, Duration = duration, EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) =>
        {
            if (version != windowBoundsAnimationVersion) return;
            BeginAnimation(property, null);
            SetValue(property, target);
        };
        BeginAnimation(property, animation);
    }

    void ResizeCollapsedToContent()
    {
        if (expanded || dragging || freeCustomDragging || taskbarCustomDragging) return;
        var targetWidth = CollapsedWidthForContent();
        if (Math.Abs(targetWidth - Width) < 0.5) return;
        var desiredLeft = WindowResizeCenter(Width) - targetWidth / 2;
        var hasGeometry = TryGetMonitorGeometry(ScreenForHeader(), out var geometry);
        var targetLeft = placement == IslandPlacement.Taskbar && hasGeometry
            ? TaskbarLeftForWidth(geometry, targetWidth)
            : hasGeometry
                ? IslandPlacementGeometry.ClampLeft(desiredLeft, targetWidth, geometry.Bounds)
                : desiredLeft;
        var duration = TimeSpan.FromMilliseconds(400);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        AnimateWindowBounds(targetWidth, targetLeft, duration, easing);
    }

    double CollapsedWidthForContent()
    {
        if (CollapsedMedia.Visibility == Visibility.Visible) return CollapsedWidth;
        DefaultHeaderLeft.Measure(new System.Windows.Size(double.PositiveInfinity, Header.Height));
        ClockGroup.Measure(new System.Windows.Size(double.PositiveInfinity, Header.Height));
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var compactWidth =
            HorizontalLayoutThickness(Outer.Margin, dpiScale) +
            HorizontalLayoutThickness(MainBorder.BorderThickness, dpiScale) +
            DefaultHeaderLeft.DesiredSize.Width +
            ClockGroup.DesiredSize.Width;
        return Math.Max(CollapsedIslandDisplayPolicy.MinimumWidth, compactWidth);
    }

    internal static double HorizontalLayoutThickness(Thickness thickness, double dpiScale) =>
        (Math.Round(thickness.Left * dpiScale) + Math.Round(thickness.Right * dpiScale)) / dpiScale;

    void Touch() => collapseTimer.Stop();

    void ScheduleMouseLeaveCollapse()
    {
        collapseTimer.Stop();
        if (expandedPinState != ExpandedPinState.Normal) return;
        collapseTimer.Start();
    }

    void Island_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        topDockHoverExitTimer.Stop();
        pointerHover = true;
        collapseTimer.Stop();
        SetTopDockFolded(false);
        ResizeCollapsedToContent();
    }

    void Island_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        collapsedMediaControlsPinned = false;
        AnimateCollapsedMediaControls(false);
        if (!expanded && placement == IslandPlacement.Top)
        {
            topDockHoverExitTimer.Stop();
            topDockHoverExitTimer.Start();
            return;
        }

        pointerHover = false;
        if (HasOpenDropDown())
        {
            collapseTimer.Stop();
            return;
        }
        if (!expanded)
        {
            ResizeCollapsedToContent();
            return;
        }
        ScheduleMouseLeaveCollapse();
    }

    void ConfirmTopDockHoverExit()
    {
        topDockHoverExitTimer.Stop();
        if (placement != IslandPlacement.Top || expanded || IsMouseOver) return;
        pointerHover = false;
        SetTopDockFolded(true);
        ResizeCollapsedToContent();
    }

    void RefreshTopDockAutoFold()
    {
        pointerHover = IsMouseOver;
        if (placement != IslandPlacement.Top || expanded) return;
        SetTopDockFolded(!pointerHover);
    }

    void SetTopDockFolded(bool folded)
    {
        folded = folded && collapsedPreferences.IslandTopDockAutoFold &&
                 placement == IslandPlacement.Top && !expanded && !pointerHover &&
                 !(musicModeActive && media.Current is { IsMusic: true });
        if (folded == topDockFolded) return;

        var currentTop = Top;
        BeginAnimation(TopProperty, null);
        if (!double.IsFinite(currentTop))
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
            currentTop = TryGetMonitorGeometry(screen, out var geometry)
                ? geometry.WorkArea.Top + 10 : 10;
        }
        Top = currentTop;
        var headerHeight = Header.ActualHeight > 0 ? Header.ActualHeight : Header.Height;
        var offset = Math.Max(0, headerHeight - TopDockVisibleHeight);
        if (folded)
        {
            topDockUnfoldedTop = currentTop;
            TopDockStatusLight.Visibility = Visibility.Collapsed;
        }

        var targetTop = folded
            ? topDockUnfoldedTop - offset
            : double.IsFinite(topDockUnfoldedTop) ? topDockUnfoldedTop : currentTop;
        topDockFolded = folded;
        AnimateTopDock(targetTop, folded);
    }

    void AnimateTopDock(double targetTop, bool folded)
    {
        if (!double.IsFinite(targetTop)) return;
        var version = ++topDockAnimationVersion;
        var fromTop = Top;
        BeginAnimation(TopProperty, null);
        MainBorder.BeginAnimation(OpacityProperty, null);
        if (!double.IsFinite(fromTop)) fromTop = targetTop;
        Top = fromTop;
        Top = targetTop;

        if (!folded)
        {
            ShowIslandSurface();
            topDockUnfoldedTop = double.NaN;
            if (!IsLoaded)
            {
                MainBorder.Opacity = 1;
                return;
            }

            MainBorder.Opacity = 1;
            var appearance = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(60),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            appearance.Completed += (_, _) =>
            {
                if (version != topDockAnimationVersion) return;
                MainBorder.BeginAnimation(OpacityProperty, null);
                MainBorder.Opacity = 1;
                UpdateHeaderTextRendering();
            };
            UpdateHeaderTextRendering(false);
            MainBorder.BeginAnimation(OpacityProperty, appearance);
            return;
        }

        if (!IsLoaded || Math.Abs(fromTop - targetTop) < 0.5)
        {
            ShowFoldedStatusStrip();
            return;
        }

        var animation = new DoubleAnimation
        {
            From = fromTop,
            To = targetTop,
            Duration = TimeSpan.FromMilliseconds(90),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (version != topDockAnimationVersion) return;
            BeginAnimation(TopProperty, null);
            Top = targetTop;
            ShowFoldedStatusStrip();
        };
        BeginAnimation(TopProperty, animation);
    }

    void ShowFoldedStatusStrip()
    {
        MainBorder.BeginAnimation(OpacityProperty, null);
        MainBorder.Opacity = 1;
        MainBorder.Background = Brushes.Transparent;
        IslandStaticRim.Visibility = Visibility.Collapsed;
        UpdateHeaderTextRendering();
        MainBorder.BorderThickness = new Thickness(0);
        TopDockStatusLight.Visibility = Visibility.Visible;
        TopDockStatusPulse.BeginAnimation(OpacityProperty, null);
        TopDockStatusPulse.Opacity = 1;
        TopDockStatusPulse.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 1,
            To = .18,
            Duration = TimeSpan.FromMilliseconds(1800),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    void ShowIslandSurface()
    {
        TopDockStatusPulse.BeginAnimation(OpacityProperty, null);
        TopDockStatusPulse.Opacity = 1;
        TopDockStatusLight.Visibility = Visibility.Collapsed;
        UpdateIslandBackground();
        UpdateHeaderTextRendering();
        IslandStaticRim.Visibility = !preferences.Load().GlowBorderEnabled || !SystemParameters.ClientAreaAnimation
            ? Visibility.Visible : Visibility.Collapsed;
        MainBorder.BorderThickness = new Thickness(1);
    }

    void ResetTopDockFold()
    {
        ++topDockAnimationVersion;
        BeginAnimation(TopProperty, null);
        MainBorder.BeginAnimation(OpacityProperty, null);
        MainBorder.Opacity = 1;
        if (topDockFolded && double.IsFinite(topDockUnfoldedTop))
            Top = topDockUnfoldedTop;
        topDockFolded = false;
        topDockUnfoldedTop = double.NaN;
        ShowIslandSurface();
    }

    bool HasOpenDropDown() => HasOpenDropDown(ExpandedContent);

    static bool HasOpenDropDown(DependencyObject? element)
    {
        if (element is System.Windows.Controls.ComboBox { IsDropDownOpen: true }) return true;
        if (element is null) return false;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (HasOpenDropDown(VisualTreeHelper.GetChild(element, index))) return true;
        return false;
    }

    void ExpandedContent_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) => Touch();
    void QuickAddInput_TextChanged(object sender, TextChangedEventArgs e) => Touch();
    void TimeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => Touch();
    void TimeSelector_DropDownOpened(object? sender, EventArgs e) => Touch();

    void TimeSelector_DropDownClosed(object? sender, EventArgs e)
    {
        pointerHover = IsMouseOver;
        if (!pointerHover && expanded)
            ScheduleMouseLeaveCollapse();
    }

    void InitializeQuickAdd()
    {
        for (var hour = 0; hour < 24; hour++) HourSelector.Items.Add(new TimePart(hour));
        for (var minute = 0; minute < 60; minute++) MinuteSelector.Items.Add(new TimePart(minute));
        HourSelector.SelectedIndex = DateTime.Now.Hour;
        MinuteSelector.SelectedIndex = DateTime.Now.Minute;
        UpdateQuickAddType();
    }

    void ReminderType_Click(object sender, RoutedEventArgs e)
    {
        addingReminder = true;
        UpdateQuickAddType();
        Touch();
    }

    void TodoType_Click(object sender, RoutedEventArgs e)
    {
        addingReminder = false;
        UpdateQuickAddType();
        Touch();
    }

    void UpdateQuickAddType()
    {
        SetThemeResource(
            ReminderTypeButton,
            Button.BackgroundProperty,
            addingReminder ? "Brush.AccentSoft" : "Brush.Control");
        SetThemeResource(
            ReminderTypeButton,
            Button.ForegroundProperty,
            addingReminder ? "Brush.TextPrimary" : "Brush.TextSecondary");
        SetThemeResource(
            TodoTypeButton,
            Button.BackgroundProperty,
            addingReminder ? "Brush.Control" : "Brush.AccentSoft");
        SetThemeResource(
            TodoTypeButton,
            Button.ForegroundProperty,
            addingReminder ? "Brush.TextSecondary" : "Brush.TextPrimary");
        UpdateQuickAddDate();
    }

    void UpdateQuickAddDate() => QuickAddDate.Text = $"{selectedDate:M'\u6708'd'\u65e5'} · {(addingReminder ? "\u63d0\u9192" : "\u5f85\u529e")}";

    void QuickAdd_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(QuickAddInput.Text) || HourSelector.SelectedItem is not TimePart hour || MinuteSelector.SelectedItem is not TimePart minute) return;
        var scheduledAt = selectedDate.Date.AddHours(hour.Value).AddMinutes(minute.Value);
        if (addingReminder)
        {
            data.SaveReminder(QuickAddInput.Text, null, scheduledAt);
            reminders.RefreshSchedule();
        }
        else data.Save(QuickAddInput.Text, null, scheduledAt, null);
        QuickAddInput.Clear();
        Touch();
    }

    sealed record TimePart(int Value)
    {
        public override string ToString() => Value.ToString("00");
    }

    void CompleteTodo_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AgendaItem item || item.IsCompleted) return;
        data.Complete(item.Id);
        reminders.Cancel(item);
        Touch();
    }

    void DismissReminderBanner_Click(object sender, RoutedEventArgs e)
    {
        ClearReminderBanner();
        Refresh();
        Touch();
    }

    void DeleteAgenda_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AgendaItem item) return;
        reminders.Delete(item);
        Touch();
    }

    void Day_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DateTime day)
        {
            selectedDate = day.Date;
            BuildCalendar();
            BuildDayAgenda();
        }
        Touch();
    }

    void PreviousMonth_Click(object sender, RoutedEventArgs e)
    {
        displayedMonth = displayedMonth.AddMonths(-1);
        selectedDate = displayedMonth;
        BuildCalendar();
        BuildDayAgenda();
        Touch();
    }

    void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        displayedMonth = displayedMonth.AddMonths(1);
        selectedDate = displayedMonth;
        BuildCalendar();
        BuildDayAgenda();
        Touch();
    }

    void UpdatePlacementAfterDrag()
    {
        var screen = ScreenForHeader();
        if (!TryGetMonitorGeometry(screen, out var geometry)) return;
        ClampWindowToBounds(geometry.Bounds);
        var header = HeaderScreenRect();

        if (geometry.Taskbar is Rect taskbar)
        {
            var taskbarHeaderHeight = Math.Min(43, Math.Max(30, taskbar.Height - 2));
            var targetHeaderTop = IslandPlacementGeometry.TaskbarHeaderTop(taskbar, taskbarHeaderHeight);
            if (IslandPlacementGeometry.ShouldSnap(
                    header.Top - targetHeaderTop,
                    placement == IslandPlacement.Taskbar,
                    SnapThreshold,
                    UnsnapThreshold))
            {
                placement = IslandPlacement.Taskbar;
                taskbarMonitorDeviceName = screen.DeviceName;
                taskbarHorizontalRatio = IslandPlacementGeometry.NormalizeHorizontalCenter(
                    header.Left + header.Width / 2,
                    geometry.Bounds);
                PositionAtTaskbar(geometry);
                PersistTaskbarPlacement(true);
                return;
            }
        }

        if (IslandPlacementGeometry.ShouldSnap(
                header.Top - geometry.WorkArea.Top,
                placement == IslandPlacement.Top,
                SnapThreshold,
                UnsnapThreshold))
        {
            if (placement == IslandPlacement.Taskbar) PersistTaskbarPlacement(false);
            placement = IslandPlacement.Top;
            ApplyPlacementVisuals();
            UpdateLayout();
            Top = geometry.WorkArea.Top;
            windowResizeCenterX = geometry.WorkArea.Left + geometry.WorkArea.Width / 2;
            Left = windowResizeCenterX - ActualWidth / 2;
            pointerHover = false;
            if (expanded) Collapse();
            else SetTopDockFolded(true);
            return;
        }

        if (placement == IslandPlacement.Taskbar) PersistTaskbarPlacement(false);
        placement = IslandPlacement.Free;
        ApplyPlacementVisuals();
        UpdateLayout();
        ClampWindowToBounds(geometry.Bounds);
    }

    void OpenAssistant()
    {
        Collapse();
        Dispatcher.BeginInvoke(() => OpenRequested?.Invoke(this, EventArgs.Empty));
    }

    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    void ManageItems_Click(object sender, RoutedEventArgs e) => OpenManagement();

    void OpenSettings()
    {
        Collapse();
        Dispatcher.BeginInvoke(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
    }

    void OpenManagement()
    {
        Collapse();
        Dispatcher.BeginInvoke(() => ManageRequested?.Invoke(this, EventArgs.Empty));
    }

    void OpenAssistant_Click(object sender, RoutedEventArgs e) => OpenAssistant();
}
