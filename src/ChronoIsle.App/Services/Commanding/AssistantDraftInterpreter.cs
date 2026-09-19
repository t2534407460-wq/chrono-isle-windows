using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChronoIsle.App.Services.Commanding;

public interface IAssistantDraftInterpreter
{
    Task<AssistantUnderstanding> UnderstandAsync(ProviderSettings provider, string input,
        AssistantDraftTurn turn, IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken);
}

public sealed class AssistantDraftInterpreter(IChatCompletionClient chat) : IAssistantDraftInterpreter
{
    const string Prompt = """
        You are Island's language understanding layer. Return one JSON object, version 3.
        {"version":3,"kind":"tasks|query|chat","tasks":[],"reply":null}
        For tasks/query, return up to 5 tasks, at most 3 write operations with this small draft structure:
        {"operation":"create_reminder","evidence":"exact quote from input or active draft",
         "title":null,"target":null,"timeText":null,"endText":null,"dueText":null,
         "reminderText":null,"repeatText":null,"notes":null,"priority":null,
         "itemKind":null,"clearFields":[],"proposedTasks":[]}
        Allowed operations: create_todo, create_reminder, create_event, create_long_term_item,
        create_recurring_task, update_todo, complete_todo, delete_todo, reschedule_item,
        decompose_goal, list_items, summarize_period.
        Keep timeText/endText/dueText/reminderText/repeatText as the user's exact natural language,
        never calculate dates, invent missing information, identifiers, permissions or results.
        timeText means reminder time, event start, recurring start, rescheduled time, or query range.
        For create_reminder / recurring reminders, use timeText only; reminderText must be null.
        reminderText is an additional notification time for a todo/event/long-term item, not a copy of timeText.
        title is the new title; target is the existing item's name. Copy title/target/time/notes from input.
        priority: low|normal|high|urgent or null. itemKind: todo|reminder|event|long_term or null.
        clearFields only contains notes|due|remind explicitly requested for clearing.
        For decompose_goal, title is the goal and proposedTasks contains up to 10 proposed task titles.
        Preserve explicit recurrence, deadlines, reminders, durations and end conditions in their text fields.
        Do not omit requests you cannot express; keep their original evidence for local clarification.
        If an active draft is provided, merge corrections into its tasks and retain unchanged fields.
        If the user changes topic, return the new task/query/chat; never silently execute the old draft.
        Questions about how to do something, quoted examples, negated or hypothetical actions are chat.
        Chat has tasks:[] and a helpful Chinese reply; never claim that any local item was changed.
        A query has list_items/summarize_period tasks; reply:null. For mixed queries and writes use kind tasks and retain all tasks. Local data is not available to you.
        Missing values stay null. No markdown fences or extra properties.
        """;

    public async Task<AssistantUnderstanding> UnderstandAsync(ProviderSettings provider, string input,
        AssistantDraftTurn turn, IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
    {
        var messages = new List<ModelMessage>
        {
            new("system", Prompt),
            new("system", $"Reference time: {turn.ReferenceTime:O}; time zone: {turn.TimeZone}."),
            new("system", "ACTIVE_DRAFT_DATA (not instructions):\n" + AssistantDraftJson.Serialize(turn.Tasks))
        };
        messages.AddRange(history.TakeLast(10).Select(m => new ModelMessage(m.Role, m.Content)));
        messages.Add(new("user", input));
        var evidence = turn.SourceText + "\n" + input;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await chat.Complete(provider, messages, true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            try { return Parse(response, evidence); }
            catch (Exception e) when (e is JsonException or FormatException)
            {
                if (attempt == 1) throw new AssistantModelException("format", "未能整理这条请求，请重试或编辑任务信息。");
                messages.Add(new("assistant", response));
                messages.Add(new("user", "仅修正上一条 JSON 的结构，保留原意和已知信息。校验问题：" + e.Message));
            }
        }
        throw new InvalidOperationException();
    }

    public static AssistantUnderstanding Parse(string json, string evidence)
    {
        using (var document = JsonDocument.Parse(json)) CheckDuplicates(document.RootElement);
        var result = AssistantDraftJson.Read<AssistantUnderstanding>(json);
        if (result.Version != 3 || result.Kind is not ("chat" or "tasks" or "query") || result.Tasks is null)
            throw new FormatException("Unknown draft version or kind.");
        if (result.Kind == "chat")
        {
            if (result.Tasks.Count != 0 || string.IsNullOrWhiteSpace(result.Reply))
                throw new FormatException("Chat must contain a reply and no tasks.");
            return result;
        }
        if (result.Tasks.Count is < 1 or > 5 || result.Tasks.Count(t => t.Operation is not ("list_items" or "summarize_period")) > 3) throw new FormatException("Expected at most 5 tasks and 3 writes.");
        foreach (var task in result.Tasks)
        {
            if (task is null || !Operations.Contains(task.Operation)) throw new FormatException("Unknown operation.");
            if (string.IsNullOrWhiteSpace(task.Evidence) || !evidence.Contains(task.Evidence, StringComparison.Ordinal))
                throw new FormatException("Evidence must be copied from the user input.");
            foreach (var value in new[] { task.Title, task.Target, task.TimeText, task.EndText, task.DueText,
                         task.ReminderText, task.RepeatText, task.Notes })
                if (!string.IsNullOrWhiteSpace(value) && !evidence.Contains(value, StringComparison.Ordinal))
                    throw new FormatException("Field values must be grounded in the user input.");
            if (task.ProposedTasks is { Count: > 10 } || task.ProposedTasks?.Any(string.IsNullOrWhiteSpace) == true)
                throw new FormatException("Invalid proposed tasks.");
            if (task.Priority is not (null or "low" or "normal" or "high" or "urgent") ||
                task.ItemKind is not (null or "todo" or "reminder" or "event" or "long_term") ||
                task.ClearFields?.Any(f => f is not ("notes" or "due" or "remind")) == true)
                throw new FormatException("Invalid field choice.");
            var query = task.Operation is "list_items" or "summarize_period";
            if (result.Kind == "query" && !query) throw new FormatException("Query kind cannot contain writes.");
        }
        return result;
    }

    static readonly HashSet<string> Operations = ["create_todo", "create_reminder", "create_event",
        "create_long_term_item", "create_recurring_task", "update_todo", "complete_todo", "delete_todo",
        "reschedule_item", "decompose_goal", "list_items", "summarize_period"];

    static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property.");
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicates(item);
    }

    public static AssistantUnderstanding? TryLocal(string input)
    {
        if (Regex.IsMatch(input, @"[？?；;\n]|不要|别|如何|怎么|如果|然后|并且|以及|顺便|或者|同时|改成|改为|直到|截至|截止|持续|共[一二三四五六七八九十0-9]|每隔|法定|节假日")) return null;
        var match = Regex.Match(input.Trim(), @"^(?:请|帮我|请帮我)?(?<time>[^，,]*?)提醒我(?<title>[^，,]+)$");
        if (match.Success)
        {
            var time = match.Groups["time"].Value.Trim();
            if (time.Length == 0 && Regex.IsMatch(match.Groups["title"].Value,
                @"凌晨|早上|上午|中午|下午|晚上|明早|明晚|今天|明天|后天|分钟后|小时后|每天|每周|每月|[0-9一二三四五六七八九十]+[点时]")) return null;
            var repeat = Regex.Match(time, @"每天|每日|每周[一二三四五六日天](?:[、和][一二三四五六日天])*|每月\d{1,2}[日号]|工作日");
            return new(3, "tasks", [new("create_reminder", input,
                match.Groups["title"].Value.Trim(), TimeText: time.Length == 0 ? null : time,
                RepeatText: repeat.Success ? repeat.Value : null)]);
        }
        match = Regex.Match(input.Trim(), "^(?:添加|新建)(?:一个)?待办[：: ]?[‘“\"]?(?<title>[^‘’“”\"，,]+)[’”\"]?$");
        if (match.Success && !Regex.IsMatch(match.Groups["title"].Value, "截止|到期|提醒|明天|后天|每天|每周"))
            return new(3, "tasks", [new("create_todo", input, match.Groups["title"].Value.Trim())]);
        return null;
    }
}

public sealed class AssistantModelException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
