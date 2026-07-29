using System.Text.Json;

namespace ChronoIsle.App.Services;

public sealed class LocalAgendaQueryService(LifeDataService data)
{
    public LocalAgendaQueryResult Query(LocalAgendaQuery query)
    {
        var items = data.AgendaForRange(query.StartsAt, query.EndsAt);
        return new(query, items, Format(query, items));
    }

    public string StructuredContext(LocalAgendaQueryResult result) => JsonSerializer.Serialize(new
    {
        rangeStart = result.Query.StartsAt,
        rangeEnd = result.Query.EndsAt,
        result.Query.Label,
        items = result.Items.Select(item => new
        {
            type = item.Kind,
            item.Title,
            item.Notes,
            startsAt = item.StartsAt,
            endsAt = item.EndsAt,
            remindAt = item.RemindAt,
            completed = item.IsCompleted
        })
    });
    static string Format(LocalAgendaQuery query, IReadOnlyList<AgendaItem> items)
    {
        var heading = $"## {query.Label}\u7684\u672C\u5730\u4E8B\u9879";
        if (items.Count == 0) return heading + "\n- \u6682\u65E0\u5F85\u529E\u3001\u957F\u671F\u4E8B\u9879\u3001\u65E5\u7A0B\u6216\u63D0\u9192\u3002";
        return heading + "\n" + string.Join("\n", items.Select(FormatItem));
    }

    static string FormatItem(AgendaItem item)
    {
        var time = item.Kind == "event" && item.EndsAt is not null
            ? $"{item.StartsAt:MM-dd HH:mm}\u2013{item.EndsAt:HH:mm}"
            : item.StartsAt.ToString("MM-dd HH:mm");
        var completed = item.Kind == "todo" && item.IsCompleted ? "\uFF08\u5DF2\u5B8C\u6210\uFF09" : "";
        return $"- {time} [{KindName(item.Kind)}]{completed} {item.Title}";
    }

    static string KindName(string kind) => kind switch
    {
        "event" => "\u65E5\u7A0B",
        "reminder" => "\u63D0\u9192",
        "recurring" => "\u5468\u671F\u63D0\u9192",
        "long_term" => "\u957F\u671F\u4E8B\u9879",
        _ => "\u5F85\u529E"
    };
}