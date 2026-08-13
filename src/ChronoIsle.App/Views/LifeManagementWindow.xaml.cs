using System.IO;
using System.Text;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using MessageBox = System.Windows.MessageBox;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.Productivity;
using ChronoIsle.App.Services.Domain;
using ChronoIsle.App.Services.ImportExport;

namespace ChronoIsle.App.Views;

public partial class LifeManagementWindow : Window
{
    readonly LifeDataService data;
    readonly ReminderService reminders;
    readonly TaskAttributesService taskAttributes;
    readonly FocusService focus;
    readonly MarkdownItemTransferService markdownTransfer;
    readonly HashSet<string> selected = [];
    readonly Dictionary<string, Border> itemRows = [];
    readonly Dictionary<string, Border> itemRowsById = [];
    static readonly string[] CommonCategories = ["工作", "学习", "生活", "健康", "家庭", "财务", "其他"];
    ItemNavigationTarget? itemToLocate;
    bool awaitingConfirmation;
    bool showingArchive;
    bool showingRecommendations = true;
    int recommendationMinutes = 30;
    EnergyLevel recommendationEnergy = EnergyLevel.Medium;

    public LifeManagementWindow(LifeDataService data, ReminderService reminders, FocusService focus, TaskAttributesService taskAttributes, MarkdownItemTransferService markdownTransfer)
    {
        InitializeComponent();
        this.data = data;
        this.reminders = reminders;
        this.focus = focus;
        this.markdownTransfer = markdownTransfer;
        Loaded += (_, _) => RefreshItems();
        this.taskAttributes = taskAttributes;
    }

    void DownloadTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "下载事项导入模板",
            Filter = "Markdown 文件 (*.md)|*.md|所有文件 (*.*)|*.*",
            FileName = "ChronoIsle-事项导入模板.md",
            DefaultExt = ".md",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        SaveMarkdown(dialog.FileName, MarkdownItemTransferService.TemplateMarkdown, "导入模板已保存。");
    }

    void ExportMarkdown_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出事项",
            Filter = "Markdown 文件 (*.md)|*.md|所有文件 (*.*)|*.*",
            FileName = $"ChronoIsle-事项-{DateTime.Now:yyyyMMdd-HHmm}.md",
            DefaultExt = ".md",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        SaveMarkdown(dialog.FileName, markdownTransfer.ExportMarkdown(), "事项已导出。");
    }

    void ImportMarkdown_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入事项",
            Filter = "Markdown 文件 (*.md;*.markdown)|*.md;*.markdown|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var preview = MarkdownItemTransferService.Parse(File.ReadAllText(dialog.FileName, Encoding.UTF8));
            var confirmation = MessageBox.Show(
                this,
                $"将导入 {preview.Items.Count} 项：{preview.Summary}{Environment.NewLine}{Environment.NewLine}导入会创建新事项，不会自动覆盖或去重。是否继续？",
                "确认导入",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes) return;

            var result = markdownTransfer.Import(preview);
            reminders.RefreshSchedule();
            RefreshItems();
            Result.Foreground = new SolidColorBrush(Color.FromRgb(157, 214, 157));
            Result.Text = $"已导入 {result.ImportedCount} 项。";
        }
        catch (MarkdownItemDocumentException exception)
        {
            ShowTransferError($"Markdown 格式无效：{exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowTransferError($"导入失败：{exception.Message}");
        }
    }

    void SaveMarkdown(string path, string content, string successMessage)
    {
        try
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
            Result.Foreground = new SolidColorBrush(Color.FromRgb(157, 214, 157));
            Result.Text = successMessage;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowTransferError($"保存失败：{exception.Message}");
        }
    }

    void ShowTransferError(string message)
    {
        Result.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
        Result.Text = message;
    }
    void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
    public void OpenItem(ItemNavigationTarget target)
    {
        itemToLocate = target;
        showingArchive = false;
        showingRecommendations = false;
        if (IsLoaded) RefreshItems();
    }

    void RefreshItems()
    {
        data.ArchiveCompletedAndOverdue();
        UpdatePageTabs();
        if (showingArchive)
        {
            RefreshArchivedItems();
            return;
        }
        var managed = data.ManagedItems().ToList();
        var recommendedIds = showingRecommendations
            ? taskAttributes.Recommend(recommendationMinutes, recommendationEnergy, DateTimeOffset.UtcNow)
                .Select(item => item.ItemId)
                .ToHashSet(StringComparer.Ordinal)
            : [];
        var visibleItems = showingRecommendations
            ? managed.Where(item => recommendedIds.Contains(item.Id)).ToList()
            : managed;
        var itemIndicators = data.GetManagedItemIndicatorStates(managed, DateTime.Now);
        selected.RemoveWhere(key => !visibleItems.Any(item => Key(item.Id, item.Kind) == key));
        Items.Children.Clear();
        itemRows.Clear();
        itemRowsById.Clear();

        if (showingRecommendations)
        {
            RecommendationSummary.Text = visibleItems.Count == 0
                ? $"没有符合当前条件的待办。可切换到“全部事项”补充预计时长和精力。"
                : $"已为你挑出 {visibleItems.Count} 件：预计不超过 {recommendationMinutes} 分钟，所需精力不高于{TaskDisplayLabels.Energy(recommendationEnergy)}。";
        }

        if (showingRecommendations && visibleItems.Count == 0)
        {
            Items.Children.Add(CreateRecommendationEmptyState());
        }

        foreach (var item in visibleItems)
        {
            var agenda = new AgendaItem(item.Id, item.Kind, item.Title, item.Notes, item.ScheduledAt ?? DateTime.Now, null, item.ScheduledAt, item.IsCompleted, item.Kind == "recurring");
            var row = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8), Tag = agenda };
            SetThemeResource(row, Border.BackgroundProperty, "Brush.Card");
            SetThemeResource(row, Border.BorderBrushProperty, "Brush.Stroke");
            var panel = new Grid();
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            var check = new CheckBox { Style = (Style)FindResource("SelectionBox"), Tag = agenda, IsChecked = selected.Contains(Key(agenda)) };
            check.Checked += (_, _) => SetSelected(agenda, true);
            check.Unchecked += (_, _) => SetSelected(agenda, false);
            Grid.SetColumn(check, 0);
            panel.Children.Add(check);

            var text = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            var title = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var indicator = itemIndicators[item.Id];
            title.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = LifeIslandWindow.IndicatorBrush(indicator), ToolTip = LifeIslandWindow.IndicatorDescription(indicator), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            title.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(title);
            text.Children.Add(SetThemeResource(new TextBlock { Text = ItemDetails(item), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }, TextBlock.ForegroundProperty, "Brush.TextSecondary"));
            Grid.SetColumn(text, 1);
            panel.Children.Add(text);

            if (agenda.Kind == "todo" && !agenda.IsCompleted)
            {
                var startFocus = new Button { Content = "开始专注", Height = 32, Style = (Style)FindResource("Action"), Tag = agenda, ToolTip = "开始 25 分钟专注" };
                SetThemeResource(startFocus, Button.BackgroundProperty, "Brush.AccentSoft");
                SetThemeResource(startFocus, Button.ForegroundProperty, "Brush.Accent");
                startFocus.Click += (sender, _) => StartFocus((AgendaItem)((FrameworkElement)sender).Tag);
                Grid.SetColumn(startFocus, 2);
                panel.Children.Add(startFocus);
            }

            var archive = new Button { Content = "归档", Width = 54, Height = 32, FontWeight = FontWeights.SemiBold, Style = (Style)FindResource("Action"), Tag = agenda, ToolTip = agenda.Kind == "recurring" ? "归档整个周期计划" : "归档事项" };
            archive.Click += (sender, _) => ArchiveOne((AgendaItem)((FrameworkElement)sender).Tag);
            Grid.SetColumn(archive, 3);
            panel.Children.Add(archive);

            row.Child = agenda.Kind is "todo" or "reminder" or "long_term" && taskAttributes.Get(agenda.Id) is { } attributes ? BuildTaskAttributesEditor(panel, attributes) : panel;
            itemRows[Key(agenda)] = row;
            itemRowsById[agenda.Id] = row;
            Items.Children.Add(row);
        }
        UpdateSelectionUi();
        LocateRequestedItem();
    }

    void ActiveTab_Click(object sender, RoutedEventArgs e)
    {
        showingArchive = false;
        showingRecommendations = false;
        selected.Clear();
        RefreshItems();
    }

    void NowTab_Click(object sender, RoutedEventArgs e)
    {
        showingArchive = false;
        showingRecommendations = true;
        selected.Clear();
        RefreshItems();
    }

    void ArchiveTab_Click(object sender, RoutedEventArgs e)
    {
        showingArchive = true;
        showingRecommendations = false;
        selected.Clear();
        RefreshItems();
    }

    void RecommendationContext_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateRecommendationContext();
    }

    void RefreshRecommendation_Click(object sender, RoutedEventArgs e) => UpdateRecommendationContext();

    void UpdateRecommendationContext()
    {
        if (RecommendationMinutes.SelectedItem is ComboBoxItem { Tag: string minutes } && int.TryParse(minutes, out var parsedMinutes))
            recommendationMinutes = parsedMinutes;
        if (RecommendationEnergy.SelectedItem is ComboBoxItem { Tag: string energy } && Enum.TryParse<EnergyLevel>(energy, out var parsedEnergy))
            recommendationEnergy = parsedEnergy;
        showingArchive = false;
        showingRecommendations = true;
        selected.Clear();
        RefreshItems();
    }

    void UpdatePageTabs()
    {
        SetTabState(NowTab, showingRecommendations && !showingArchive);
        SetTabState(ActiveTab, !showingArchive && !showingRecommendations);
        SetTabState(ArchiveTab, showingArchive);
        RecommendationContext.Visibility = showingRecommendations && !showingArchive ? Visibility.Visible : Visibility.Collapsed;
        ArchiveSelected.Visibility = showingArchive ? Visibility.Collapsed : Visibility.Visible;
    }

    void SetTabState(Button tab, bool isSelected)
    {
        SetThemeResource(tab, Button.BackgroundProperty, isSelected ? "Brush.AccentSoft" : "Brush.Surface");
        SetThemeResource(tab, Button.ForegroundProperty, isSelected ? "Brush.Accent" : "Brush.TextPrimary");
    }

    Border CreateRecommendationEmptyState()
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "还没有适合现在完成的事项", FontWeight = FontWeights.SemiBold, FontSize = 14 });
        content.Children.Add(SetThemeResource(new TextBlock
        {
            Text = $"系统只会选普通、未完成、可编辑，预计不超过 {recommendationMinutes} 分钟且所需精力不高于{TaskDisplayLabels.Energy(recommendationEnergy)}的待办。到“全部事项”补充属性后，它会自动出现在这里。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0)
        }, TextBlock.ForegroundProperty, "Brush.TextSecondary"));
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 8), Child = content };
        SetThemeResource(card, Border.BackgroundProperty, "Brush.Surface");
        SetThemeResource(card, Border.BorderBrushProperty, "Brush.Stroke");
        return card;
    }

    static T SetThemeResource<T>(T element, DependencyProperty property, string resourceKey)
        where T : FrameworkElement
    {
        element.SetResourceReference(property, resourceKey);
        return element;
    }

    void RefreshArchivedItems()
    {
        selected.Clear();
        Items.Children.Clear();
        foreach (var item in data.ArchivedTodos())
        {
            var row = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 8)
            };
            SetThemeResource(row, Border.BackgroundProperty, "Brush.Card");
            SetThemeResource(row, Border.BorderBrushProperty, "Brush.Stroke");
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"{item.Title} · {ArchivedKindLabel(item.Kind)}", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(SetThemeResource(new TextBlock
            {
                Text = $"{item.Reason} · 归档于 {item.ArchivedAt:yyyy-MM-dd HH:mm} · 将于 {item.ArchivedAt.AddDays(7):MM-dd HH:mm} 自动删除",
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 10)
            }, TextBlock.ForegroundProperty, "Brush.TextSecondary"));
            var controls = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var restore = new Button { Content = "恢复", Height = 30, Style = (Style)FindResource("Action"), Padding = new Thickness(12, 4, 12, 4) };
            SetThemeResource(restore, Button.BackgroundProperty, "Brush.AccentSoft");
            SetThemeResource(restore, Button.ForegroundProperty, "Brush.Accent");
            if (item.Kind == "recurring")
            {
                restore.Click += (_, _) => RestoreArchivedItem(item, null);
            }
            else
            {
                controls.Children.Add(SetThemeResource(new TextBlock { Text = "恢复日期", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, TextBlock.ForegroundProperty, "Brush.TextSecondary"));
                var date = ArchiveDateSelector(DateTime.Today.AddDays(1));
                restore.Click += (_, _) => RestoreArchivedItem(item, date.Tag is DateTime selectedDate ? selectedDate : null);
                controls.Children.Add(date);
            }
            controls.Children.Add(restore);
            var delete = new Button { Content = "删除", Height = 30, Style = (Style)FindResource("Button.Danger"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
            delete.Click += (_, _) => DeleteArchivedItem(item);
            controls.Children.Add(delete);
            panel.Children.Add(controls);
            row.Child = panel;
            Items.Children.Add(row);
        }
        if (Items.Children.Count == 0)
            Items.Children.Add(SetThemeResource(new TextBlock { Text = "暂无归档事项。", Margin = new Thickness(0, 8, 0, 0) }, TextBlock.ForegroundProperty, "Brush.TextSecondary"));
    }

    Button ArchiveDateSelector(DateTime initialDate)
    {
        var selected = initialDate.Date;
        var button = new Button
        {
            Content = selected.ToString("yyyy-MM-dd"),
            Tag = selected,
            Height = 30,
            Style = (Style)FindResource("Action"),
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 4, 10, 4)
        };
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true
        };
        var calendar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(92, 100, 114)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 6, 0, 0),
            Width = 264
        };
        popup.Child = calendar;
        var displayedMonth = new DateTime(selected.Year, selected.Month, 1);
        void RenderMonth()
        {
            var panel = new StackPanel();
            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var previous = new Button { Content = "‹", Width = 28, Height = 28, Style = (Style)FindResource("Action"), Padding = new Thickness(0) };
            previous.Click += (_, _) => { displayedMonth = displayedMonth.AddMonths(-1); RenderMonth(); };
            var title = new TextBlock { Text = displayedMonth.ToString("yyyy 年 M 月"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var next = new Button { Content = "›", Width = 28, Height = 28, Style = (Style)FindResource("Action"), Padding = new Thickness(0) };
            next.Click += (_, _) => { displayedMonth = displayedMonth.AddMonths(1); RenderMonth(); };
            Grid.SetColumn(title, 1);
            Grid.SetColumn(next, 2);
            header.Children.Add(previous);
            header.Children.Add(title);
            header.Children.Add(next);
            panel.Children.Add(header);

            var weekdays = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 3) };
            foreach (var weekday in new[] { "一", "二", "三", "四", "五", "六", "日" })
                weekdays.Children.Add(new TextBlock { Text = weekday, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 157)), HorizontalAlignment = System.Windows.HorizontalAlignment.Center });
            panel.Children.Add(weekdays);

            var days = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7 };
            var offset = ((int)displayedMonth.DayOfWeek + 6) % 7;
            for (var index = 0; index < offset; index++) days.Children.Add(new Border { Height = 28 });
            for (var day = 1; day <= DateTime.DaysInMonth(displayedMonth.Year, displayedMonth.Month); day++)
            {
                var value = displayedMonth.AddDays(day - 1);
                var cell = new Button { Content = day.ToString(), Height = 28, Style = (Style)FindResource("Action"), Padding = new Thickness(0), Margin = new Thickness(1) };
                if (value == selected) cell.Background = new SolidColorBrush(Color.FromRgb(70, 70, 74));
                cell.Click += (_, _) =>
                {
                    selected = value;
                    button.Tag = selected;
                    button.Content = selected.ToString("yyyy-MM-dd");
                    popup.IsOpen = false;
                };
                days.Children.Add(cell);
            }
            panel.Children.Add(days);
            calendar.Child = panel;
        }
        RenderMonth();
        button.Click += (_, _) => popup.IsOpen = !popup.IsOpen;
        return button;
    }

    void RestoreArchivedItem(ArchivedTodoItem item, DateTime? selectedDate)
    {
        if (item.Kind is not ("recurring" or "long_term") && selectedDate is null)
        {
            Result.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            Result.Text = "请选择恢复日期。";
            return;
        }
        var time = item.DueAt?.TimeOfDay ?? TimeSpan.FromHours(9);
        var scheduledAt = selectedDate?.Date.Add(time);
        if (!data.RestoreArchivedItem(item.Id, scheduledAt))
        {
            Result.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            Result.Text = "恢复日期需要晚于当前时间。";
            return;
        }
        Result.Foreground = new SolidColorBrush(Color.FromRgb(157, 214, 157));
        reminders.RefreshSchedule();
        Result.Text = item.Kind == "recurring" ? "已恢复周期提醒。" : $"已恢复到 {scheduledAt:yyyy-MM-dd HH:mm}。";
        RefreshItems();
    }


    void DeleteArchivedItem(ArchivedTodoItem item)
    {
        if (!data.DeleteArchivedItem(item.Id)) return;
        Result.Foreground = new SolidColorBrush(Color.FromRgb(157, 214, 157));
        Result.Text = "已永久删除归档事项。";
        RefreshItems();
    }

    void LocateRequestedItem()
    {
        var target = itemToLocate;
        itemToLocate = null;
        if (target is null || !itemRows.TryGetValue(Key(target.Id, target.Kind), out var row) && !itemRowsById.TryGetValue(target.Id, out row)) return;
        Dispatcher.BeginInvoke(() =>
        {
            ItemsScroller.UpdateLayout();
            row.BringIntoView();
            FlashItem(row);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    static void FlashItem(Border row)
    {
        var baseColor = row.Background is SolidColorBrush brush ? brush.Color : Color.FromRgb(36, 36, 38);
        var highlight = new SolidColorBrush(baseColor);
        row.Background = highlight;
        highlight.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation(baseColor, Color.FromRgb(55, 96, 145), TimeSpan.FromMilliseconds(260))
        {
            AutoReverse = true,
            RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2)
        });
    }
    StackPanel BuildTaskAttributesEditor(Grid row, TaskAttributes attributes)
    {
        var container = new StackPanel();
        container.Children.Add(row);
        var editor = new WrapPanel { Margin = new Thickness(36, 8, 0, 0) };
        var priorityOptions = Enum.GetValues<LifePriority>().Select(value => new ComboBoxItem { Content = TaskDisplayLabels.Priority(value), Tag = value }).ToArray();
        var priority = new System.Windows.Controls.ComboBox { Width = 76, Height = 27, ItemsSource = priorityOptions, SelectedItem = priorityOptions.Single(item => (LifePriority)item.Tag == attributes.Priority), Margin = new Thickness(0, 0, 8, 4), ToolTip = "优先级" };
        var category = new System.Windows.Controls.ComboBox { Width = 86, Height = 27, ItemsSource = CommonCategories, IsEditable = true, Text = attributes.Category ?? "", Margin = new Thickness(0, 0, 5, 4), ToolTip = "分类" };
        var estimatedMinutes = attributes.EstimatedMinutes ?? 30;
        var estimateUnitMinutes = estimatedMinutes >= 60 && estimatedMinutes % 60 == 0 ? 60 : 1;
        var estimateStepper = PositiveNumberStepper(estimatedMinutes / estimateUnitMinutes, "预计时长", out var estimateValue);
        var estimateUnit = TimeUnitSelector(estimateUnitMinutes);
        var energyOptions = new ComboBoxItem[] { new() { Content = "不限", Tag = null } }
            .Concat(Enum.GetValues<EnergyLevel>().Select(value => new ComboBoxItem { Content = TaskDisplayLabels.Energy(value), Tag = value })).ToArray();
        var energy = new System.Windows.Controls.ComboBox { Width = 92, Height = 27, ItemsSource = energyOptions, SelectedItem = energyOptions.Single(item => Equals(item.Tag, attributes.Energy)), Margin = new Thickness(0, 0, 5, 4), ToolTip = "能量" };
        var overdueGraceStepper = PositiveNumberStepper(attributes.OverdueGraceMinutes, "超时宽限分钟", out var overdueGrace);
        var details = new WrapPanel { Margin = new Thickness(36, 4, 0, 0), Visibility = Visibility.Collapsed };
        var more = new Button { Content = "更多设置 ▾", Height = 27, Style = (Style)FindResource("Action"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 4) };
        more.Click += (_, _) =>
        {
            var expanded = details.Visibility != Visibility.Visible;
            details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            more.Content = expanded ? "收起设置 ▴" : "更多设置 ▾";
        };
        var save = new Button { Content = "保存", Height = 27, Style = (Style)FindResource("Action"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 0, 4) };
        SetThemeResource(save, Button.BackgroundProperty, "Brush.AccentSoft");
        SetThemeResource(save, Button.ForegroundProperty, "Brush.Accent");
        save.Click += (_, _) => SaveTaskAttributes(attributes, priority, category, estimateValue, estimateUnit, energy, overdueGrace);
        editor.Children.Add(SecondaryLabel("优先级", new Thickness(0, 0, 5, 4)));
        editor.Children.Add(priority);
        editor.Children.Add(SecondaryLabel("超时宽限", new Thickness(0, 0, 3, 4)));
        editor.Children.Add(overdueGraceStepper);
        editor.Children.Add(SecondaryLabel("分钟", new Thickness(0, 0, 8, 4)));
        editor.Children.Add(more);
        editor.Children.Add(save);
        details.Children.Add(SecondaryLabel("分类", new Thickness(0, 0, 4, 4)));
        details.Children.Add(category);
        details.Children.Add(SecondaryLabel("预计", new Thickness(0, 0, 4, 4)));
        details.Children.Add(estimateStepper);
        details.Children.Add(estimateUnit);
        details.Children.Add(SecondaryLabel("能量", new Thickness(0, 0, 4, 4)));
        details.Children.Add(energy);
        var completed = attributes.CompletedAtUtc is null ? "未完成" : attributes.CompletedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        details.Children.Add(SetThemeResource(new TextBlock
        {
            Text = $"完成：{completed} · 已延期 {attributes.DeferredCount} 次",
            FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
        }, TextBlock.ForegroundProperty, "Brush.TextSecondary"));
        container.Children.Add(editor);
        container.Children.Add(details);
        return container;
    }

    TextBlock SecondaryLabel(string text, Thickness margin) => SetThemeResource(new TextBlock
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = margin
    }, TextBlock.ForegroundProperty, "Brush.TextSecondary");

    TextBox EditableTextBox(double width, string text, string toolTip) => new()
    {
        Width = width,
        Height = 27,
        Text = text,
        Margin = new Thickness(0, 0, 5, 4),
        ToolTip = toolTip,
        Style = (Style)FindResource("EditableInput")
    };

    Grid PositiveNumberStepper(int initialValue, string toolTip, out TextBox valueBox)
    {
        var box = EditableTextBox(42, TaskAttributeNumbers.Normalize(initialValue).ToString(), toolTip);
        box.Margin = new Thickness(0, 0, 0, 4);
        box.IsReadOnly = true;
        box.IsTabStop = false;
        box.TextAlignment = TextAlignment.Center;
        valueBox = box;

        var up = StepperButton("▲", () => box.Text = TaskAttributeNumbers.Increase(StepperValue(box)).ToString());
        var down = StepperButton("▼", () => box.Text = TaskAttributeNumbers.Decrease(StepperValue(box)).ToString());
        var arrows = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical, Margin = new Thickness(2, 0, 5, 4) };
        arrows.Children.Add(up);
        arrows.Children.Add(down);

        var stepper = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        stepper.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stepper.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stepper.Children.Add(box);
        Grid.SetColumn(arrows, 1);
        stepper.Children.Add(arrows);
        return stepper;
    }

    System.Windows.Controls.ComboBox TimeUnitSelector(int selectedMinutes) => new()
    {
        Width = 58,
        Height = 27,
        ItemsSource = new[]
        {
            new ComboBoxItem { Content = "分钟", Tag = 1 },
            new ComboBoxItem { Content = "小时", Tag = 60 }
        },
        SelectedIndex = selectedMinutes == 60 ? 1 : 0,
        Margin = new Thickness(0, 0, 5, 4),
        ToolTip = "时间单位"
    };

    Button StepperButton(string content, Action action)
    {
        var button = new Button { Content = content, Width = 18, Height = 13, MinHeight = 13, Padding = new Thickness(0), FontSize = 8, Style = (Style)FindResource("Action") };
        button.Click += (_, _) => action();
        return button;
    }

    static int StepperValue(TextBox valueBox) => int.TryParse(valueBox.Text, out var value) ? TaskAttributeNumbers.Normalize(value) : 1;

    void SaveTaskAttributes(TaskAttributes original, System.Windows.Controls.ComboBox priorityBox, System.Windows.Controls.ComboBox categoryBox, TextBox estimateValueBox, System.Windows.Controls.ComboBox estimateUnitBox, System.Windows.Controls.ComboBox energyBox, TextBox overdueGraceBox)
    {
        if (priorityBox.SelectedItem is not ComboBoxItem { Tag: LifePriority priority }) return;
        if (!int.TryParse(estimateValueBox.Text.Trim(), out var estimateValue) || estimateValue <= 0)
        {
            Result.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            Result.Text = "预计时长必须大于 0。";
            return;
        }
        var unitMinutes = estimateUnitBox.SelectedItem is ComboBoxItem { Tag: int unit } ? unit : 1;
        if (estimateValue > int.MaxValue / unitMinutes)
        {
            Result.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            Result.Text = "预计时长过大。";
            return;
        }
        if (!int.TryParse(overdueGraceBox.Text.Trim(), out var overdueGrace) || overdueGrace <= 0)
        {
            Result.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            Result.Text = "超时宽限必须是正整数分钟。";
            return;
        }
        var updated = original with
        {
            Priority = priority,
            Category = categoryBox.Text,
            EstimatedMinutes = estimateValue * unitMinutes,
            Energy = energyBox.SelectedItem is ComboBoxItem { Tag: EnergyLevel energy } ? energy : null,
            OverdueGraceMinutes = overdueGrace
        };
        var result = taskAttributes.Update(updated);
        Result.Foreground = result == TaskAttributesUpdateResult.Succeeded
            ? new SolidColorBrush(Color.FromRgb(157, 214, 157))
            : new SolidColorBrush(Color.FromRgb(255, 120, 120));
        Result.Text = result switch
        {
            TaskAttributesUpdateResult.Succeeded => "任务属性已保存。",
            TaskAttributesUpdateResult.ConcurrentConflict => "事项刚被其他操作更新，请刷新后再保存。",
            TaskAttributesUpdateResult.ReadOnly => "只读事项不能修改属性。",
            _ => "未找到可编辑待办。"
        };
        if (result == TaskAttributesUpdateResult.Succeeded) RefreshItems();
    }

    void StartFocus(AgendaItem item)
    {
        try
        {
            var active = focus.RestoreActive();
            if (active is not null)
            {
                Result.Foreground = new SolidColorBrush(Color.FromRgb(157, 214, 157));
                Result.Text = active.ItemId == item.Id
                    ? $"正在专注：{item.Title}。"
                    : "已有进行中的专注，请先在灵动岛结束当前专注。";
                return;
            }
            var session = focus.Start(item.Id, 25);
            Result.Foreground = new SolidColorBrush(Color.FromRgb(157, 214, 157));
            Result.Text = $"已开始专注：{item.Title}（25 分钟）。灵动岛会显示倒计时。";
        }
        catch (Exception exception)
        {
            Result.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            Result.Text = $"无法开始专注：{exception.Message}";
        }
    }

    void SetSelected(AgendaItem item, bool isSelected)
    {
        if (isSelected) selected.Add(Key(item));
        else selected.Remove(Key(item));
        awaitingConfirmation = false;
        Result.Text = "";
        UpdateSelectionUi();
    }

    void ArchiveOne(AgendaItem item)
    {
        reminders.Archive(item);
        selected.Remove(Key(item));
        awaitingConfirmation = false;
        Result.Text = "已归档事项。";
        RefreshItems();
    }

    void UpdateSelectionUi() => ArchiveSelected.Content = selected.Count == 0
        ? "批量归档"
        : $"归档已选 {selected.Count} 项";

    void ArchiveSelected_Click(object sender, RoutedEventArgs e)
    {
        var targets = Items.Children.OfType<Border>().Select(border => border.Tag).OfType<AgendaItem>().Where(item => selected.Contains(Key(item))).ToList();
        if (targets.Count == 0)
        {
            Result.Text = "\u8BF7\u5148\u9009\u62E9\u4E8B\u9879\u3002";
            return;
        }
        if (!awaitingConfirmation)
        {
            awaitingConfirmation = true;
            Result.Text = $"再次点击批量归档，确认归档 {targets.Count} 项。";
            return;
        }
        reminders.Archive(targets);
        selected.Clear();
        awaitingConfirmation = false;
        Result.Text = "已归档所选事项。";
        RefreshItems();
    }

    static string ArchivedKindLabel(string kind) => kind switch
    {
        "event" => "日程",
        "reminder" => "提醒",
        "recurring" => "周期提醒",
        "long_term" => "长期事项",
        _ => "待办"
    };

    static string ItemDetails(ManagedLifeItem item)
    {
        if (item.Kind == "recurring")
            return $"\u5468\u671F\u63D0\u9192 \u00B7 {item.RecurrenceLabel} \u00B7 \u4E0B\u4E00\u6B21 {item.ScheduledAt:MM-dd HH:mm}";
        var kind = item.Kind switch { "event" => "\u65E5\u7A0B", "reminder" => "\u63D0\u9192", "long_term" => "\u957F\u671F\u4E8B\u9879", _ => "\u5F85\u529E" };
        var schedule = item.ScheduledAt is { } value ? value.ToString("yyyy-MM-dd HH:mm") : "\u672A\u8BBE\u65E5\u671F";
        var completed = item.IsCompleted ? " \u00B7 \u5DF2\u5B8C\u6210" : "";
        return $"{kind} \u00B7 {schedule}{completed}";
    }

    static string Key(AgendaItem item) => Key(item.Id, item.Kind);
    static string Key(string id, string kind) => kind + ":" + id;
}
