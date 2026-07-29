using System.Globalization;
using System.Text;

namespace ChronoIsle.App.Services.ImportExport;

public enum MarkdownItemKind
{
    Todo,
    Reminder,
    Event,
    LongTerm,
    Recurring
}

public sealed record MarkdownTransferItem(
    MarkdownItemKind Kind,
    string Title,
    string? Notes,
    DateTime? DueAt,
    DateTime? StartsAt,
    DateTime? EndsAt,
    DateTime? RemindAt,
    bool IsCompleted,
    RecurrenceKind? Recurrence,
    TimeOnly? RecurrenceTime,
    IReadOnlyList<DayOfWeek> Weekdays);

public sealed record MarkdownImportPreview(IReadOnlyList<MarkdownTransferItem> Items)
{
    public string Summary
    {
        get
        {
            var groups = Items
                .GroupBy(item => item.Kind)
                .OrderBy(group => group.Key)
                .Select(group => $"{MarkdownItemTransferService.KindLabel(group.Key)} {group.Count()} 项");
            return $"共 {Items.Count} 项\n{string.Join("，", groups)}";
        }
    }
}

public sealed record MarkdownImportResult(int ImportedCount);

public sealed class MarkdownItemDocumentException(string message, int? lineNumber = null)
    : FormatException(lineNumber is null ? message : $"第 {lineNumber} 行：{message}")
{
    public int? LineNumber { get; } = lineNumber;
}

public sealed class MarkdownItemTransferService(LifeDataService data)
{
    const string Marker = "<!-- chrono-isle-items:v1 -->";
    const string DateFormat = "yyyy-MM-dd HH:mm";
    static readonly HashSet<string> AllowedFields =
    [
        "类型", "标题", "备注", "截止时间", "开始时间", "结束时间", "提醒时间",
        "状态", "周期规则", "周期时间", "星期"
    ];

    public static string TemplateMarkdown => """
        # ChronoIsle 事项导入模板
        <!-- chrono-isle-items:v1 -->

        > 每个 `## 事项` 表示一条新事项。时间格式固定为 `yyyy-MM-dd HH:mm`。
        > 类型可填写：待办、提醒、日程、长期事项、周期提醒。
        > 周期规则可填写：每天、工作日、法定工作日、法定节假日、每周。
        > 每周事项的星期使用：周一,周三,周五。删除不需要的空白事项块。

        ## 事项 1
        - 类型：待办
        - 标题：
        - 备注：
        - 截止时间：
        - 开始时间：
        - 结束时间：
        - 提醒时间：
        - 状态：进行中
        - 周期规则：
        - 周期时间：
        - 星期：
        """;

    public string ExportMarkdown() => Serialize(data.MarkdownTransferItems());

    public MarkdownImportResult Import(MarkdownImportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Items.Count == 0)
            throw new MarkdownItemDocumentException("文件中没有可导入的事项。");
        return new(data.ImportMarkdownItems(preview.Items));
    }

    public static string Serialize(IReadOnlyList<MarkdownTransferItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var markdown = new StringBuilder();
        markdown.AppendLine("# ChronoIsle 事项");
        markdown.AppendLine(Marker);
        markdown.AppendLine();
        markdown.AppendLine("> 由 ChronoIsle 导出。删除或调整事项块后可重新导入；导入会创建新事项，不复用原数据库 ID。");
        markdown.AppendLine();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            markdown.AppendLine($"## 事项 {index + 1}");
            Field(markdown, "类型", KindLabel(item.Kind));
            Field(markdown, "标题", Escape(item.Title));
            Field(markdown, "备注", Escape(item.Notes));
            Field(markdown, "截止时间", FormatDate(item.DueAt));
            Field(markdown, "开始时间", FormatDate(item.StartsAt));
            Field(markdown, "结束时间", FormatDate(item.EndsAt));
            Field(markdown, "提醒时间", FormatDate(item.RemindAt));
            Field(markdown, "状态", item.IsCompleted ? "已完成" : "进行中");
            Field(markdown, "周期规则", RecurrenceLabel(item.Recurrence));
            Field(markdown, "周期时间", item.RecurrenceTime?.ToString("HH:mm", CultureInfo.InvariantCulture));
            Field(markdown, "星期", string.Join(",", item.Weekdays.Select(WeekdayLabel)));
            markdown.AppendLine();
        }
        return markdown.ToString();
    }

    public static MarkdownImportPreview Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            throw new MarkdownItemDocumentException("文件为空。");
        if (!markdown.Contains(Marker, StringComparison.Ordinal))
            throw new MarkdownItemDocumentException("缺少 ChronoIsle Markdown 版本标记。");

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownTransferItem>();
        Dictionary<string, (string Value, int Line)>? fields = null;
        var sectionLine = 0;

        void CompleteSection()
        {
            if (fields is null) return;
            items.Add(ParseItem(fields, sectionLine));
            fields = null;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith("## 事项", StringComparison.Ordinal))
            {
                CompleteSection();
                if (items.Count >= 500)
                    throw new MarkdownItemDocumentException("单个文件最多导入 500 项。", lineNumber);
                fields = new(StringComparer.Ordinal);
                sectionLine = lineNumber;
                continue;
            }
            if (fields is null || trimmed.Length == 0 || trimmed.StartsWith('>') ||
                trimmed.StartsWith("<!--", StringComparison.Ordinal))
                continue;
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
                throw new MarkdownItemDocumentException("事项块内只允许固定字段列表。", lineNumber);

            var body = trimmed[2..];
            var separator = body.IndexOf('：');
            if (separator < 0) separator = body.IndexOf(':');
            if (separator <= 0)
                throw new MarkdownItemDocumentException("字段必须使用“名称：值”格式。", lineNumber);
            var key = body[..separator].Trim();
            var value = body[(separator + 1)..].Trim();
            if (!AllowedFields.Contains(key))
                throw new MarkdownItemDocumentException($"不支持字段“{key}”。", lineNumber);
            if (!fields.TryAdd(key, (value, lineNumber)))
                throw new MarkdownItemDocumentException($"字段“{key}”重复。", lineNumber);
        }
        CompleteSection();

        if (items.Count == 0)
            throw new MarkdownItemDocumentException("文件中没有 `## 事项` 数据块。");
        return new(items);
    }

    static MarkdownTransferItem ParseItem(
        IReadOnlyDictionary<string, (string Value, int Line)> fields,
        int sectionLine)
    {
        var kind = ParseKind(Required(fields, "类型", sectionLine));
        var titleField = Required(fields, "标题", sectionLine);
        var title = Unescape(titleField.Value).Trim();
        if (title.Length is < 1 or > 200)
            throw new MarkdownItemDocumentException("标题长度必须在 1 到 200 字之间。", titleField.Line);
        var notes = OptionalText(fields, "备注");
        var due = OptionalDate(fields, "截止时间");
        var start = OptionalDate(fields, "开始时间");
        var end = OptionalDate(fields, "结束时间");
        var remind = OptionalDate(fields, "提醒时间");
        var completed = ParseStatus(fields);
        var recurrence = OptionalRecurrence(fields);
        var recurrenceTime = OptionalTime(fields, "周期时间");
        var weekdays = OptionalWeekdays(fields);

        switch (kind)
        {
            case MarkdownItemKind.Todo:
                Reject(start is not null || end is not null || recurrence is not null || recurrenceTime is not null,
                    "待办不能包含日程或周期字段。", sectionLine);
                break;
            case MarkdownItemKind.Reminder:
                if (remind is null) Missing("提醒必须填写提醒时间。", fields, "提醒时间", sectionLine);
                Reject(due is not null || start is not null || end is not null || recurrence is not null ||
                       recurrenceTime is not null || completed,
                    "提醒只能包含提醒时间，且不能标记为已完成。", sectionLine);
                break;
            case MarkdownItemKind.Event:
                if (start is null) Missing("日程必须填写开始时间。", fields, "开始时间", sectionLine);
                if (end is null) Missing("日程必须填写结束时间。", fields, "结束时间", sectionLine);
                if (end <= start)
                    throw new MarkdownItemDocumentException("结束时间必须晚于开始时间。", sectionLine);
                Reject(due is not null || recurrence is not null || recurrenceTime is not null || completed,
                    "日程不能包含截止、周期或已完成状态。", sectionLine);
                break;
            case MarkdownItemKind.LongTerm:
                Reject(start is not null || end is not null || recurrence is not null || recurrenceTime is not null,
                    "长期事项不能包含日程或周期字段。", sectionLine);
                break;
            case MarkdownItemKind.Recurring:
                if (recurrence is null) Missing("周期提醒必须填写周期规则。", fields, "周期规则", sectionLine);
                if (recurrenceTime is null) Missing("周期提醒必须填写周期时间。", fields, "周期时间", sectionLine);
                if (recurrence == RecurrenceKind.Weekly && weekdays.Count == 0)
                    Missing("每周周期提醒必须填写星期。", fields, "星期", sectionLine);
                Reject(due is not null || start is not null || end is not null || remind is not null || completed,
                    "周期提醒不能包含单次时间或已完成状态。", sectionLine);
                break;
        }

        return new(kind, title, notes, due, start, end, remind, completed, recurrence, recurrenceTime, weekdays);
    }

    static void Reject(bool condition, string message, int line)
    {
        if (condition) throw new MarkdownItemDocumentException(message, line);
    }

    static void Missing(
        string message,
        IReadOnlyDictionary<string, (string Value, int Line)> fields,
        string field,
        int sectionLine) =>
        throw new MarkdownItemDocumentException(message, fields.TryGetValue(field, out var value) ? value.Line : sectionLine);

    static (string Value, int Line) Required(
        IReadOnlyDictionary<string, (string Value, int Line)> fields,
        string name,
        int sectionLine)
    {
        if (!fields.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value.Value))
            throw new MarkdownItemDocumentException($"必须填写{name}。", value.Line == 0 ? sectionLine : value.Line);
        return value;
    }

    static string? OptionalText(
        IReadOnlyDictionary<string, (string Value, int Line)> fields,
        string name) =>
        fields.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value.Value)
            ? Unescape(value.Value)
            : null;

    static DateTime? OptionalDate(
        IReadOnlyDictionary<string, (string Value, int Line)> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value.Value)) return null;
        if (DateTime.TryParseExact(value.Value, DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return parsed;
        throw new MarkdownItemDocumentException($"{name}必须使用 {DateFormat}。", value.Line);
    }

    static TimeOnly? OptionalTime(
        IReadOnlyDictionary<string, (string Value, int Line)> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value.Value)) return null;
        if (TimeOnly.TryParseExact(value.Value, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return parsed;
        throw new MarkdownItemDocumentException($"{name}必须使用 HH:mm。", value.Line);
    }

    static bool ParseStatus(IReadOnlyDictionary<string, (string Value, int Line)> fields)
    {
        if (!fields.TryGetValue("状态", out var value) || string.IsNullOrWhiteSpace(value.Value)) return false;
        return value.Value.Trim().ToLowerInvariant() switch
        {
            "进行中" or "false" => false,
            "已完成" or "true" => true,
            _ => throw new MarkdownItemDocumentException("状态只能填写“进行中”或“已完成”。", value.Line)
        };
    }

    static RecurrenceKind? OptionalRecurrence(
        IReadOnlyDictionary<string, (string Value, int Line)> fields)
    {
        if (!fields.TryGetValue("周期规则", out var value) || string.IsNullOrWhiteSpace(value.Value)) return null;
        return value.Value.Trim().ToLowerInvariant() switch
        {
            "每天" or "daily" => RecurrenceKind.Daily,
            "工作日" or "weekdays" => RecurrenceKind.Weekdays,
            "法定工作日" or "official_workdays" => RecurrenceKind.OfficialWorkdays,
            "法定节假日" or "statutory_holidays" => RecurrenceKind.StatutoryHolidays,
            "每周" or "weekly" => RecurrenceKind.Weekly,
            _ => throw new MarkdownItemDocumentException("不支持该周期规则。", value.Line)
        };
    }

    static IReadOnlyList<DayOfWeek> OptionalWeekdays(
        IReadOnlyDictionary<string, (string Value, int Line)> fields)
    {
        if (!fields.TryGetValue("星期", out var value) || string.IsNullOrWhiteSpace(value.Value)) return [];
        var days = new List<DayOfWeek>();
        foreach (var token in value.Value.Split([',', '，', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var day = token.ToLowerInvariant() switch
            {
                "周一" or "星期一" or "monday" => DayOfWeek.Monday,
                "周二" or "星期二" or "tuesday" => DayOfWeek.Tuesday,
                "周三" or "星期三" or "wednesday" => DayOfWeek.Wednesday,
                "周四" or "星期四" or "thursday" => DayOfWeek.Thursday,
                "周五" or "星期五" or "friday" => DayOfWeek.Friday,
                "周六" or "星期六" or "saturday" => DayOfWeek.Saturday,
                "周日" or "星期日" or "周天" or "sunday" => DayOfWeek.Sunday,
                _ => throw new MarkdownItemDocumentException($"无法识别星期“{token}”。", value.Line)
            };
            if (!days.Contains(day)) days.Add(day);
        }
        return days.OrderBy(day => day).ToArray();
    }

    static MarkdownItemKind ParseKind((string Value, int Line) field) =>
        field.Value.Trim().ToLowerInvariant() switch
        {
            "待办" or "todo" => MarkdownItemKind.Todo,
            "提醒" or "reminder" => MarkdownItemKind.Reminder,
            "日程" or "event" => MarkdownItemKind.Event,
            "长期事项" or "long_term" or "longterm" => MarkdownItemKind.LongTerm,
            "周期提醒" or "recurring" => MarkdownItemKind.Recurring,
            _ => throw new MarkdownItemDocumentException("不支持该事项类型。", field.Line)
        };

    public static string KindLabel(MarkdownItemKind kind) => kind switch
    {
        MarkdownItemKind.Todo => "待办",
        MarkdownItemKind.Reminder => "提醒",
        MarkdownItemKind.Event => "日程",
        MarkdownItemKind.LongTerm => "长期事项",
        MarkdownItemKind.Recurring => "周期提醒",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    static string RecurrenceLabel(RecurrenceKind? recurrence) => recurrence switch
    {
        RecurrenceKind.Daily => "每天",
        RecurrenceKind.Weekdays => "工作日",
        RecurrenceKind.OfficialWorkdays => "法定工作日",
        RecurrenceKind.StatutoryHolidays => "法定节假日",
        RecurrenceKind.Weekly => "每周",
        _ => ""
    };

    static string WeekdayLabel(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };

    static string FormatDate(DateTime? value) =>
        value?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? "";

    static void Field(StringBuilder markdown, string name, string? value) =>
        markdown.Append("- ").Append(name).Append('：').AppendLine(value ?? "");

    static string Escape(string? value) =>
        string.IsNullOrEmpty(value)
            ? ""
            : value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Replace("\n", "\\n", StringComparison.Ordinal);

    static string Unescape(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                result.Append(value[index]);
                continue;
            }
            var next = value[++index];
            result.Append(next == 'n' ? '\n' : next);
        }
        return result.ToString();
    }
}
