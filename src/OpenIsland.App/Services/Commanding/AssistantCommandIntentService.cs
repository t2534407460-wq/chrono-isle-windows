using System.Text.Json;

namespace OpenIsland.App.Services.Commanding;

/// <summary>
/// Converts an untrusted model response into the one strict command envelope accepted by the
/// local execution boundary. This type deliberately has no database access.
/// </summary>
public sealed class AssistantCommandIntentService(IChatCompletionClient chat)
{
    const string SystemPrompt = """
        You are Open Island's command parser. Return exactly one JSON object, with no Markdown or prose.
        The object must conform to this exact envelope:
        {"schemaVersion":1,"command":"...","arguments":{},"missingFields":[],"ambiguityReasons":[]}.
        Allowed command values: create_todo, create_reminder, create_event, list_items, update_todo,
        complete_todo, delete_todo, create_recurring_task, reschedule_item, decompose_goal, summarize_period.
        Never include IDs, row versions, clientRequestId, confirmationId, confidence, extra properties, or permissions.
        Time expressions use {"localDate":"YYYY-MM-DD or null","localTime":"HH:mm or null",
        "relativeExpression":"one hour later in the user's language or null","timeZoneHint":"IANA ID or null",
        "originalText":"the user's original time wording"}. Preserve originalText whenever a time object exists.
        If a requested field is missing or any time is vague (afternoon, evening, weekend, when free, soon,
        after work, before sleep, later), put a short reason in ambiguityReasons or missingFields and leave the
        unresolved field null. Do not invent a time.
        create_todo arguments: title, notes, due, remind, recurrence, priority.
        create_reminder arguments: title, notes, remind, due, recurrence, priority; remind is required.
        create_event arguments: title, notes, start, end, remind; start and end are required.
        list_items arguments: range, kind, includeCompleted.
        Existing target selectors contain only title, kind, timeHint; never an ID.
        A recurring task has title, notes, kind (todo or reminder), wallStart, recurrence, priority.
        Recurrence has frequency (daily, weekly, monthly), interval, weekdays, monthDay, end.
        decompose_goal arguments: goal, constraints, maxItems, proposedTasks. proposedTasks must contain 1 to
        maxItems task objects with title, priority, estimatedMinutes and category. Every title is required;
        estimatedMinutes is optional but positive when present. Never set a completed state or invent a schedule.
        """;

    public async Task<AssistantCommandParseResult> AnalyzeAsync(
        ProviderSettings provider,
        IEnumerable<ChatMessage> history,
        string input)
    {
        var messages = new List<ModelMessage>
        {
            new("system", SystemPrompt),
            new("system", $"Current local time: {DateTime.Now:O}; time zone: {TimeZoneInfo.Local.Id}.")
        };
        messages.AddRange(history.Select(message => new ModelMessage(message.Role, message.Content)));
        messages.Add(new("user", input));
        return Parse(await chat.Complete(provider, messages, jsonObject: true));
    }

    public static AssistantCommandParseResult Parse(string response)
    {
        if (AssistantCommandEnvelopeJson.TryDeserialize(response, out var envelope, out var error))
            return new(envelope, response, null);

        return new(null, response,
            error is null
                ? "模型没有返回可执行的命令，未创建任何事项。"
                : $"模型返回的命令无效（{error.Code}），未创建任何事项。");
    }
}

public sealed record AssistantCommandParseResult(
    AssistantCommandEnvelope? Envelope,
    string RawJson,
    string? ErrorMessage)
{
    public bool IsValid => Envelope is not null && ErrorMessage is null;
}
