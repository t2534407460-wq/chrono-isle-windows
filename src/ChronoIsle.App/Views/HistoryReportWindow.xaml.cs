using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Documents;
using ChronoIsle.App.Services.Reporting;

namespace ChronoIsle.App.Views;

public partial class HistoryReportWindow : Window
{
    HwndSource? windowSource;
    readonly HistoryReportService service;
    HistoryReport? report;
    IReadOnlyList<HistoryReport> snapshots = [];
    public HistoryReportWindow(HistoryReportService service, ChronoIsle.App.Services.Sync.Day21HabitClient? day21 = null)
    {
        this.service = service; InitializeComponent(); Refresh(); LoadSnapshots();
        if (day21 is not null) Day21History.Initialize(day21);
        ReportTabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, ReportTabs)) UpdateReportScope(); };
    }
    void UpdateReportScope()
    {
        var source = ReportTabs.SelectedIndex == 2;
        LocalReportFilters.Visibility = Summary.Visibility = LocalReportFooter.Visibility = source ? Visibility.Collapsed : Visibility.Visible;
        ReportDescription.Text = source ? "查看21day的来源历史；记录保留在21day，不计入时屿报表的统计、快照或导出。" : "查看时屿已保存事项，按日期筛选记录并留存报表快照。";
    }
    void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    void All_Click(object sender, RoutedEventArgs e) { StartDate.SelectedDate = EndDate.SelectedDate = null; Refresh(); }
    void Refresh()
    {
        try { SnapshotPicker.SelectedIndex = -1; ShowReport(service.Read(StartDate.SelectedDate is { } a ? DateOnly.FromDateTime(a) : null, EndDate.SelectedDate is { } b ? DateOnly.FromDateTime(b) : null)); }
        catch (ArgumentException e) { Notice.Text = e.Message; }
    }
    void ShowReport(HistoryReport value)
    {
        report = value; Records.ItemsSource = value.Records;
        Summary.Text = $"共 {value.Records.Count} 项    已完成 {value.Completed} 项    未完成 {value.Unfinished} 项    已归档 {value.Records.Count(r => r.Archived)} 项";
        var max = Math.Max(1, value.Days.Select(d => Math.Max(d.Created, d.Completed)).DefaultIfEmpty().Max());
        Trend.ItemsSource = value.Days.Select(d => new { d.Date, CreatedWidth = d.Created * 420d / max, CompletedWidth = d.Completed * 420d / max, Label = $"{d.Created} / {d.Completed}" });
        Notice.Text = $"统计截至 {value.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm}。时间缺失不推算；已清理的旧记录无法补回。已完成项按当前状态统计，趋势图按每项首次有时间的完成记录统计；重新打开事项不会抹去这条历史。";
    }
    void LoadSnapshots()
    {
        snapshots = service.Snapshots(); SnapshotPicker.ItemsSource = snapshots.Select(s => $"{s.GeneratedAt.ToLocalTime():MM-dd HH:mm:ss} · {s.Records.Count} 项").ToArray();
    }
    void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        if (report is null) return; service.SaveSnapshot(report); LoadSnapshots(); Notice.Text = "已保存固定快照，后续事项变化不会改写这份报表。";
    }
    void Snapshot_Changed(object sender, SelectionChangedEventArgs e)
    { if (SnapshotPicker.SelectedIndex >= 0 && SnapshotPicker.SelectedIndex < snapshots.Count) ShowReport(snapshots[SnapshotPicker.SelectedIndex]); }
    void Csv_Click(object sender, RoutedEventArgs e) => Export(false);
    void Excel_Click(object sender, RoutedEventArgs e) => Export(true);
    void Export(bool excel)
    {
        if (report is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"时屿历史报表-{DateTime.Now:yyyyMMdd}", Filter = excel ? "Excel 工作簿|*.xlsx" : "CSV 表格|*.csv", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (excel) { using var output = File.Create(dialog.FileName); HistoryReportExport.Xlsx(output, report); }
            else File.WriteAllText(dialog.FileName, HistoryReportExport.Csv(report), new UTF8Encoding(true));
            Notice.Text = "报表已导出。";
        }
        catch (IOException) { Notice.Text = "无法写入文件，请关闭占用该文件的程序或选择其他位置。"; }
        catch (UnauthorizedAccessException) { Notice.Text = "没有写入该位置的权限，请选择其他位置。"; }
    }
    void Print_Click(object sender, RoutedEventArgs e)
    {
        if (report is null) return;
        var dialog = new System.Windows.Controls.PrintDialog();
        if (dialog.ShowDialog() != true) return;
        var document = new FlowDocument { FontFamily = FontFamily, FontSize = 11, PagePadding = new Thickness(32), ColumnWidth = double.PositiveInfinity, PageWidth = dialog.PrintableAreaWidth };
        document.Blocks.Add(new Paragraph(new Run("时屿历史报表")) { FontSize = 22 });
        document.Blocks.Add(new Paragraph(new Run($"{Summary.Text}\n生成时间：{report.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n统计时区：{report.TimeZoneId}")));
        foreach (var r in report.Records) document.Blocks.Add(new Paragraph(new Run($"{r.Title} · {r.KindLabel} · {r.StatusLabel}\n来源：{r.Source}；分类：{r.Category}；创建：{r.CreatedText}；完成：{r.CompletedText}")));
        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, "时屿历史报表");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        windowSource?.AddHook(WindowWorkArea.ConstrainMaximizedBounds);
    }
    protected override void OnClosed(EventArgs e)
    {
        if (windowSource is { IsDisposed: false }) windowSource.RemoveHook(WindowWorkArea.ConstrainMaximizedBounds);
        windowSource = null;
        base.OnClosed(e);
    }
    void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else DragMove();
    }
    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
