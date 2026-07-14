namespace OpenIsland.App.Services;

public sealed class AssistantActionService(
    LifeDataService data,
    AssistantIntentService intents,
    ConversationRouter router,
    LocalAgendaQueryService localQueries,
    OpenAiChatService chat)
{
    public async Task<AssistantConversationResult> HandleAsync(
        ProviderSettings provider,
        ChatSession session,
        IReadOnlyList<ChatMessage> history,
        string input)
    {
        var activeDraft = data.ActiveClarification(session.Id);
        var route = router.Decide(input, activeDraft, DateTime.Now);
        return route.Kind switch
        {
            ConversationRouteKind.CreateAction => await HandleCreationAsync(provider, session, history, input, activeDraft),
            ConversationRouteKind.LocalQuery => await HandleLocalQueryAsync(provider, history, input, route.Query!),
            _ => await HandleGeneralChatAsync(provider, history, input)
        };
    }

    async Task<AssistantConversationResult> HandleCreationAsync(
        ProviderSettings provider,
        ChatSession session,
        IReadOnlyList<ChatMessage> history,
        string input,
        AssistantAction? activeDraft)
    {
        var analysis = await intents.AnalyzeAsync(provider, history, input, activeDraft);
        if (!analysis.IsValid) return new(analysis.ErrorMessage!, null, true);

        if (analysis.NeedsClarification)
        {
            var clarification = data.SaveAction(session.Id, input, analysis.RawJson, "clarifying", null, activeDraft?.Id);
            return new(analysis.ClarificationMessage!, clarification, false);
        }

        var action = data.SaveAction(session.Id, input, analysis.RawJson, "awaiting_confirmation", null, activeDraft?.Id);
        return new(ConfirmationText(analysis.Intent!), action, false);
    }

    async Task<AssistantConversationResult> HandleLocalQueryAsync(
        ProviderSettings provider,
        IReadOnlyList<ChatMessage> history,
        string input,
        LocalAgendaQuery query)
    {
        var local = localQueries.Query(query);
        try
        {
            var messages = new List<ModelMessage>
            {
                new("system", "You are Island. Answer in Chinese using only the LOCAL_DATA supplied below. Do not invent records, create records, or repeat the full list. Give a concise useful summary."),
                new("system", $"Current local time: {DateTime.Now:O}; time zone: {TimeZoneInfo.Local.Id}.")
            };
            messages.AddRange(history.Select(message => new ModelMessage(message.Role, message.Content)));
            messages.Add(new("user", $"QUESTION:\n{input}\n\nLOCAL_DATA:\n{localQueries.StructuredContext(local)}"));
            var summary = await chat.Complete(provider, messages);
            return new($"{local.ListText}\n\n### Island \u6982\u62EC\n{summary.Trim()}", null, false);
        }
        catch (Exception exception)
        {
            return new($"{local.ListText}\n\n> Island \u672A\u80FD\u751F\u6210\u6982\u62EC\uFF1A{exception.Message}", null, false);
        }
    }

    async Task<AssistantConversationResult> HandleGeneralChatAsync(
        ProviderSettings provider,
        IReadOnlyList<ChatMessage> history,
        string input)
    {
        var messages = new List<ModelMessage>
        {
            new("system", $"You are Island, a helpful local organizer. Current local time: {DateTime.Now:O}; time zone: {TimeZoneInfo.Local.Id}. Answer in the user's language. Do not claim to create or modify local records."),
        };
        messages.AddRange(history.Select(message => new ModelMessage(message.Role, message.Content)));
        messages.Add(new("user", input));
        return new(await chat.Complete(provider, messages), null, false);
    }

    public ActionExecutionResult Confirm(string actionId)
    {
        var action = data.Action(actionId);
        if (action is null || action.Status != "awaiting_confirmation")
            return new(false, "\u8FD9\u4E2A\u786E\u8BA4\u5DF2\u5931\u6548\uFF0C\u672A\u521B\u5EFA\u4EFB\u4F55\u4E8B\u9879\u3002", null);

        var analysis = AssistantIntentService.Parse(action.IntentJson);
        if (!analysis.IsValid || !analysis.NeedsConfirmation)
        {
            data.SetActionStatus(actionId, "failed", analysis.ErrorMessage ?? "\u786E\u8BA4\u6570\u636E\u4E0D\u5B8C\u6574\u3002");
            return new(false, "\u786E\u8BA4\u6570\u636E\u65E0\u6548\uFF0C\u672A\u521B\u5EFA\u4EFB\u4F55\u4E8B\u9879\u3002", null);
        }

        try
        {
            var item = analysis.Intent!.Kind switch
            {
                AssistantIntentKind.CreateTodo => CreateTodo(analysis.Intent),
                AssistantIntentKind.CreateEvent => CreateEvent(analysis.Intent),
                AssistantIntentKind.CreateRecurringReminder => CreateRecurringReminder(analysis.Intent),
                _ => throw new InvalidOperationException("\u4E0D\u652F\u6301\u786E\u8BA4\u8FD9\u4E2A\u52A8\u4F5C\u3002")
            };
            data.SetActionStatus(actionId, "confirmed");
            return new(true, $"\u5DF2\u521B\u5EFA{KindName(item.Kind)}\uFF1A{item.Title}", item);
        }
        catch (Exception exception)
        {
            data.SetActionStatus(actionId, "failed", exception.Message);
            return new(false, $"\u521B\u5EFA\u5931\u8D25\uFF1A{exception.Message}", null);
        }
    }

    public void Cancel(string actionId) => data.SetActionStatus(actionId, "cancelled");

    AgendaItem CreateTodo(AssistantIntent intent)
    {
        var todo = data.Save(intent.Title!, intent.Notes, intent.DueAt, intent.ReminderAt);
        return new(todo.Id, "todo", todo.Title, todo.Notes, todo.DueAt ?? DateTime.Now, null, todo.RemindAt, todo.IsCompleted);
    }

    AgendaItem CreateEvent(AssistantIntent intent)
    {
        var item = data.SaveEvent(intent.Title!, intent.Notes, intent.StartsAt!.Value, intent.EndsAt!.Value, intent.ReminderAt ?? intent.StartsAt);
        return new(item.Id, "event", item.Title, item.Notes, item.StartsAt, item.EndsAt, item.RemindAt, false);
    }

    AgendaItem CreateRecurringReminder(AssistantIntent intent)
    {
        var reminder = data.SaveRecurringReminder(intent.Title!, intent.Notes, intent.ReminderTime!.Value, intent.Recurrence!.Value, intent.Weekdays);
        return data.NextOccurrence(reminder, DateTime.Now)
            ?? throw new InvalidOperationException("\u65E0\u6CD5\u8BA1\u7B97\u4E0B\u4E00\u6B21\u5468\u671F\u63D0\u9192\u3002");
    }

    static string ConfirmationText(AssistantIntent intent) => intent.Kind switch
    {
        AssistantIntentKind.CreateTodo =>
            $"\u8BC6\u522B\u5230\u5F85\u529E\uFF1A{intent.Title}\n{FormatTime(intent.DueAt, "\u622A\u6B62")}{FormatTime(intent.ReminderAt, "\u63D0\u9192")}".Trim(),
        AssistantIntentKind.CreateEvent =>
            $"\u8BC6\u522B\u5230\u65E5\u7A0B\uFF1A{intent.Title}\n\u65F6\u95F4\uFF1A{intent.StartsAt:yyyy-MM-dd HH:mm} \u2013 {intent.EndsAt:HH:mm}\n\u63D0\u9192\uFF1A{(intent.ReminderAt ?? intent.StartsAt):yyyy-MM-dd HH:mm}",
        AssistantIntentKind.CreateRecurringReminder =>
            $"\u8BC6\u522B\u5230\u5468\u671F\u63D0\u9192\uFF1A{intent.Title}\n\u65F6\u95F4\uFF1A\u6BCF\u5929 {intent.ReminderTime:HH:mm}\n\u5468\u671F\uFF1A{RecurrenceText(intent.Recurrence!.Value, intent.Weekdays)}",
        _ => throw new InvalidOperationException("\u4E0D\u652F\u6301\u786E\u8BA4\u8FD9\u4E2A\u52A8\u4F5C\u3002")
    };

    static string FormatTime(DateTime? value, string label) => value is null ? "" : $"{label}\uFF1A{value:yyyy-MM-dd HH:mm}\n";

    static string KindName(string kind) => kind switch
    {
        "event" => "\u65E5\u7A0B",
        "recurring" => "\u5468\u671F\u63D0\u9192",
        "reminder" => "\u63D0\u9192",
        _ => "\u5F85\u529E"
    };

    public static string RecurrenceText(RecurrenceKind recurrence, IReadOnlyList<DayOfWeek> weekdays) => recurrence switch
    {
        RecurrenceKind.Daily => "\u6BCF\u5929",
        RecurrenceKind.Weekdays => "\u5DE5\u4F5C\u65E5",
        RecurrenceKind.Weekly => "\u6BCF\u5468" + string.Join("\u3001", weekdays.OrderBy(day => day).Select(DayName)),
        _ => throw new ArgumentOutOfRangeException(nameof(recurrence))
    };

    static string DayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "\u4E00",
        DayOfWeek.Tuesday => "\u4E8C",
        DayOfWeek.Wednesday => "\u4E09",
        DayOfWeek.Thursday => "\u56DB",
        DayOfWeek.Friday => "\u4E94",
        DayOfWeek.Saturday => "\u516D",
        _ => "\u65E5"
    };
}