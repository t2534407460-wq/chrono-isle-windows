using System.Text.RegularExpressions;

namespace ChronoIsle.App.Services;

public sealed class ConversationRouter
{
    static readonly string[] CreateWords = ["添加", "创建", "新建", "设置", "安排", "拆解", "提醒我", "帮我记", "记得", "每天", "每周", "工作日"];
    static readonly string[] AgendaWords = ["待办", "提醒", "日程", "安排", "事项"];
    static readonly string[] QueryWords = ["查看", "查询", "多少", "哪些", "什么", "有没有", "有吗", "列表"];
    static readonly string[] ExistingItemMutationWords = ["修改", "改到", "改为", "调整", "重新安排", "重排", "延后", "提前", "取消", "删除", "完成", "设为", "收到"];
    static readonly string[] QuestionWords = ["怎么", "如何", "为什么", "什么", "几", "吗", "？", "?"];

    public ConversationRoute Decide(string input, AssistantAction? activeDraft, DateTime now)
    {
        var text = input.Trim();
        // A new, explicit operation on existing items must not be consumed as an
        // answer to an older clarification. Otherwise a stale draft sends the
        // request back to the creation-only model parser.
        if (IsExistingItemMutation(text)) return new(ConversationRouteKind.ModificationClarification);
        if (activeDraft is not null) return new(ConversationRouteKind.CreateAction);
        if (IsCreateRequest(text)) return new(ConversationRouteKind.CreateAction);
        return TryQuery(text, now, out var query)
            ? new ConversationRoute(ConversationRouteKind.LocalQuery, query)
            : new ConversationRoute(ConversationRouteKind.GeneralChat);
    }

    static bool IsExistingItemMutation(string text)
    {
        if (QuestionWords.Any(text.Contains) || !AgendaWords.Any(text.Contains)) return false;
        if (ExistingItemMutationWords.Any(text.Contains)) return true;

        // A bulk selector plus a clock is a modification candidate, not an implicit new reminder.
        return (text.Contains("所有", StringComparison.Ordinal) || text.Contains("全部", StringComparison.Ordinal)) &&
            Regex.IsMatch(text, @"\d{1,2}\s*(点|:)");
    }

    static bool IsCreateRequest(string text)
    {
        if (text.StartsWith("今天有") || text.StartsWith("当前有") || text.StartsWith("本周有") || text.StartsWith("这周有") || text.StartsWith("本月有") || text.StartsWith("这个月有")) return false;
        if (QuestionWords.Any(text.Contains)) return false;
        if (CreateWords.Any(text.Contains)) return true;
        var hasFutureTime = text.Contains("明天") || text.Contains("后天") || Regex.IsMatch(text, @"\d{1,2}\s*(点|:)");
        return hasFutureTime && text.Length <= 40;
    }

    static bool TryQuery(string text, DateTime now, out LocalAgendaQuery? query)
    {
        query = null;
        if (!AgendaWords.Any(text.Contains)) return false;
        var hasQueryWording = QueryWords.Any(text.Contains);
        if (text.Contains("本周") || text.Contains("这周"))
        {
            var start = now.Date.AddDays(-((int)now.DayOfWeek + 6) % 7);
            query = new(start, start.AddDays(7), "本周");
            return true;
        }
        if (text.Contains("本月") || text.Contains("这个月"))
        {
            var start = new DateTime(now.Year, now.Month, 1);
            query = new(start, start.AddMonths(1), "本月");
            return true;
        }
        if (text.Contains("今天") || text.Contains("当前") || hasQueryWording)
        {
            var start = now.Date;
            query = new(start, start.AddDays(1), "今天");
            return true;
        }
        return false;
    }
}