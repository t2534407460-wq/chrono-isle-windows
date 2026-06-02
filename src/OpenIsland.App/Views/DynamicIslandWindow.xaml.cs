using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using OpenIsland.App.ViewModels;

namespace OpenIsland.App.Views;

public partial class DynamicIslandWindow : Window
{
    private readonly DynamicIslandViewModel _viewModel;
    private bool _isDragging;
    private Point _dragStartPoint;

    // 普通模式岛宽 vs 展开/权限提示时的拓宽宽度。等"丝滑"动画 0.5s ease-out 在两者间过渡。
    // 展开后宽度是折叠时的两倍（320→640），提供更宽敞的操作空间。
    private const double NormalWidth = 320;
    private const double ExpandedWidth = 640;  // 展开后宽度
    private const double PermissionWidth = 640;

    // Notch 形态参数：仿 MacBook 刘海的横条；snap 阈值 = 拖到距屏顶 28px 内放手就吸附。
    private const double NotchWidth = 480;
    private const double NotchSnapThreshold = 28;
    private const double NotchUnsnapThreshold = 48;

    public DynamicIslandWindow(DynamicIslandViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Width = NormalWidth;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        // VM 请求播放一次小章鱼动画（媒体控制 → headphones 等）→ 转给精灵控件
        _viewModel.PlaySprite += name => Dispatcher.BeginInvoke(() => StatusSprite.PlayOnce(name));
        Loaded += (_, _) => PositionAtTopCenter();
    }

    /// <summary>圆形关闭按钮：小章鱼挥手拜拜，约 3 秒后隐藏灵动岛（托盘菜单可再显示）。</summary>
    private void CloseIsland_Click(object sender, RoutedEventArgs e)
    {
        StatusSprite.PlayOnce("byebye"); // 30 帧 ≈ 3s 的挥手告别
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3100) };
        t.Tick += (_, _) => { t.Stop(); Hide(); };
        t.Start();
    }

    private void PositionAtTopCenter()
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Left + (screen.Width - ActualWidth) / 2;
        Top = screen.Top + 10;
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _dragStartPoint = e.GetPosition(this);
        (sender as UIElement)?.CaptureMouse();
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            var currentPos = e.GetPosition(this);
            var delta = currentPos - _dragStartPoint;
            if (Math.Abs(delta.X) > 5 || Math.Abs(delta.Y) > 5)
            {
                _isDragging = false;
                (sender as UIElement)?.ReleaseMouseCapture();
                DragMove(); // 阻塞直到鼠标松开
                CheckNotchSnap();
            }
        }
    }

    /// <summary>
    /// 拖拽结束后检查是否要吸附到 Notch / 离开 Notch。
    /// - 当前不在 Notch 模式 + 距屏顶 < 28px 放手 → 吸附为 Notch
    /// - 当前在 Notch 模式 + 距屏顶 > 48px 放手 → 翻回默认岛
    /// - 当前在 Notch 模式 + 仍贴顶 → 复位到 Top=0 不让它斜挂
    /// </summary>
    private void CheckNotchSnap()
    {
        var screenTop = SystemParameters.WorkArea.Top;
        var distFromTop = Top - screenTop;

        if (!_viewModel.IsNotchMode && distFromTop < NotchSnapThreshold)
        {
            EnterNotchMode();
        }
        else if (_viewModel.IsNotchMode && distFromTop > NotchUnsnapThreshold)
        {
            ExitNotchMode();
        }
        else if (_viewModel.IsNotchMode)
        {
            Top = screenTop; // 紧贴顶
        }
    }

    private void EnterNotchMode()
    {
        _viewModel.IsNotchMode = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(450);

        // Notch 形态 = 默认岛粘到屏顶居中，不改 Width（让原 320/权限 640 都能保留所有
        // 内部功能：展开箭头、会话列表、状态灯、权限三键）。只动 Top → 0、Left → 居中。
        var screenCenter = SystemParameters.WorkArea.Left + SystemParameters.WorkArea.Width / 2;
        var currentWidth = ActualWidth > 0 ? ActualWidth : Width;

        AnimateWindowProp(LeftProperty, screenCenter - currentWidth / 2, dur, ease);
        AnimateWindowProp(TopProperty, SystemParameters.WorkArea.Top, dur, ease);
    }

    private void ExitNotchMode()
    {
        _viewModel.IsNotchMode = false;
        // 不改 Width / Top / Left —— 用户拖出顶部后位置由 DragMove 已经定位，
        // 这里只翻 IsNotchMode 让外形（Margin/CornerRadius）通过 DataTrigger 自然过渡。
    }

    private void Header_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            // 短点（非拖拽）= 用户意图"点 Open Island"：一键清空下面所有栏目，谁活动谁再回来，
            // 然后照旧 toggle 展开/收起（不破坏看列表的能力）。Notch 与默认形态行为一致。
            // 清空逻辑见 DynamicIslandViewModel.ClearAllSessions（复用单卡叉号的 dismiss
            // 状态机，未答的权限 prompt 不会被清掉）。
            // A genuine tap (not a drag) on the "Open Island" header clears the session
            // list (active ones reappear on their own) and then toggles expand/collapse as
            // before — see DynamicIslandViewModel.OnHeaderTapped / ClearAllSessions.
            // 例外：点在最左的小章鱼上 → 放一次"龟派气功"彩蛋，不展开/收起。
            // （Grid 在 MouseDown 抓了鼠标，MouseUp 总落到 Grid，小章鱼自己的事件收不到，
            //   所以在这里按命中位置分流。）
            var pOnSprite = e.GetPosition(StatusSprite);
            bool onSprite = pOnSprite.X >= 0 && pOnSprite.Y >= 0
                            && pOnSprite.X < StatusSprite.ActualWidth
                            && pOnSprite.Y < StatusSprite.ActualHeight;
            if (onSprite)
                StatusSprite.PlayOnce("kamehameha");
            else
                _viewModel.OnHeaderTapped();
        }
        _isDragging = false;
        (sender as UIElement)?.ReleaseMouseCapture();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DynamicIslandViewModel.IsExpanded))
            Dispatcher.BeginInvoke(() => AnimateExpand(_viewModel.IsExpanded));
        else if (e.PropertyName == nameof(DynamicIslandViewModel.IsPermissionMode))
            Dispatcher.BeginInvoke(() => AnimateWidth(_viewModel.IsPermissionMode));
        else if (e.PropertyName == nameof(DynamicIslandViewModel.IsNotificationVisible))
            Dispatcher.BeginInvoke(() => AnimateNotification(_viewModel.IsNotificationVisible));
    }

    /// <summary>
    /// 任务完成通知展开/收起动画。
    /// 类似展开状态，但只显示通知内容，不影响现有的展开/收起功能。
    /// </summary>
    private void AnimateNotification(bool show)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(300);

        if (show)
        {
            // 通知出现：使用固定宽度（与折叠状态一致），让文本自动换行
            double targetWidth = NormalWidth;

            // 从中心向外扩展宽度（如果当前宽度不同）
            double currentWidth = ActualWidth > 0 ? ActualWidth : Width;
            double widthDelta = targetWidth - currentWidth;
            double targetLeft = Left - (widthDelta / 2);

            AnimateWindowProp(WidthProperty, targetWidth, dur, ease);
            AnimateWindowProp(LeftProperty, targetLeft, dur, ease);

            // 展开通知区域：先显示，再动画高度
            NotificationBorder.Visibility = Visibility.Visible;
            NotificationBorder.Height = 0;
            NotificationBorder.BeginAnimation(HeightProperty, new DoubleAnimation
            {
                To = 80, // 增加高度以支持换行显示
                Duration = dur,
                EasingFunction = ease
            });
        }
        else
        {
            // 通知消失：先动画高度到0，再隐藏
            NotificationBorder.BeginAnimation(HeightProperty, new DoubleAnimation
            {
                To = 0,
                Duration = dur,
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            });

            // 动画结束后隐藏
            var hideTimer = new System.Windows.Threading.DispatcherTimer { Interval = dur };
            hideTimer.Tick += (_, _) =>
            {
                hideTimer.Stop();
                NotificationBorder.Visibility = Visibility.Collapsed;
            };
            hideTimer.Start();

            // 恢复原始宽度
            double currentWidth = ActualWidth > 0 ? ActualWidth : Width;
            double widthDelta = NormalWidth - currentWidth;
            double targetLeft = Left - (widthDelta / 2);

            AnimateWindowProp(WidthProperty, NormalWidth, dur, ease);
            AnimateWindowProp(LeftProperty, targetLeft, dur, ease);
        }
    }

    /// <summary>
    /// 动画一个 Window 属性 + 动画结束后清掉动画 / 写本地值。
    /// 必须如此：默认 FillBehavior.HoldEnd 会把属性永久锁在 To 值，DragMove 无法实际移动
    /// （Win32 移了 OS 窗口但 WPF 属性读出来还是动画值）→ 后续 CheckNotchSnap 读 Top 永远
    /// 是 0，永远 unsnap 不出来；Width/Left 同理影响其他交互。
    /// </summary>
    private void AnimateWindowProp(DependencyProperty prop, double target, TimeSpan dur, IEasingFunction ease)
    {
        var anim = new DoubleAnimation
        {
            To = target,
            Duration = dur,
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        anim.Completed += (_, _) =>
        {
            BeginAnimation(prop, null); // 清动画
            SetValue(prop, target);     // 写到本地值，后续读写正常
        };
        BeginAnimation(prop, anim);
    }

    /// <summary>
    /// 权限模式 ↔ 普通模式：岛宽从 320 ↔ 640 拉伸/收缩，并同步移动 Left 让水平中心保持
    /// 不动，避免视觉上"向右滑出"。0.5s CubicEase EaseOut，配合 Visibility 切换内部
    /// 视图——视图切换是瞬时的，但宽度动画让整体过渡显得平滑。
    /// </summary>
    private void AnimateWidth(bool toPermission)
    {
        double targetWidth = toPermission ? PermissionWidth : NormalWidth;
        double currentWidth = ActualWidth > 0 ? ActualWidth : Width;
        double widthDelta = targetWidth - currentWidth;
        double targetLeft = Left - widthDelta / 2;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(500);

        AnimateWindowProp(WidthProperty, targetWidth, dur, ease);
        AnimateWindowProp(LeftProperty, targetLeft, dur, ease);

        // 进入权限模式时强制展开内容区，否则拓宽空岛没意义。
        if (toPermission && !_viewModel.IsExpanded)
        {
            _viewModel.IsExpanded = true; // 这会经 PropertyChanged 调一次 AnimateExpand
        }

        // 注意：权限模式切换时不再调用 AnimateExpand，避免位置计算冲突
        // AnimateExpand 只在 IsExpanded 属性变化时由 OnViewModelPropertyChanged 触发
    }

    private void AnimateExpand(bool expand)
    {
        double targetHeight;
        double targetWidth;
        double currentWidth = ActualWidth > 0 ? ActualWidth : Width;
        double currentLeft = Left;

        if (expand)
        {
            if (_viewModel.IsPermissionMode)
            {
                // 权限面板：直接给兜底大值，不 measure（避免 layout pass 没追上时拿到旧内容尺寸）
                targetHeight = 1200;
                targetWidth = PermissionWidth;
            }
            else
            {
                // 普通模式：展开后宽度是折叠时的两倍（320→640）
                targetWidth = ExpandedWidth;
                // 用目标宽度 measure 内容，确保高度计算正确
                ExpandedContent.Measure(new Size(ExpandedWidth, double.PositiveInfinity));
                targetHeight = ExpandedContent.DesiredSize.Height;
                if (targetHeight < 1) targetHeight = 600;
            }
        }
        else
        {
            targetHeight = 0;
            targetWidth = NormalWidth;
        }

        // 计算新的 Left 位置，实现从中间向两侧展开的效果
        // 展开时：向左扩展一半的宽度增量
        // 收起时：向右收缩一半的宽度增量
        double widthDelta = targetWidth - currentWidth;
        double targetLeft = currentLeft - (widthDelta / 2);

        // 使用更丝滑的动画：400ms + CubicEase EaseInOut
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        // 宽度动画：展开时变宽，收起时变窄
        AnimateWindowProp(WidthProperty, targetWidth, TimeSpan.FromMilliseconds(400), ease);

        // Left 位置动画：实现从中间向两侧展开的效果
        AnimateWindowProp(LeftProperty, targetLeft, TimeSpan.FromMilliseconds(400), ease);

        // 高度动画：与宽度动画同步，400ms 完成
        ExpandedContent.BeginAnimation(MaxHeightProperty, new DoubleAnimation
        {
            To = targetHeight,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = ease
        });

        // Rotate chevron: 90° = right (collapsed), 270° = down (expanded)
        ChevronRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation
            {
                To = expand ? 270 : 90,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = ease
            });
    }
}
