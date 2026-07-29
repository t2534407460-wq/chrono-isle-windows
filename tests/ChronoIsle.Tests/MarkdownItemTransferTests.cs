using ChronoIsle.App;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.ImportExport;
using Microsoft.Data.Sqlite;

namespace ChronoIsle.Tests;

public sealed class MarkdownItemTransferTests : IDisposable
{
    readonly string directory = Path.Combine(
        Path.GetTempPath(), "chrono-isle-markdown-transfer-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Markdown_round_trip_preserves_all_supported_item_types()
    {
        var start = new DateTime(2026, 8, 5, 14, 0, 0);
        MarkdownTransferItem[] items =
        [
            new(MarkdownItemKind.Todo, "提交周报", "第一行\n第二行", start, null, null,
                start.AddMinutes(-30), false, null, null, []),
            new(MarkdownItemKind.Reminder, "喝水", null, null, null, null,
                start.AddHours(1), false, null, null, []),
            new(MarkdownItemKind.Event, "产品会议", "会议室 A", null, start,
                start.AddHours(1), start.AddMinutes(-10), false, null, null, []),
            new(MarkdownItemKind.LongTerm, "学习日语", "长期坚持", null, null, null,
                null, false, null, null, []),
            new(MarkdownItemKind.Recurring, "周复盘", null, null, null, null,
                null, false, RecurrenceKind.Weekly, new TimeOnly(20, 30),
                [DayOfWeek.Monday, DayOfWeek.Friday])
        ];

        var markdown = MarkdownItemTransferService.Serialize(items);
        var parsed = MarkdownItemTransferService.Parse(markdown);

        Assert.Equal(5, parsed.Items.Count);
        Assert.Equal(markdown, MarkdownItemTransferService.Serialize(parsed.Items));
        Assert.Contains("\\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_document_is_rejected_before_any_item_is_written()
    {
        Directory.CreateDirectory(directory);
        var data = new LifeDataService(Path.Combine(directory, "invalid.db"));
        const string markdown = """
            # ChronoIsle 事项
            <!-- chrono-isle-items:v1 -->

            ## 事项 1
            - 类型：提醒
            - 标题：没有时间的提醒
            - 提醒时间：
            """;

        var error = Assert.Throws<MarkdownItemDocumentException>(() =>
            MarkdownItemTransferService.Parse(markdown));

        Assert.Contains("提醒时间", error.Message, StringComparison.Ordinal);
        Assert.Empty(data.ManagedItems());
    }

    [Fact]
    public void Import_creates_todo_reminder_event_long_term_and_recurring_items()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "import.db");
        var data = new LifeDataService(path);
        var start = DateTime.Today.AddDays(10).AddHours(9);
        MarkdownTransferItem[] items =
        [
            new(MarkdownItemKind.Todo, "导入待办", null, start, null, null,
                null, false, null, null, []),
            new(MarkdownItemKind.Reminder, "导入提醒", null, null, null, null,
                start.AddHours(1), false, null, null, []),
            new(MarkdownItemKind.Event, "导入日程", null, null, start.AddHours(2),
                start.AddHours(3), null, false, null, null, []),
            new(MarkdownItemKind.LongTerm, "导入长期事项", null, null, null, null,
                null, false, null, null, []),
            new(MarkdownItemKind.Recurring, "导入周期提醒", null, null, null, null,
                null, false, RecurrenceKind.Weekly, new TimeOnly(8, 30),
                [DayOfWeek.Tuesday])
        ];
        var service = new MarkdownItemTransferService(data);

        var result = service.Import(MarkdownItemTransferService.Parse(
            MarkdownItemTransferService.Serialize(items)));

        Assert.Equal(5, result.ImportedCount);
        var managed = data.ManagedItems();
        Assert.Contains(managed, item => item.Kind == "todo" && item.Title == "导入待办");
        Assert.Contains(managed, item => item.Kind == "reminder" && item.Title == "导入提醒");
        Assert.Contains(managed, item => item.Kind == "event" && item.Title == "导入日程");
        Assert.Contains(managed, item => item.Kind == "long_term" && item.Title == "导入长期事项");
        Assert.Contains(managed, item => item.Kind == "recurring" && item.Title == "导入周期提醒");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
