using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ChronoIsle.App.Services.Sync;
using Microsoft.Data.Sqlite;
using MessageBox = System.Windows.MessageBox;

namespace ChronoIsle.App.Views;

public partial class Day21HabitPanel : System.Windows.Controls.UserControl
{
    Day21HabitClient? client;
    bool busy;
    bool dashboardPresentation;
    public bool ShowHistory { get; set; }
    public Day21HabitPanel()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible && IsLoaded && client?.LoggedIn == true) Refresh_Click(this, new RoutedEventArgs()); };
        Loaded += (_, _) => { Render(); if (IsVisible && client?.LoggedIn == true) Refresh_Click(this, new RoutedEventArgs()); };
    }
    public void Initialize(Day21HabitClient value) { client = value; Render(); }
    public void UseDashboardLayout()
    {
        dashboardPresentation = true;
        SourceHeader.Children[0].Visibility = Visibility.Collapsed;
        SourceLayout.Children.Remove(SourceStatus);
        SourceHeader.Children.Add(SourceStatus);
        SourceLayout.Margin = new Thickness(0);
        SourceStatus.Margin = new Thickness(0);
        SourceStatus.VerticalAlignment = VerticalAlignment.Center;
        SourceStatus.FontSize = 11;
        SourceStatus.TextWrapping = TextWrapping.NoWrap;
        SourceStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        SourceHeader.Margin = new Thickness(0, 0, 0, 5);
        RefreshSource.Content = "刷新";
        RefreshSource.MinHeight = 26;
        RefreshSource.FontSize = 11;
        RefreshSource.Padding = new Thickness(8, 2, 8, 2);
        RefreshSource.Margin = new Thickness(8, 0, 0, 0);
        Render();
    }
    async void Refresh_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    { if (client is null) throw new InvalidOperationException("来源服务尚未初始化。"); await client.RefreshAsync(); SourceMessage.Text = dashboardPresentation ? "" : "来源记录已刷新。"; });
    void Account_Click(object sender, RoutedEventArgs e) { if (Application.Current is App app) app.OpenLifeSettings(); }
    public void Render()
    {
        SourceCards.Children.Clear(); RequestIssues.Children.Clear();
        OpenAccount.Visibility = client?.LoggedIn == true ? Visibility.Collapsed : Visibility.Visible;
        if (dashboardPresentation)
        {
            RequestIssues.Margin = new Thickness(0);
            SourceCards.Margin = new Thickness(0);
            SourceMessage.Margin = new Thickness(0);
            RefreshSource.Visibility = client?.LoggedIn == true ? Visibility.Visible : Visibility.Collapsed;
        }
        if (client?.LoggedIn != true) { SourceStatus.Text = "在设置头像入口登录，与21day使用同一个邮箱账号。"; return; }
        var requests = client.Requests();
        SourceStatus.Text = client.LastFetched is { } at ? $"来源快照：{at.ToLocalTime():MM-dd HH:mm:ss} · {client.TimeZoneId}；离线显示缓存" : "尚未获取来源记录。请确认手机已登录并同步习惯。";
        if (dashboardPresentation)
        {
            SourceStatus.ToolTip = SourceStatus.Text;
            if (client.LastFetched is { } fetched) SourceStatus.Text = $"更新于 {fetched.ToLocalTime():MM-dd HH:mm}";
        }
        foreach (var request in requests)
        {
            var content = new StackPanel();
            content.Children.Add(Text(request.Status == "pending" ? "有一项操作尚未确认，请重试原请求" : request.Message, "Brush.Warning"));
            var retry = ActionButton(request.Status == "pending" ? "重试原请求" : "保留来源并结束此请求");
            retry.Click += async (_, _) => await Run(async () =>
            {
                if (request.Status == "pending") await client.RefreshAsync(true);
                else { await client.RefreshAsync(); client.DismissFailed(request); }
                SourceMessage.Text = "已更新来源及请求状态。";
            });
            if (!ShowHistory) content.Children.Add(retry); RequestIssues.Children.Add(Card(content, "Brush.Surface"));
        }
        var cards = client.Cached().OrderByDescending(c => c.Timer is not null).ThenByDescending(c => c.Scheduled).ThenBy(c => c.Name);
        var count = 0;
        foreach (var card in cards)
        {
            count++; var content = new StackPanel();
            if (dashboardPresentation)
            {
                var heading = new DockPanel();
                var name = Text(card.Name, "Brush.TextPrimary", 13, true);
                name.MaxWidth = 160; name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = card.Name;
                name.Margin = new Thickness(0, 0, 12, 3); DockPanel.SetDock(name, Dock.Left); heading.Children.Add(name);
                var details = Text(card.Summary, "Brush.TextSecondary"); details.ToolTip = $"{card.Day} · {card.Summary}";
                heading.Children.Add(details); content.Children.Add(heading);
            }
            else
            {
                content.Children.Add(Text(card.Name, "Brush.TextPrimary", 15, true));
                content.Children.Add(Text($"{card.Day} · {card.Summary}", "Brush.TextSecondary"));
            }
            if (ShowHistory)
            {
                foreach (var entry in card.Data.GetProperty("entries").EnumerateObject().OrderByDescending(p => p.Name))
                    content.Children.Add(Text($"{entry.Name}　{entry.Value.GetProperty("value").GetInt32()} {card.Unit}　{entry.Value.GetProperty("note").GetString()}", "Brush.TextSecondary"));
                if (card.Data.GetProperty("entries").EnumerateObject().Count() == 0) content.Children.Add(Text("本轮暂无记录；未记录不等于0。", "Brush.TextTertiary"));
            }
            else
            {
                var actions = new WrapPanel { Margin = new Thickness(0, dashboardPresentation ? 3 : 9, 0, 0) };
                var hasPending = requests.Any(r => r.SourceId == card.SourceId);
                foreach (var action in card.Actions.Distinct())
                {
                    if (action is "timer_finish" or "timer_cancel" && !OwnTimer(card) || action == "timer_takeover" && OwnTimer(card)) continue;
                    if (action == "confirm" && card.Value is not null) continue;
                    var label = action switch { "count" => "+1", "confirm" => "今日确认", "set_total" => "修改今日总量", "timer_start" => "开始计时", "timer_finish" => "完成计时", "timer_cancel" => "取消计时", "timer_takeover" => "接管计时", "archive" => "归档本轮", _ => null };
                    if (label is null) continue;
                    var button = ActionButton(label); button.IsEnabled = !hasPending; System.Windows.Automation.AutomationProperties.SetAutomationId(button, "Day21-" + action);
                    if (action == "set_total") button.Click += (_, _) => EditTotal(content, card, button);
                    else button.Click += async (_, _) => await Run(async () =>
                    {
                        if (action is "archive" or "timer_takeover")
                        {
                            if (action == "timer_takeover")
                            {
                                await client.RefreshAsync();
                                var fresh = client.Cached().SingleOrDefault(c => c.SourceId == card.SourceId);
                                if (fresh?.SourceRevision != card.SourceRevision) throw new InvalidOperationException("计时状态已变化，请查看更新后的卡片并重新选择。");
                            }
                            var message = action == "archive" ? "将归档21day来源的本轮习惯，已有历史记录保留。是否继续？" : "将把此计时的负责设备改为本机。原设备需要刷新来源；开始时间和已记录总量保持不变。是否接管？";
                            if (MessageBox.Show(Window.GetWindow(this), message, "21day来源操作", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                        }
                        SourceMessage.Text = await client.ExecuteAsync(card, action, takeoverConfirmed: action == "timer_takeover");
                    });
                    actions.Children.Add(button);
                }
                content.Children.Add(actions);
                if (card.Timer is not null && !OwnTimer(card)) content.Children.Add(Text("计时由另一设备负责；在本机结束前请在线接管。", "Brush.Warning"));
            }
            var sourceCard = Card(content, "Brush.Card");
            if (dashboardPresentation)
            {
                sourceCard.Padding = new Thickness(0, 4, 0, 0);
                sourceCard.CornerRadius = new CornerRadius(0);
                sourceCard.BorderThickness = new Thickness(0);
                sourceCard.Margin = new Thickness(0, 0, 0, 4);
            }
            SourceCards.Children.Add(sourceCard);
        }
        if (count == 0) SourceCards.Children.Add(Text("没有已同步的21day习惯。请先在手机创建习惯并同步。", "Brush.TextSecondary"));
    }
    bool OwnTimer(Day21HabitCard card) => card.Timer is { ValueKind: JsonValueKind.Object } timer && timer.GetProperty("device").GetString() == client!.DeviceId.ToString();
    void EditTotal(StackPanel content, Day21HabitCard card, Button trigger)
    {
        if (trigger.Tag is true) return; trigger.Tag = true;
        var editor = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        editor.Children.Add(Text("更正指定日期总量，不是累加；更改总量时计时明细将重置。", "Brush.TextSecondary"));
        var total = new TextBox { Text = (card.Value ?? 0).ToString(), MinHeight = 36, MaxLength = 6, Margin = new Thickness(0, 6, 0, 6) };
        var note = new TextBox { Text = card.Entry is { } entry ? entry.GetProperty("note").GetString() ?? "" : "", MaxLength = 300, MinHeight = 36, Margin = new Thickness(0, 0, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(total, "当日总量"); System.Windows.Automation.AutomationProperties.SetName(note, "当日备注");
        editor.Children.Add(total); editor.Children.Add(note);
        var buttons = new WrapPanel(); var save = ActionButton("确认总量校正"); var cancel = ActionButton("取消");
        save.Click += async (_, _) =>
        {
            if (!int.TryParse(total.Text, out var value) || value is < 0 or > 100000) { Error("请输入0至100000的整数总量。"); return; }
            if (card.Data.GetProperty("plan").GetProperty("mode").GetString() == "CHECK" && value > 1) { Error("每日确认的总量只能为0或1。"); return; }
            await Run(async () => { SourceMessage.Text = await client!.ExecuteAsync(card, "set_total", value, note.Text); });
        };
        cancel.Click += (_, _) => { content.Children.Remove(editor); trigger.Tag = false; };
        buttons.Children.Add(save); buttons.Children.Add(cancel); editor.Children.Add(buttons); content.Children.Add(editor); total.Focus();
    }
    async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; RefreshSource.IsEnabled = false; SourceCards.IsEnabled = false; RequestIssues.IsEnabled = false; SourceBusy.Visibility = Visibility.Visible;
        SourceMessage.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary"); SourceMessage.Text = "正在连接21day…";
        try { await action(); }
        catch (InvalidOperationException e) { Error(e.Message); }
        catch (HttpRequestException) { Error("无法连接21day，缓存和未确认的原请求已保留。"); }
        catch (OperationCanceledException) { Error("来源请求超时，原请求已保留，可重试。"); }
        catch (Exception e) when (e is IOException or JsonException or SqliteException or KeyNotFoundException or OverflowException or ArgumentException)
        { Error("来源数据或本机存储无法处理，请保留数据并刷新核对。"); }
        finally { busy = false; RefreshSource.IsEnabled = true; SourceCards.IsEnabled = true; RequestIssues.IsEnabled = true; SourceBusy.Visibility = Visibility.Collapsed; Render(); }
    }
    void Error(string message) { SourceMessage.Text = message; SourceMessage.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger"); }
    Button ActionButton(string label) => new() { Content = label, Style = (Style)FindResource("Button.Secondary"), Padding = dashboardPresentation ? new Thickness(8, 3, 8, 3) : new Thickness(10, 5, 10, 5), MinHeight = dashboardPresentation ? 28 : 32, Margin = new Thickness(0, 0, 7, dashboardPresentation ? 3 : 6) };
    static TextBlock Text(string text, string brush, double size = 12, bool bold = false)
    { var value = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, 5) }; value.SetResourceReference(TextBlock.ForegroundProperty, brush); return value; }
    static Border Card(UIElement content, string brush)
    { var border = new Border { Child = content, Padding = new Thickness(14), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 10) }; border.SetResourceReference(Border.BackgroundProperty, brush); border.SetResourceReference(Border.BorderBrushProperty, "Brush.Stroke"); return border; }
}
