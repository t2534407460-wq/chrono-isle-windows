using System.Text.Json;
using System.Text.RegularExpressions;
using ChronoIsle.App.Services.Reporting;
using ChronoIsle.App.Services.Commanding;
using ChronoIsle.App.Services.Productivity;
using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.App.Services;

public sealed class AssistantActionService
{
    readonly LifeDataService data;
    readonly ChinaStatutoryHolidayCalendar holidays;
    readonly ConversationRouter router;
    readonly LocalAgendaQueryService localQueries;
    readonly IChatCompletionClient chat;
    readonly LifePreferencesService? preferences;
    readonly AssistantCommandIntentService commandParser;
    readonly AssistantCommandPipeline commandPipeline;
    readonly DraftStore drafts;

    public AssistantActionService(
        LifeDataService data,
        ChinaStatutoryHolidayCalendar holidays,
        ConversationRouter router,
        LocalAgendaQueryService localQueries,
        IChatCompletionClient chat,
        AssistantCommandIntentService? commandIntentParser = null,
        AssistantCommandPipeline? pipeline = null,
        LifePreferencesService? preferences = null)
    {
        this.data = data;
        this.holidays = holidays;
        this.router = router;
        this.localQueries = localQueries;
        this.chat = chat;
        this.preferences = preferences;
        commandParser = commandIntentParser ?? new AssistantCommandIntentService(chat);
        commandPipeline = pipeline ?? new AssistantCommandPipeline(data.DatabasePath);
        var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(data.DatabasePath);
        new ProductivitySchemaInitializer(runtime.WriteQueue).Initialize();
        drafts = new DraftStore(runtime.WriteQueue, runtime.ConnectionFactory);
    }

    // Existing callers can still supply the retired parser while persisted legacy confirmations
    // remain supported. New requests never use it to bypass the command boundary.
    public AssistantActionService(
        LifeDataService data,
        ChinaStatutoryHolidayCalendar holidays,
        AssistantIntentService _,
        ConversationRouter router,
        LocalAgendaQueryService localQueries,
        IChatCompletionClient chat)
        : this(data, holidays, router, localQueries, chat)
    {
    }

    public async Task<AssistantConversationResult> HandleAsync(
        ProviderSettings provider,
        ChatSession session,
        IReadOnlyList<ChatMessage> history,
        string input,
        Action<string>? onDelta = null,
        CancellationToken cancellationToken = default)
    {
        if (TryParseOfficialSleepSchedule(input, out var sleepSchedule))
        {
            var previousHolidayBatch = data.ActiveHolidayReminderBatch(session.Id);
            if (previousHolidayBatch is not null) data.SetActionStatus(previousHolidayBatch.Id, "superseded");
            return ApplyPersona(PrepareOfficialSleepScheduleConfirmation(session, input, sleepSchedule));
        }

        var holidayBatch = data.ActiveHolidayReminderBatch(session.Id);
        if (holidayBatch is not null)
            return ApplyPersona(ContinueHolidayReminderBatch(session, input, holidayBatch));

        if (TryParseExplicitClock(input, out var followupTime) && LastUserRequestedHolidayBatch(history))
            return ApplyPersona(PrepareHolidayReminderBatchConfirmation(session, input, followupTime, null));

        var activeDraft = data.ActiveClarification(session.Id);
        if (activeDraft is not null && TryReadTimeSuggestion(activeDraft.IntentJson, out var suggestion))
            return ApplyPersona(ContinueTimeSuggestion(activeDraft, input, suggestion));

        var route = router.Decide(input, activeDraft, DateTime.Now);
        var result = route.Kind switch
        {
            ConversationRouteKind.CreateAction => await HandleCreationAsync(provider, session, history, input, activeDraft),
            ConversationRouteKind.ModificationClarification => HandleModificationClarification(session, input, activeDraft),
            ConversationRouteKind.LocalQuery => await HandleLocalQueryAsync(provider, history, input, route.Query!),
            _ => await HandleGeneralChatAsync(provider, history, input, onDelta, cancellationToken)
        };
        return ApplyPersona(result);
    }

    AssistantConversationResult ApplyPersona(AssistantConversationResult result)
    {
        if (!Enum.TryParse<AssistantPersona>(preferences?.Load().AssistantPersona, out var persona) || persona == AssistantPersona.Direct)
            return result;
        var profile = AssistantPersonaFormatter.GetProfile(persona);
        var prefix = result.IsFailure
            ? profile.FailurePrefix
            : result.PendingAction is not null
                ? profile.ConfirmationPrompt
                : result.RefreshReminders
                    ? profile.SuccessPrefix
                    : profile.Acknowledgement;
        return result with { Reply = $"{prefix}\n{result.Reply}" };
    }

    async Task<AssistantConversationResult> HandleCreationAsync(
        ProviderSettings provider,
        ChatSession session,
        IReadOnlyList<ChatMessage> history,
        string input,
        AssistantAction? activeDraft)
    {
        var parsed = TryParseExplicitSingleReminder(input, DateTime.Now, out var localReminder)
            ? new AssistantCommandParseResult(localReminder, string.Empty, null)
            : await commandParser.AnalyzeAsync(provider, history, input);
        if (!parsed.IsValid) return new(parsed.ErrorMessage!, null, true);
        if (parsed.Envelope!.Command == AssistantCommandName.DecomposeGoal)
            return PrepareDecompositionDraft(session, input, (DecomposeGoalArgumentsV1)parsed.Envelope.Arguments, activeDraft);


        AssistantCommandPipelineResult result;
        try
        {
            result = commandPipeline.SubmitParsed(input, parsed.Envelope!);
        }
        catch (Exception exception) when (exception is AssistantCommandContractException or InvalidOperationException or ArgumentException)
        {
            return new($"命令未通过本地安全校验：{exception.Message}。未创建任何事项。", null, true);
        }

        return result.State switch
        {
            AssistantCommandPipelineState.ClarificationRequired when AmbiguousTimeSuggestionPlanner.CanOffer(input, parsed.Envelope!) =>
                PrepareTimeSuggestions(session, input, parsed.Envelope!, activeDraft),
            AssistantCommandPipelineState.Succeeded => new(PipelineSuccessText(parsed.Envelope!, result), null, false, true),
            AssistantCommandPipelineState.AwaitingConfirmation => PreparePipelineConfirmation(session, input, parsed.Envelope!, result, activeDraft),
            AssistantCommandPipelineState.ClarificationRequired => new(PipelineClarificationText(parsed.Envelope!, result.Code), null, false),
            _ => new($"命令未执行（{result.Code}），未创建任何事项。", null, true)
        };
    }

    static bool TryParseExplicitSingleReminder(string input, DateTime now, out AssistantCommandEnvelope envelope)
    {
        envelope = default!;
        if (input.Contains("每天", StringComparison.Ordinal) || input.Contains("每周", StringComparison.Ordinal) ||
            input.Contains("工作日", StringComparison.Ordinal) || input.Contains("节假日", StringComparison.Ordinal))
            return false;

        const string timePattern = @"(?<time>(?:(?<date>今天|明天|后天)\s*)?(?<period>凌晨|上午|中午|下午|晚上)?\s*(?<hour>\d{1,2}|[零〇一二两三四五六七八九十]{1,3})\s*(?:(?:点|时)\s*(?:(?<half>半)|(?<minute>\d{1,2})\s*分?)?|:\s*(?<colonMinute>\d{2})))";
        var match = Regex.Match(input, $@"^\s*{timePattern}\s*(?:提醒(?:我)?|叫我|记得)\s*(?<title>.+?)\s*[。！？!?]?\s*$");
        if (!match.Success)
            match = Regex.Match(input, $@"^\s*(?:提醒(?:我)?|叫我|记得)\s*{timePattern}\s*(?<title>.+?)\s*[。！？!?]?\s*$");
        if (!match.Success) return false;

        if (!TryParseReminderHour(match.Groups["hour"].Value, out var hour))
            return false;
        var period = match.Groups["period"].Value;
        if (period == "凌晨" && hour == 12) hour = 0;
        if ((period == "中午" || period == "下午" || period == "晚上") && hour is >= 1 and <= 11) hour += 12;
        var minuteText = match.Groups["minute"].Success
            ? match.Groups["minute"].Value
            : match.Groups["colonMinute"].Value;
        var minute = match.Groups["half"].Success ? 30 : string.IsNullOrEmpty(minuteText) ? 0 : int.Parse(minuteText);
        if (minute is < 0 or > 59) return false;

        var title = match.Groups["title"].Value.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200) return false;

        var date = now.Date.AddDays(match.Groups["date"].Value switch
        {
            "明天" => 1,
            "后天" => 2,
            _ => 0
        });
        var scheduled = date.AddHours(hour).AddMinutes(minute);
        if (!match.Groups["date"].Success && scheduled <= now) scheduled = scheduled.AddDays(1);
        if (match.Groups["date"].Value == "今天" && scheduled <= now) return false;

        var originalTime = match.Groups["time"].Value.Trim();
        envelope = new AssistantCommandEnvelope(
            AssistantCommandSchema.V1,
            AssistantCommandName.CreateReminder,
            new CreateReminderArgumentsV1(
                title,
                null,
                new AssistantTimeExpressionV1(DateOnly.FromDateTime(scheduled), TimeOnly.FromDateTime(scheduled), null, null, originalTime),
                null,
                null,
                null),
            [],
            []);
        return true;
    }

    static bool TryParseReminderHour(string value, out int hour)
    {
        if (int.TryParse(value, out hour)) return hour is >= 0 and <= 23;

        var normalized = value.Replace('〇', '零').Replace('两', '二');
        var tenIndex = normalized.IndexOf('十');
        if (tenIndex < 0)
        {
            hour = normalized.Length == 1 ? ChineseDigit(normalized[0]) : -1;
            return hour is >= 0 and <= 9;
        }

        if (tenIndex != normalized.LastIndexOf('十') ||
            tenIndex > 1 ||
            normalized.Length - tenIndex > 2)
        {
            hour = 0;
            return false;
        }

        var tens = tenIndex == 0 ? 1 : ChineseDigit(normalized[0]);
        var ones = tenIndex == normalized.Length - 1 ? 0 : ChineseDigit(normalized[^1]);
        hour = tens * 10 + ones;
        return tens is >= 1 and <= 2 && ones is >= 0 and <= 9 && hour <= 23;
    }

    static int ChineseDigit(char value) => value switch
    {
        '零' => 0, '一' => 1, '二' => 2, '三' => 3, '四' => 4,
        '五' => 5, '六' => 6, '七' => 7, '八' => 8, '九' => 9,
        _ => -1
    };

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
        string input,
        Action<string>? onDelta,
        CancellationToken cancellationToken)
    {
        var messages = new List<ModelMessage>
        {
            new("system", $"You are Island, a helpful local organizer. Current local time: {DateTime.Now:O}; time zone: {TimeZoneInfo.Local.Id}. Answer in the user's language. Do not claim to create or modify local records."),
        };
        messages.AddRange(history.Select(message => new ModelMessage(message.Role, message.Content)));
        messages.Add(new("user", input));
        if (onDelta is null)
            return new(await chat.Complete(provider, messages), null, false);

        var reply = new System.Text.StringBuilder();
        await foreach (var delta in chat.StreamComplete(provider, messages, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            reply.Append(delta);
            onDelta(delta);
        }
        return new(reply.ToString(), null, false);
    }

    public CommandDraftView? GetPendingDecompositionDraft(string actionId)
    {
        var action = data.Action(actionId);
        return action?.Status == "awaiting_confirmation" && TryReadDecompositionDraft(action.IntentJson, out var decomposition)
            ? drafts.Get(decomposition.DraftId)
            : null;
    }

    public ActionExecutionResult ConfirmDecompositionDraft(string actionId, IReadOnlySet<string> selectedItemIds,
        IReadOnlyDictionary<string, DateTimeOffset?> scheduleOverrides)
    {
        var action = data.Action(actionId);
        if (action is null || action.Status != "awaiting_confirmation" ||
            !TryReadDecompositionDraft(action.IntentJson, out var decomposition))
            return new(false, "这个拆解确认已失效，未创建任何事项。", null);

        try
        {
            var ids = drafts.ConfirmSelected(decomposition.DraftId, selectedItemIds, scheduleOverrides);
            data.SetActionStatus(actionId, "confirmed");
            var arranged = scheduleOverrides.Count;
            return new(true, arranged == 0
                ? $"已将“{decomposition.Goal}”选择的 {ids.Count} 项任务加入待办。"
                : $"已将“{decomposition.Goal}”选择的 {ids.Count} 项任务加入待办，其中 {arranged} 项已安排时间。", null);
        }
        catch (Exception exception)
        {
            data.SetActionStatus(actionId, "failed", exception.Message);
            return new(false, $"任务拆解未写入：{exception.Message}。", null);
        }
    }

    public ActionExecutionResult Confirm(string actionId)
    {
        var action = data.Action(actionId);
        if (action is null || action.Status != "awaiting_confirmation")
            return new(false, "\u8FD9\u4E2A\u786E\u8BA4\u5DF2\u5931\u6548\uFF0C\u672A\u521B\u5EFA\u4EFB\u4F55\u4E8B\u9879\u3002", null);

        if (TryReadDecompositionDraft(action.IntentJson, out var decomposition))
        {
            try
            {
                var ids = drafts.Confirm(decomposition.DraftId);
                data.SetActionStatus(actionId, "confirmed");
                return new(true, $"已将“{decomposition.Goal}”拆解的 {ids.Count} 项任务加入待办。", null);
            }
            catch (Exception exception)
            {
                data.SetActionStatus(actionId, "failed", exception.Message);
                return new(false, $"任务拆解未写入：{exception.Message}。", null);
            }
        }

        if (TryReadPipelineConfirmation(action.IntentJson, out var pipelineConfirmation))
        {
            try
            {
                var result = commandPipeline.Confirm(pipelineConfirmation.ConfirmationId);
                var succeeded = result.State == AssistantCommandPipelineState.Succeeded;
                data.SetActionStatus(actionId, succeeded ? "confirmed" : result.State.ToString().ToLowerInvariant(),
                    succeeded ? null : result.Code);
                return new(succeeded, succeeded
                    ? PipelineConfirmedText(pipelineConfirmation.Command, result)
                    : $"确认未执行：{ConfirmationFailureText(result.Code)}。未修改任何事项。", null);
            }
            catch (Exception exception)
            {
                data.SetActionStatus(actionId, "failed", exception.Message);
                return new(false, $"确认执行失败：{exception.Message}。未修改任何事项。", null);
            }
        }

        if (TryReadOfficialSleepSchedule(action.IntentJson, out var sleepSchedule))
        {
            try
            {
                data.SaveOfficialSleepReminderSchedule(
                    TimeOnly.ParseExact(sleepSchedule.OfficialWorkdayTime, "HH:mm"),
                    TimeOnly.ParseExact(sleepSchedule.StatutoryHolidayTime, "HH:mm"));
                data.SetActionStatus(actionId, "confirmed");
                return new(true, $"\u5DF2\u8BBE\u7F6E\u7761\u89C9\u63D0\u9192\uFF1A\u6CD5\u5B9A\u5DE5\u4F5C\u65E5 {sleepSchedule.OfficialWorkdayTime}\uFF0C\u6CD5\u5B9A\u8282\u5047\u65E5 {sleepSchedule.StatutoryHolidayTime}\u3002", null);
            }
            catch (Exception exception)
            {
                data.SetActionStatus(actionId, "failed", exception.Message);
                return new(false, $"\u8BBE\u7F6E\u7761\u89C9\u63D0\u9192\u5931\u8D25\uFF1A{exception.Message}", null);
            }
        }

        if (TryReadHolidayReminderBatch(action.IntentJson, out var holidayBatch))
        {
            try
            {
                var result = data.RescheduleHolidayReminderTargets(holidayBatch.Targets, TimeOnly.ParseExact(holidayBatch.ReminderTime, "HH:mm"));
                data.SetActionStatus(actionId, "confirmed");
                return new(true, $"\u5DF2\u5C06 {result.AppliedCount} \u6761\u6CD5\u5B9A\u8282\u5047\u65E5\u7684\u5355\u6B21\u63D0\u9192\u6539\u4E3A\u6BCF\u5929 {holidayBatch.ReminderTime} \u518D\u63D0\u9192\uFF0C\u4FDD\u7559\u539F\u65E5\u671F\u548C\u5176\u4ED6\u4FE1\u606F\u3002", null);
            }
            catch (Exception exception)
            {
                data.SetActionStatus(actionId, "failed", exception.Message);
                return new(false, $"\u6279\u91CF\u4FEE\u6539\u5931\u8D25\uFF1A{exception.Message}", null);
            }
        }

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

    public void Cancel(string actionId)
    {
        var action = data.Action(actionId);
        if (action is not null && action.Status == "awaiting_confirmation" &&
            TryReadDecompositionDraft(action.IntentJson, out var decomposition))
        {
            try { drafts.Cancel(decomposition.DraftId); }
            catch (InvalidOperationException) { }
        }
        data.SetActionStatus(actionId, "cancelled");
    }

    AssistantConversationResult PrepareDecompositionDraft(
        ChatSession session,
        string sourceText,
        DecomposeGoalArgumentsV1 arguments,
        AssistantAction? previousAction)
    {
        var proposed = arguments.ProposedTasks;
        if (string.IsNullOrWhiteSpace(arguments.Goal) || proposed is not { Count: > 0 })
            return new("为了生成可确认的拆解草稿，请补充目标；Tuux 还需要模型给出至少一项可执行子任务。", null, false);

        try
        {
            var proposals = proposed.Select(task => new DraftProposal(
                task.Title!.Trim(),
                Priority: task.Priority?.ToString() ?? "Normal",
                Category: string.IsNullOrWhiteSpace(task.Category) ? null : task.Category.Trim(),
                EstimatedMinutes: task.EstimatedMinutes)).ToArray();
            var draftId = drafts.Create(proposals);
            var action = data.SaveAction(
                session.Id,
                sourceText,
                JsonSerializer.Serialize(new DecompositionDraftAction("decomposition_draft_v1", draftId, arguments.Goal.Trim())),
                "awaiting_confirmation",
                null,
                previousAction?.Id);
            return new AssistantConversationResult(DecompositionPreviewText(arguments.Goal, proposals), action, false);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return new($"任务拆解草稿未保存：{exception.Message}。未创建任何事项。", null, true);
        }
    }

    AssistantConversationResult PrepareTimeSuggestions(
        ChatSession session,
        string sourceText,
        AssistantCommandEnvelope envelope,
        AssistantAction? previousAction)
    {
        var request = envelope.Arguments switch
        {
            CreateReminderArgumentsV1 reminder => new TimeSuggestionAction(
                "time_suggestion_v1", "create_reminder", reminder.Title!.Trim(), reminder.Notes,
                reminder.Priority, AmbiguousTimeSuggestionPlanner.SuggestedTimes(DateTime.Now)),
            CreateTodoArgumentsV1 todo => new TimeSuggestionAction(
                "time_suggestion_v1", "create_todo", todo.Title!.Trim(), todo.Notes,
                todo.Priority, AmbiguousTimeSuggestionPlanner.SuggestedTimes(DateTime.Now)),
            _ => throw new InvalidOperationException("This command cannot be scheduled from a time suggestion.")
        };
        var action = data.SaveAction(session.Id, sourceText, JsonSerializer.Serialize(request), "clarifying", null, previousAction?.Id);
        var choices = request.Options.Select((option, index) => $"{index + 1}. {option:MM月dd日 HH:mm}");
        return new AssistantConversationResult(
            $"“{request.Title}”的时间还不明确，Tuux 不会自动创建。可以选择：\n{string.Join("\n", choices)}\n\n请回复 1、2 或 3。",
            action,
            false);
    }

    AssistantConversationResult ContinueTimeSuggestion(
        AssistantAction action,
        string input,
        TimeSuggestionAction suggestion)
    {
        if (!int.TryParse(input.Trim(), out var selected) || selected < 1 || selected > suggestion.Options.Count)
            return new("请只回复候选序号 1、2 或 3；在选择前不会创建任何事项。", null, false);

        var scheduledAt = suggestion.Options[selected - 1];
        var time = new AssistantTimeExpressionV1(
            DateOnly.FromDateTime(scheduledAt), TimeOnly.FromDateTime(scheduledAt), null, null,
            scheduledAt.ToString("yyyy-MM-dd HH:mm"));
        var envelope = suggestion.Command switch
        {
            "create_reminder" => new AssistantCommandEnvelope(
                AssistantCommandSchema.V1, AssistantCommandName.CreateReminder,
                new CreateReminderArgumentsV1(suggestion.Title, suggestion.Notes, time, null, null, suggestion.Priority), [], []),
            "create_todo" => new AssistantCommandEnvelope(
                AssistantCommandSchema.V1, AssistantCommandName.CreateTodo,
                new CreateTodoArgumentsV1(suggestion.Title, suggestion.Notes, time, null, null, suggestion.Priority), [], []),
            _ => throw new InvalidOperationException("时间建议已失效，请重新发起请求。")
        };
        try
        {
            var result = commandPipeline.SubmitParsed($"{action.SourceText}（选择 {selected}）", envelope);
            if (result.State != AssistantCommandPipelineState.Succeeded)
            {
                data.SetActionStatus(action.Id, "failed", result.Code);
                return new($"已选择时间，但本地命令未执行（{result.Code}），未创建任何事项。", null, true);
            }
            data.SetActionStatus(action.Id, "confirmed");
            return new(PipelineSuccessText(envelope, result), null, false, true);
        }
        catch (Exception exception) when (exception is AssistantCommandContractException or InvalidOperationException or ArgumentException)
        {
            data.SetActionStatus(action.Id, "failed", exception.Message);
            return new($"已选择时间，但本地校验未通过：{exception.Message}。未创建任何事项。", null, true);
        }
    }

    AssistantConversationResult PreparePipelineConfirmation(
        ChatSession session,
        string sourceText,
        AssistantCommandEnvelope envelope,
        AssistantCommandPipelineResult result,
        AssistantAction? previousAction)
    {
        var bridge = new PipelineConfirmationAction(
            "pipeline_confirmation_v1",
            result.ConfirmationId!,
            AssistantCommandEnvelopeJson.CommandName(envelope.Command));
        var action = data.SaveAction(
            session.Id,
            sourceText,
            JsonSerializer.Serialize(bridge),
            "awaiting_confirmation",
            null,
            previousAction?.Id);
        return new AssistantConversationResult(PipelineConfirmationText(envelope, result), action, false);
    }

    static string PipelineConfirmationText(AssistantCommandEnvelope envelope, AssistantCommandPipelineResult result)
    {
        var command = AssistantCommandEnvelopeJson.CommandName(envelope.Command);
        var targetText = result.ItemIds.Count == 0 ? "" : $"\n目标：{string.Join("、", result.ItemIds)}";
        return $"已识别到需要确认的操作：{command}。\n本地安全策略要求确认后才会执行；确认卡 15 分钟内有效，目标发生变化会自动失效。{targetText}";
    }

    static string ConfirmationFailureText(string code) => code switch
    {
        "confirmation_expired" => "确认已过期，请重新发起操作",
        "target_version_changed" => "目标事项已变化，请查看最新内容后重新确认",
        "command_hash_changed" or "command_schema_stale" => "确认内容已失效，请重新发起操作",
        "idempotent_replay" => "该操作已执行过",
        _ => $"本地安全校验未通过（{code}）"
    };

    static string PipelineSuccessText(AssistantCommandEnvelope envelope, AssistantCommandPipelineResult result) =>
        envelope.Arguments switch
        {
            CreateReminderArgumentsV1 { Title: { } title, Remind: { } remind } =>
                $"已设置提醒：{title}（{FormatTime(remind)}）",
            CreateTodoArgumentsV1 { Title: { } title, Due: { } due } =>
                $"已添加待办：{title}（截止：{FormatTime(due)}）",
            CreateTodoArgumentsV1 { Title: { } title } => $"已添加待办：{title}",
            CreateEventArgumentsV1 { Title: { } title, Start: { } start } =>
                $"已添加日程：{title}（{FormatTime(start)}）",
            CreateRecurringTaskArgumentsV1 { Title: { } title } => $"已添加重复事项：{title}",
            _ => "已完成操作。"
        };

    static string FormatTime(AssistantTimeExpressionV1 time)
    {
        if (time.LocalDate is { } date && time.LocalTime is { } clock)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var label = date == today ? "今天" : date == today.AddDays(1) ? "明天" : $"{date:yyyy年M月d日}";
            return $"{label} {clock:HH:mm}";
        }

        return time.OriginalText?.Trim() ?? "已安排";
    }

    static string PipelineConfirmedText(string command, AssistantCommandPipelineResult result) =>
        $"已确认并执行：{command}。";

    static string PipelineClarificationText(AssistantCommandEnvelope envelope, string reason)
    {
        var details = envelope.MissingFields.Concat(envelope.AmbiguityReasons).Distinct().ToArray();
        var suffix = details.Length == 0 ? "请补充明确的事项和时间。" : $"请补充：{string.Join("、", details)}。";
        return $"为了避免误操作，Tuux 暂未写入任何事项（{reason}）。{suffix}";
    }

    static bool TryReadPipelineConfirmation(string json, out PipelineConfirmationAction confirmation)
    {
        try
        {
            confirmation = JsonSerializer.Deserialize<PipelineConfirmationAction>(json)!;
            return confirmation is not null && confirmation.Kind == "pipeline_confirmation_v1" &&
                !string.IsNullOrWhiteSpace(confirmation.ConfirmationId) &&
                !string.IsNullOrWhiteSpace(confirmation.Command);
        }
        catch (JsonException)
        {
            confirmation = default!;
            return false;
        }
    }

    static bool TryReadDecompositionDraft(string json, out DecompositionDraftAction draft)
    {
        try
        {
            draft = JsonSerializer.Deserialize<DecompositionDraftAction>(json)!;
            return draft is not null && draft.Kind == "decomposition_draft_v1" &&
                !string.IsNullOrWhiteSpace(draft.DraftId) && !string.IsNullOrWhiteSpace(draft.Goal);
        }
        catch (JsonException)
        {
            draft = default!;
            return false;
        }
    }

    static bool TryReadTimeSuggestion(string json, out TimeSuggestionAction suggestion)
    {
        try
        {
            suggestion = JsonSerializer.Deserialize<TimeSuggestionAction>(json)!;
            return suggestion is not null && suggestion.Kind == "time_suggestion_v1" &&
                suggestion.Command is "create_reminder" or "create_todo" &&
                !string.IsNullOrWhiteSpace(suggestion.Title) && suggestion.Options is { Count: 3 };
        }
        catch (JsonException)
        {
            suggestion = default!;
            return false;
        }
    }

    static string DecompositionPreviewText(string goal, IReadOnlyList<DraftProposal> proposals)
    {
        var lines = proposals.Select((proposal, index) =>
        {
            var details = new[]
                { proposal.EstimatedMinutes is int minutes ? $"约 {minutes} 分钟" : null, proposal.Category }
                .Where(value => !string.IsNullOrWhiteSpace(value));
            return $"{index + 1}. {proposal.Title}" + (details.Any() ? $"（{string.Join("，", details)}）" : "");
        });
        return $"已生成“{goal}”的任务拆解草稿：\n{string.Join("\n", lines)}\n\n确认后会一次性加入待办；取消则不会创建任何事项。";
    }

    sealed record PipelineConfirmationAction(string Kind, string ConfirmationId, string Command);
    sealed record DecompositionDraftAction(string Kind, string DraftId, string Goal);
    sealed record TimeSuggestionAction(string Kind, string Command, string Title, string? Notes, AssistantPriorityV1? Priority, IReadOnlyList<DateTime> Options);

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
        RecurrenceKind.OfficialWorkdays => "\u6CD5\u5B9A\u5DE5\u4F5C\u65E5",
        RecurrenceKind.StatutoryHolidays => "\u6CD5\u5B9A\u8282\u5047\u65E5",
        RecurrenceKind.Weekly => "\u6BCF\u5468" + string.Join("\u3001", weekdays.OrderBy(day => day).Select(DayName)),
        _ => throw new ArgumentOutOfRangeException(nameof(recurrence))
    };

    AssistantConversationResult HandleModificationClarification(ChatSession session, string input, AssistantAction? supersededDraft)
    {
        if (supersededDraft is not null)
        {
            // This message starts a distinct operation, rather than answering the
            // previous creation clarification. Do not let that draft trap later input.
            data.SetActionStatus(supersededDraft.Id, "superseded");
        }

        var mentionsHoliday = input.Contains("节假日", StringComparison.Ordinal) || input.Contains("假日", StringComparison.Ordinal);
        if (mentionsHoliday)
        {
            return StartHolidayReminderBatch(session, input, supersededDraft);
        }

        var reply = "\u6211\u7406\u89e3\u4f60\u60f3\u4fee\u6539\u5df2\u6709\u63d0\u9192\u3002\u5f53\u524d\u6279\u91cf\u4fee\u6539\u9700\u8981\u5148\u9009\u62e9\u5177\u4f53\u63d0\u9192\uff1b\u4e3a\u4e86\u907f\u514d\u8bef\u6539\uff0c\u672a\u4fee\u6539\u4efb\u4f55\u4e8b\u9879\u3002\u8bf7\u6307\u5b9a\u63d0\u9192\u540d\u79f0\u548c\u660e\u786e\u65f6\u95f4\uff0c\u4f8b\u5982\uff1a\u628a\u201c\u559d\u6c34\u201d\u63d0\u9192\u6539\u5230\u5468\u516d\u65e5 13:00\u3002";
        if (supersededDraft is not null)
            reply = "\u5DF2\u505C\u6B62\u4E0A\u4E00\u6761\u5F85\u8865\u5145\u7684\u521B\u5EFA\u8BF7\u6C42\u3002\n\n" + reply;
        return new AssistantConversationResult(reply, null, false);
    }

    AssistantConversationResult PrepareOfficialSleepScheduleConfirmation(
        ChatSession session,
        string sourceText,
        OfficialSleepSchedule schedule)
    {
        var command = new OfficialSleepScheduleCommand(
            OfficialSleepScheduleCommandName,
            schedule.OfficialWorkdayTime.ToString("HH:mm"),
            schedule.StatutoryHolidayTime.ToString("HH:mm"));
        var action = data.SaveAction(
            session.Id,
            sourceText,
            JsonSerializer.Serialize(command),
            "awaiting_confirmation");
        return new AssistantConversationResult(
            $"已识别为新的睡觉提醒计划：法定工作日 {command.OfficialWorkdayTime}，法定节假日 {command.StatutoryHolidayTime}。将依据本地已接入的 2025–2026 年国务院节假日与调休安排创建两条周期提醒；不会修改现有提醒。请确认执行。",
            action,
            false);
    }

    static bool TryParseOfficialSleepSchedule(string input, out OfficialSleepSchedule schedule)
    {
        if (!input.Contains("睡觉", StringComparison.Ordinal) ||
            !input.Contains("工作日", StringComparison.Ordinal) ||
            !input.Contains("节假日", StringComparison.Ordinal) ||
            !TryExtractScheduleTime(input, "工作日", out var officialWorkdayTime) ||
            !TryExtractScheduleTime(input, "节假日", out var statutoryHolidayTime))
        {
            schedule = default!;
            return false;
        }

        schedule = new OfficialSleepSchedule(officialWorkdayTime, statutoryHolidayTime);
        return true;
    }

    static bool TryExtractScheduleTime(string input, string category, out TimeOnly time)
    {
        var categoryMatch = Regex.Match(input, $@"{Regex.Escape(category)}(?<segment>[^，,。；;]{{0,40}})");
        if (!categoryMatch.Success)
        {
            time = default;
            return false;
        }

        var timeMatch = Regex.Match(
            categoryMatch.Groups["segment"].Value.Replace('：', ':'),
            @"(?<period>凌晨|早上|上午|中午|下午|晚上)?\s*(?<hour>\d{1,2})\s*(?::|点)\s*(?<minute>\d{1,2})?");
        if (!timeMatch.Success || !int.TryParse(timeMatch.Groups["hour"].Value, out var hour))
        {
            time = default;
            return false;
        }

        var minute = timeMatch.Groups["minute"].Success && int.TryParse(timeMatch.Groups["minute"].Value, out var parsedMinute)
            ? parsedMinute
            : 0;
        var period = timeMatch.Groups["period"].Value;
        if (minute is < 0 or > 59 || hour is < 0 or > 23 || (hour == 12 && string.IsNullOrEmpty(period)))
        {
            time = default;
            return false;
        }

        if (period == "凌晨")
        {
            if (hour == 12) hour = 0;
            else if (hour > 11) { time = default; return false; }
        }
        else if (period is "下午" or "晚上")
        {
            if (hour is > 0 and < 12) hour += 12;
        }
        else if (period is "早上" or "上午")
        {
            if (hour > 12) { time = default; return false; }
        }

        time = new TimeOnly(hour, minute);
        return true;
    }

    AssistantConversationResult StartHolidayReminderBatch(ChatSession session, string input, AssistantAction? supersededDraft)
    {
        if (TryParseExplicitClock(input, out var reminderTime))
            return PrepareHolidayReminderBatchConfirmation(session, input, reminderTime, supersededDraft);

        _ = data.SaveAction(
            session.Id,
            input,
            JsonSerializer.Serialize(new HolidayReminderBatchDraft(HolidayReminderBatchCommandName)),
            "holiday_batch_time_pending",
            null,
            null);
        var prefix = supersededDraft is null ? "" : "已停止上一条待补充的创建请求。\n\n";
        return new AssistantConversationResult(
            prefix + "已接入 2025–2026 年的国务院法定节假日与调休上班日。将批量修改多个已有提醒；“1点钟”可能是 01:00 或 13:00，因此尚未修改。请只回复明确的 24 小时时间，例如 `01:00` 或 `13:00`。",
            null,
            false);
    }

    AssistantConversationResult ContinueHolidayReminderBatch(ChatSession session, string input, AssistantAction pending)
    {
        if (!TryParseExplicitClock(input, out var reminderTime))
            return new AssistantConversationResult("请使用明确的 24 小时时间，例如 `01:00` 或 `13:00`；在确认前不会修改任何提醒。", null, false);

        return PrepareHolidayReminderBatchConfirmation(session, pending.SourceText, reminderTime, pending);
    }

    AssistantConversationResult PrepareHolidayReminderBatchConfirmation(
        ChatSession session,
        string sourceText,
        TimeOnly reminderTime,
        AssistantAction? previousAction)
    {
        var targetSet = data.StatutoryHolidayReminderTargets(holidays);
        if (targetSet.Targets.Count == 0)
        {
            if (previousAction is not null) data.SetActionStatus(previousAction.Id, "cancelled");
            var recurringOnly = targetSet.ExcludedRecurringOccurrences > 0
                ? $"检测到 {targetSet.ExcludedRecurringOccurrences} 个周期提醒实例；周期提醒必须逐次处理，本次没有修改。"
                : "当前没有安排在 2025–2026 法定节假日的单次提醒，未修改任何事项。";
            return new AssistantConversationResult(recurringOnly, null, false);
        }

        var batch = new HolidayReminderBatchCommand(
            HolidayReminderBatchCommandName,
            reminderTime.ToString("HH:mm"),
            targetSet.Targets);
        var action = data.SaveAction(
            session.Id,
            sourceText,
            JsonSerializer.Serialize(batch),
            "awaiting_confirmation",
            null,
            previousAction?.Status == "holiday_batch_time_pending" ? previousAction.Id : null);
        var recurringNote = targetSet.ExcludedRecurringOccurrences == 0
            ? ""
            : $"\n另有 {targetSet.ExcludedRecurringOccurrences} 个周期提醒实例未纳入此次修改。";
        return new AssistantConversationResult(
            $"已找到 {targetSet.Targets.Count} 条法定节假日的单次提醒。将保留每条提醒的原日期、标题和其他信息，只把提醒时间改为 {batch.ReminderTime}，并清除旧的已提醒标记。请确认执行。{recurringNote}",
            action,
            false);
    }

    static bool LastUserRequestedHolidayBatch(IReadOnlyList<ChatMessage> history)
    {
        foreach (var message in history.Reverse())
        {
            if (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)) continue;
            if (message.Content.Contains("节假日", StringComparison.Ordinal) || message.Content.Contains("假日", StringComparison.Ordinal))
                return true;

            // A prior exact clock is a continuation of the same batch request.
            // Any other user input begins a new conversation turn instead.
            if (!TryParseExplicitClock(message.Content, out _)) return false;
        }

        return false;
    }

    static bool TryParseExplicitClock(string input, out TimeOnly reminderTime)
    {
        var match = Regex.Match(input.Replace('：', ':'), @"(?<!\d)(?<hour>[01]?\d|2[0-3]):(?<minute>[0-5]\d)(?!\d)");
        if (match.Success && int.TryParse(match.Groups["hour"].Value, out var hour) && int.TryParse(match.Groups["minute"].Value, out var minute))
        {
            reminderTime = new TimeOnly(hour, minute);
            return true;
        }

        reminderTime = default;
        return false;
    }

    static bool TryReadOfficialSleepSchedule(string intentJson, out OfficialSleepScheduleCommand schedule)
    {
        try
        {
            schedule = JsonSerializer.Deserialize<OfficialSleepScheduleCommand>(intentJson)!;
            return schedule is not null && schedule.Command == OfficialSleepScheduleCommandName &&
                TimeOnly.TryParseExact(schedule.OfficialWorkdayTime, "HH:mm", out _) &&
                TimeOnly.TryParseExact(schedule.StatutoryHolidayTime, "HH:mm", out _);
        }
        catch (JsonException)
        {
            schedule = default!;
            return false;
        }
    }

    const string OfficialSleepScheduleCommandName = "create_official_sleep_schedule";
    sealed record OfficialSleepSchedule(TimeOnly OfficialWorkdayTime, TimeOnly StatutoryHolidayTime);
    sealed record OfficialSleepScheduleCommand(
        string Command,
        string OfficialWorkdayTime,
        string StatutoryHolidayTime);

    static bool TryReadHolidayReminderBatch(string intentJson, out HolidayReminderBatchCommand batch)
    {
        try
        {
            batch = JsonSerializer.Deserialize<HolidayReminderBatchCommand>(intentJson)!;
            return batch is not null && batch.Command == HolidayReminderBatchCommandName &&
                TimeOnly.TryParseExact(batch.ReminderTime, "HH:mm", out _);
        }
        catch (JsonException)
        {
            batch = default!;
            return false;
        }
    }

    const string HolidayReminderBatchCommandName = "reschedule_holiday_reminders";
    sealed record HolidayReminderBatchDraft(string Command);
    sealed record HolidayReminderBatchCommand(string Command, string ReminderTime, IReadOnlyList<HolidayReminderTarget> Targets);

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