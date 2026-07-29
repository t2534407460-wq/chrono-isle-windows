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
    readonly AssistantCommandPipeline commandPipeline;
    readonly DraftStore drafts;
    readonly IConversationPlanner conversationPlanner;
    readonly IOperationArgumentParser operationParser;
    readonly AssistantPlanPipeline planPipeline;

    public AssistantActionService(
        LifeDataService data,
        ChinaStatutoryHolidayCalendar holidays,
        ConversationRouter router,
        LocalAgendaQueryService localQueries,
        IChatCompletionClient chat,
        AssistantCommandPipeline? pipeline = null,
        LifePreferencesService? preferences = null,
        IConversationPlanner? planner = null,
        IOperationArgumentParser? argumentParser = null,
        AssistantPlanPipeline? assistantPlanPipeline = null)
    {
        this.data = data;
        this.holidays = holidays;
        this.router = router;
        this.localQueries = localQueries;
        this.chat = chat;
        this.preferences = preferences;
        commandPipeline = pipeline ?? new AssistantCommandPipeline(data.DatabasePath);
        var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(data.DatabasePath);
        new ProductivitySchemaInitializer(runtime.WriteQueue).Initialize();
        conversationPlanner = planner ?? new ConversationPlannerV2(chat);
        operationParser = argumentParser ?? new OperationArgumentParserV2(chat);
        planPipeline = assistantPlanPipeline ?? new AssistantPlanPipeline(data.DatabasePath, commandPipeline);
        drafts = new DraftStore(runtime.WriteQueue, runtime.ConnectionFactory);
    }

    // Source-compatibility overload only. The retired parser is ignored and every new request
    // enters the V2 planner/operation pipeline.
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
        var pendingConfirmation = data.ActiveConfirmation(session.Id);
        if (pendingConfirmation is not null && IsConfirmationReply(input))
        {
            var execution = Confirm(pendingConfirmation.Id);
            return ApplyPersona(new(execution.Message, null, !execution.Succeeded, execution.Succeeded));
        }
        if (pendingConfirmation is not null && IsCancellationReply(input))
        {
            Cancel(pendingConfirmation.Id);
            return ApplyPersona(new("已取消整个计划，没有执行任何写操作。", null, false));
        }

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
        if (activeDraft is not null && TryReadArgumentClarification(activeDraft.IntentJson, out var argumentClarification))
            return ApplyPersona(await ContinueArgumentClarificationAsync(
                provider, session, input, activeDraft, argumentClarification, cancellationToken));
        if (activeDraft is not null && TryReadPlanClarification(activeDraft.IntentJson, out var clarification))
        {
            var continued = await ContinuePlanClarificationAsync(provider, session, input, activeDraft, clarification, cancellationToken);
            if (continued is not null) return ApplyPersona(continued);
        }

        if (TryParseExplicitSingleReminder(input, DateTime.Now, out var localReminder))
            return ApplyPersona(SubmitV2Commands(session, input, [localReminder], [], activeDraft));

        var result = await HandleV2Async(provider, session, history, input, activeDraft, onDelta, cancellationToken);
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
    async Task<AssistantConversationResult> HandleV2Async(
        ProviderSettings provider,
        ChatSession session,
        IReadOnlyList<ChatMessage> history,
        string input,
        AssistantAction? activeDraft,
        Action<string>? onDelta,
        CancellationToken cancellationToken)
    {
        ConversationPlanResultV2 planned;
        try
        {
            planned = await conversationPlanner.PlanAsync(provider, AssistantTurnContextV2.Empty, input, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new($"意图规划失败：{exception.Message}。没有执行任何写操作。", null, true);
        }
        if (!planned.IsValid)
            return new($"我无法安全拆解这条请求（{planned.ErrorCode}）。请把要查询、创建或修改的事项说得更明确；如果操作很多，请拆成两条消息。", null, false);

        var plan = planned.Plan!;
        if (plan.Segments.Count == 1 && plan.Segments[0].Kind == ConversationSegmentKindV2.Chat)
            return await HandleGeneralChatAsync(provider, history, input, onDelta, cancellationToken);

        var chatReplies = new List<string>();
        var queryReplies = new List<string>();
        var writeReplies = new List<string>();
        var candidateSets = new Dictionary<string, IReadOnlyList<AssistantPlanCandidateBindingV2>>(StringComparer.Ordinal);
        foreach (var segment in plan.Segments.Where(value => value.Kind == ConversationSegmentKindV2.Command && RequiresCandidate(value.Operation)))
        {
            var candidates = planPipeline.FindCandidateBindings(segment.Evidence, segment.SegmentRef);
            if (candidates.Count == 0)
            {
                writeReplies.Add($"未找到与“{segment.Evidence}”唯一对应的本地事项；未执行任何写操作。请补充完整事项名称。");
                continue;
            }
            if (candidates.Count > 1)
            {
                if (plan.WriteOperationCount > 1)
                {
                    writeReplies.Add("这条消息同时包含多个写操作，其中至少一个目标有重名。为避免部分执行，请先单独选择目标，再重新发送其余操作。");
                    writeReplies.Add(CandidateChoiceText(candidates));
                    continue;
                }
                var bridge = new PlanClarificationActionV2(
                    "assistant_plan_clarification_v2", segment.Operation, segment.Evidence, candidates);
                data.SaveAction(session.Id, input, JsonSerializer.Serialize(bridge), "clarifying", null, activeDraft?.Id);
                writeReplies.Add("找到多个可能的目标，暂未执行。请回复下面的 candidateRef：\n" + CandidateChoiceText(candidates));
                continue;
            }
            candidateSets[segment.SegmentRef] = candidates;
        }
        if (writeReplies.Count > 0)
            return new(CombineV2Replies(chatReplies, queryReplies, writeReplies), null, false);

        var commands = new List<AssistantCommandEnvelope>();
        var bindings = new List<AssistantPlanCandidateBindingV2>();
        foreach (var segment in plan.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (segment.Kind)
            {
                case ConversationSegmentKindV2.Chat:
                {
                    var chatResult = await HandleGeneralChatAsync(provider, history, segment.Evidence, null, cancellationToken);
                    chatReplies.Add(chatResult.Reply.Trim());
                    break;
                }
                case ConversationSegmentKindV2.Query:
                {
                    var route = router.Decide(segment.Evidence, null, DateTime.Now);
                    if (segment.Evidence.Contains("长期", StringComparison.Ordinal) &&
                        !HasExplicitQueryRange(segment.Evidence))
                        queryReplies.Add(LongTermQueryText(data.LongTermItems()));
                    else if (route.Kind == ConversationRouteKind.LocalQuery && route.Query is not null)
                        queryReplies.Add((await HandleLocalQueryAsync(provider, history, segment.Evidence, route.Query)).Reply.Trim());
                    else
                        queryReplies.Add($"无法安全确定“{segment.Evidence}”的查询范围，请补充日期或范围。");
                    break;
                }
                case ConversationSegmentKindV2.Command:
                {
                    var segmentBindings = candidateSets.GetValueOrDefault(segment.SegmentRef) ?? [];
                    var context = new AssistantTurnContextV2(
                        segment.Operation,
                        segment.Evidence,
                        null,
                        segmentBindings.Select(ToPublicCandidate).ToArray(),
                        DateTimeOffset.Now.AddMinutes(15));
                    var parsed = await operationParser.ParseAsync(provider, segment, context, cancellationToken);
                    if (!parsed.IsValid)
                    {
                        writeReplies.Add($"“{segment.Evidence}”的参数不完整或无效，请补充后重试（argument_invalid）。");
                        break;
                    }
                    var envelope = parsed.Envelope!;
                    if (segmentBindings.Count == 1)
                    {
                        try { envelope = AttachCandidate(envelope, segmentBindings[0]); }
                        catch (InvalidOperationException)
                        {
                            writeReplies.Add("模型返回了不属于本轮候选集的目标引用；未执行任何写操作。");
                            break;
                        }
                        bindings.Add(segmentBindings[0]);
                    }
                    commands.Add(envelope);
                    break;
                }
            }
        }

        if (writeReplies.Count > 0)
            return new(CombineV2Replies(chatReplies, queryReplies, writeReplies), null, false);
        if (commands.Count == 0)
            return new(CombineV2Replies(chatReplies, queryReplies, ["本轮没有可执行的写操作。"]), null, false);

        if (commands.Count == 1 && commands[0].Command == AssistantCommandName.DecomposeGoal)
        {
            var draft = PrepareDecompositionDraft(session, input, (DecomposeGoalArgumentsV1)commands[0].Arguments, activeDraft);
            return draft with { Reply = CombineV2Replies(chatReplies, queryReplies, [draft.Reply]) };
        }
        if (commands.Any(command => command.Command == AssistantCommandName.DecomposeGoal))
            return new(CombineV2Replies(chatReplies, queryReplies,
                ["目标拆解需要单独确认草稿，不能与其他写操作放在同一原子计划中；请拆成两条消息。"]), null, false);

        var submitted = SubmitV2Commands(session, input, commands, bindings, activeDraft);
        return submitted with { Reply = CombineV2Replies(chatReplies, queryReplies, [submitted.Reply]) };
    }

    AssistantConversationResult SubmitV2Commands(
        ChatSession session,
        string sourceText,
        IReadOnlyList<AssistantCommandEnvelope> commands,
        IReadOnlyList<AssistantPlanCandidateBindingV2> bindings,
        AssistantAction? previousAction)
    {
        try
        {
            var result = planPipeline.SubmitPlan(sourceText, commands, bindings);
            return result.State switch
            {
                AssistantPlanPipelineState.Succeeded => new(PlanSucceededText(result, commands), null, false, true),
                AssistantPlanPipelineState.AwaitingConfirmation => PreparePlanConfirmation(session, sourceText, result, previousAction),
                AssistantPlanPipelineState.ClarificationRequired => PrepareArgumentClarification(
                    session, sourceText, commands, bindings, result.Code, previousAction),
                _ => new($"计划执行失败（{result.Code}），没有写入任何事项。", null, true)
            };
        }
        catch (Exception exception) when (exception is AssistantCommandContractException or InvalidOperationException or ArgumentException)
        {
            return new($"计划未通过本地安全校验：{exception.Message}。没有写入任何事项。", null, true);
        }
    }

    AssistantConversationResult PrepareArgumentClarification(
        ChatSession session,
        string sourceText,
        IReadOnlyList<AssistantCommandEnvelope> commands,
        IReadOnlyList<AssistantPlanCandidateBindingV2> bindings,
        string code,
        AssistantAction? previousAction)
    {
        if (commands.Count != 1)
            return new(PlanClarificationText(code) + " 多步骤计划尚未部分保存，请补充后重新发送整条请求。", null, false);
        var command = commands[0];
        var missing = command.MissingFields.Concat(command.AmbiguityReasons).Distinct(StringComparer.Ordinal).ToArray();
        var bridge = new PlanArgumentClarificationActionV2(
            "assistant_argument_clarification_v2",
            OperationFor(command.Command),
            sourceText,
            missing,
            bindings,
            DateTimeOffset.Now.AddMinutes(15));
        var action = data.SaveAction(session.Id, sourceText, JsonSerializer.Serialize(bridge),
            "clarifying", null, previousAction?.Id);
        var details = missing.Length == 0 ? "请补充明确的事项、日期或时间。" : $"请补充：{string.Join("、", missing)}。";
        return new($"{PlanClarificationText(code)} {details}", action, false);
    }

    async Task<AssistantConversationResult> ContinueArgumentClarificationAsync(
        ProviderSettings provider,
        ChatSession session,
        string input,
        AssistantAction action,
        PlanArgumentClarificationActionV2 clarification,
        CancellationToken cancellationToken)
    {
        if (IsCancellationReply(input))
        {
            data.SetActionStatus(action.Id, "cancelled");
            return new("已取消待补充计划，没有执行任何写操作。", null, false);
        }
        if (DateTimeOffset.Now >= clarification.ExpiresAt)
        {
            data.SetActionStatus(action.Id, "stale", "clarification_expired");
            return new("待补充计划已过期，请重新发起操作。", null, false);
        }
        var evidence = clarification.Evidence + "；补充信息：" + input.Trim();
        var segment = new ConversationSegmentV2("followup", ConversationSegmentKindV2.Command,
            clarification.Operation, evidence, []);
        var context = new AssistantTurnContextV2(
            clarification.Operation,
            clarification.Evidence,
            clarification.MissingFields,
            clarification.Candidates.Select(ToPublicCandidate).ToArray(),
            clarification.ExpiresAt);
        var parsed = await operationParser.ParseAsync(provider, segment, context, cancellationToken);
        if (!parsed.IsValid)
            return new("补充内容仍无法通过参数校验，请写明具体事项、日期和时间；回复“取消”可放弃。", action, false);
        var envelope = parsed.Envelope!;
        if (clarification.Candidates.Count == 1)
        {
            try { envelope = AttachCandidate(envelope, clarification.Candidates[0]); }
            catch (InvalidOperationException) { return new("候选目标已经失效，请重新发起操作。", null, false); }
        }
        data.SetActionStatus(action.Id, "superseded");
        return SubmitV2Commands(session, evidence, [envelope], clarification.Candidates, action);
    }
    AssistantConversationResult PreparePlanConfirmation(
        ChatSession session,
        string sourceText,
        AssistantPlanPipelineResultV2 result,
        AssistantAction? previousAction)
    {
        var bridge = new PlanConfirmationActionV2(
            "assistant_plan_confirmation_v2", result.ConfirmationId!, result.PlanId, result.Preview);
        var action = data.SaveAction(session.Id, sourceText, JsonSerializer.Serialize(bridge),
            "awaiting_confirmation", null, previousAction?.Id);
        var lines = result.Preview.Select(step => $"{step.StepIndex + 1}. {step.Description}");
        var text = $"以下计划尚未执行：\n{string.Join("\n", lines)}\n\n确认后将整体执行；任一步失败都会全部回滚。确认卡 15 分钟内有效。";
        var pendingPlan = new AssistantPendingPlan(result.PlanId, result.Code,
            result.Preview.Select(step => new AssistantPendingPlanStep(
                step.StepIndex, step.Operation, step.Description, step.Targets)).ToArray(), "等待确认");
        return new(text, action, false, false, pendingPlan);
    }

    async Task<AssistantConversationResult?> ContinuePlanClarificationAsync(
        ProviderSettings provider,
        ChatSession session,
        string input,
        AssistantAction action,
        PlanClarificationActionV2 clarification,
        CancellationToken cancellationToken)
    {
        if (IsCancellationReply(input))
        {
            data.SetActionStatus(action.Id, "cancelled");
            return new("已取消待补充操作，没有修改任何事项。", null, false);
        }
        var selected = clarification.Candidates.Where(candidate =>
            string.Equals(candidate.CandidateRef, input.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Title, input.Trim().Trim('“', '”', '"'), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length != 1)
            return new("请回复一个明确的 candidateRef；如要放弃，请回复“取消”。\n" + CandidateChoiceText(clarification.Candidates), null, false);

        var evidence = clarification.Evidence + "；目标选择为 " + input.Trim();
        var segment = new ConversationSegmentV2("followup", ConversationSegmentKindV2.Command,
            clarification.Operation, evidence, []);
        var context = new AssistantTurnContextV2(clarification.Operation, clarification.Evidence, null,
            [ToPublicCandidate(selected[0])], DateTimeOffset.Now.AddMinutes(15));
        var parsed = await operationParser.ParseAsync(provider, segment, context, cancellationToken);
        if (!parsed.IsValid)
            return new("目标已选定，但其他参数仍不完整，请重新发起并写明要修改的值。", null, false);
        AssistantCommandEnvelope envelope;
        try { envelope = AttachCandidate(parsed.Envelope!, selected[0]); }
        catch (InvalidOperationException)
        {
            return new("候选引用校验失败，未执行任何写操作。", null, true);
        }
        data.SetActionStatus(action.Id, "superseded");
        return SubmitV2Commands(session, evidence, [envelope], [selected[0]], action);
    }

    static AssistantCandidateRefV2 ToPublicCandidate(AssistantPlanCandidateBindingV2 candidate) =>
        new(candidate.CandidateRef, candidate.Title, candidate.Kind.ToString(), candidate.TimeText);

    static AssistantCommandEnvelope AttachCandidate(
        AssistantCommandEnvelope envelope,
        AssistantPlanCandidateBindingV2 binding)
    {
        var current = envelope.Arguments switch
        {
            UpdateTodoArgumentsV1 value => value.Target,
            CompleteTodoArgumentsV1 value => value.Target,
            DeleteTodoArgumentsV1 value => value.Target,
            RescheduleItemArgumentsV1 value => value.Target,
            _ => null
        };
        if (current?.CandidateRef is { Length: > 0 } reference &&
            !string.Equals(reference, binding.CandidateRef, StringComparison.Ordinal))
            throw new InvalidOperationException("candidateRef 不属于本轮候选集。");
        var target = new AssistantTargetSelectorV1(binding.Title, binding.Kind, current?.TimeHint, binding.CandidateRef);
        var arguments = envelope.Arguments switch
        {
            UpdateTodoArgumentsV1 value => value with { Target = target },
            CompleteTodoArgumentsV1 value => value with { Target = target },
            DeleteTodoArgumentsV1 value => value with { Target = target },
            RescheduleItemArgumentsV1 value => value with { Target = target },
            _ => envelope.Arguments
        };
        return new AssistantCommandEnvelope(envelope.SchemaVersion, envelope.Command, arguments,
            envelope.MissingFields, envelope.AmbiguityReasons);
    }

    static ConversationOperationV2 OperationFor(AssistantCommandName command) => command switch
    {
        AssistantCommandName.CreateTodo => ConversationOperationV2.CreateTodo,
        AssistantCommandName.CreateReminder => ConversationOperationV2.CreateReminder,
        AssistantCommandName.CreateEvent => ConversationOperationV2.CreateEvent,
        AssistantCommandName.CreateLongTermItem => ConversationOperationV2.CreateLongTermItem,
        AssistantCommandName.CreateRecurringTask => ConversationOperationV2.CreateRecurringTask,
        AssistantCommandName.UpdateTodo => ConversationOperationV2.UpdateTodo,
        AssistantCommandName.CompleteTodo => ConversationOperationV2.CompleteTodo,
        AssistantCommandName.DeleteTodo => ConversationOperationV2.DeleteTodo,
        AssistantCommandName.RescheduleItem => ConversationOperationV2.RescheduleItem,
        AssistantCommandName.DecomposeGoal => ConversationOperationV2.DecomposeGoal,
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };
    static bool RequiresCandidate(ConversationOperationV2 operation) => operation is
        ConversationOperationV2.UpdateTodo or ConversationOperationV2.CompleteTodo or
        ConversationOperationV2.DeleteTodo or ConversationOperationV2.RescheduleItem;

    static bool IsConfirmationReply(string input) => input.Trim() is "确认" or "确认全部" or "执行" or "全部执行";

    static bool IsCancellationReply(string input) => input.Trim() is "取消" or "算了" or "不用了" or "停止";

    static bool HasExplicitQueryRange(string evidence) => Regex.IsMatch(
        evidence,
        @"今天|明天|后天|本周|这周|下周|本月|这个月|下个月|\d{4}\s*[-年/]\s*\d{1,2}",
        RegexOptions.CultureInvariant);

    static string LongTermQueryText(IReadOnlyList<ManagedLifeItem> items)
    {
        if (items.Count == 0) return "## 长期事项\n- 暂无长期事项。";
        return "## 长期事项\n" + string.Join("\n", items.Select(item =>
        {
            var schedule = item.ScheduledAt is { } value ? value.ToString("yyyy-MM-dd HH:mm") : "未设日期";
            var status = item.IsCompleted ? "已完成" : "进行中";
            return $"- [{status}] {item.Title} · {schedule}";
        }));
    }
    static string CandidateChoiceText(IReadOnlyList<AssistantPlanCandidateBindingV2> candidates) =>
        string.Join("\n", candidates.Select(candidate =>
            $"- {candidate.CandidateRef}：{candidate.Title}（{candidate.Kind}{(string.IsNullOrWhiteSpace(candidate.TimeText) ? "" : $"，{candidate.TimeText}")}）"));

    static string CombineV2Replies(
        IReadOnlyList<string> chat,
        IReadOnlyList<string> queries,
        IReadOnlyList<string> writes) =>
        string.Join("\n\n", chat.Concat(queries).Concat(writes).Where(value => !string.IsNullOrWhiteSpace(value)));

    static string PlanSucceededText(
        AssistantPlanPipelineResultV2 result,
        IReadOnlyList<AssistantCommandEnvelope>? commands = null)
    {
        if (commands is { Count: 1 })
        {
            return commands[0].Arguments switch
            {
                CreateReminderArgumentsV1 { Title: { } title, Remind: { } remind } =>
                    $"已设置提醒：{title}（{FormatTime(remind)}）",
                CreateTodoArgumentsV1 { Title: { } title, Due: { } due } =>
                    $"已添加待办：{title}（截止：{FormatTime(due)}）",
                CreateTodoArgumentsV1 { Title: { } title } => $"已添加待办：{title}",
                CreateEventArgumentsV1 { Title: { } title, Start: { } start } =>
                    $"已添加日程：{title}（{FormatTime(start)}）",
                CreateLongTermItemArgumentsV1 { Title: { } title } =>
                    $"已添加长期事项：{title}",
                CreateRecurringTaskArgumentsV1 { Title: { } title } => $"已添加重复事项：{title}",
                _ => result.ItemIds.Count == 0 ? "计划已执行。" : $"计划已执行，共影响 {result.ItemIds.Count} 个事项。"
            };
        }
        return result.ItemIds.Count == 0 ? "计划已执行。" : $"计划已执行，共影响 {result.ItemIds.Count} 个事项。";
    }

    static string PlanClarificationText(string code) => code switch
    {
        "target_not_found" => "未找到目标事项，请补充完整名称。",
        "target_ambiguous" => "找到多个候选目标，请先明确选择。",
        "missing_required_fields" => "还缺少执行所需的信息，请补充明确的事项、日期或时间。",
        _ => $"为了避免误操作，计划仍在等待补充（{code}）。"
    };

    static bool TryParseExplicitSingleReminder(string input, DateTime now, out AssistantCommandEnvelope envelope)
    {
        envelope = default!;
        if (input.Contains("每天", StringComparison.Ordinal) || input.Contains("每周", StringComparison.Ordinal) ||
            input.Contains("工作日", StringComparison.Ordinal) || input.Contains("节假日", StringComparison.Ordinal))
            return false;
        string[] relativePatterns =
        [
            @"^\s*(?:请)?(?:帮我)?(?:设置|创建|添加)?\s*(?<relative>\d{1,4}\s*(?:分钟|小时|天)后)\s*的?\s*(?<title>.+?)\s*(?:提醒|闹钟)\s*[。！？!?]?\s*$",
            @"^\s*(?<relative>\d{1,4}\s*(?:分钟|小时|天)后)\s*(?:提醒(?:我)?|叫我|记得)\s*(?<title>.+?)\s*[。！？!?]?\s*$",
            @"^\s*(?:提醒(?:我)?|叫我|记得)\s*(?<relative>\d{1,4}\s*(?:分钟|小时|天)后)\s*(?<title>.+?)\s*[。！？!?]?\s*$"
        ];
        Match? relativeMatch = null;
        foreach (var pattern in relativePatterns)
        {
            relativeMatch = Regex.Match(input, pattern);
            if (relativeMatch.Success) break;
        }
        if (relativeMatch is { Success: true })
        {
            var relativeTitle = relativeMatch.Groups["title"].Value.Trim();
            if (string.IsNullOrWhiteSpace(relativeTitle) || relativeTitle.Length > 200) return false;
            var expression = relativeMatch.Groups["relative"].Value.Trim();
            envelope = new AssistantCommandEnvelope(
                AssistantCommandSchema.V1,
                AssistantCommandName.CreateReminder,
                new CreateReminderArgumentsV1(
                    relativeTitle,
                    null,
                    new AssistantTimeExpressionV1(null, null, expression, null, expression),
                    null,
                    null,
                    null),
                [],
                []);
            return true;
        }


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

        if (TryReadPlanConfirmation(action.IntentJson, out var planConfirmation))
        {
            try
            {
                var result = planPipeline.ConfirmPlan(planConfirmation.ConfirmationId);
                var succeeded = result.State == AssistantPlanPipelineState.Succeeded;
                data.SetActionStatus(actionId, succeeded ? "confirmed" : result.State.ToString().ToLowerInvariant(),
                    succeeded ? null : result.Code);
                return new(succeeded, succeeded
                    ? PlanSucceededText(result)
                    : $"确认未执行：{PlanConfirmationFailureText(result.Code)}。没有修改任何事项。", null);
            }
            catch (Exception exception)
            {
                data.SetActionStatus(actionId, "failed", exception.Message);
                return new(false, $"确认执行失败：{exception.Message}。计划已回滚。", null);
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

        data.SetActionStatus(actionId, "superseded", "assistant_protocol_v2_cutover");
        return new(false, "这个旧版确认已失效，请重新发起操作。", null);
    }

    public void Cancel(string actionId)
    {
        var action = data.Action(actionId);
        if (action is not null && action.Status == "awaiting_confirmation" &&
            TryReadPlanConfirmation(action.IntentJson, out var planConfirmation))
        {
            try { planPipeline.CancelPlan(planConfirmation.ConfirmationId); }
            catch (InvalidOperationException) { }
            catch (KeyNotFoundException) { }
        }
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

    static string PlanConfirmationFailureText(string code) => code switch
    {
        "confirmation_expired" => "确认已过期，请重新发起操作",
        "target_version_changed" => "目标事项在确认期间已变化，请查看最新内容后重新发起",
        "plan_hash_changed" or "command_schema_stale" => "计划内容已失效，请重新发起操作",
        "confirmation_not_awaiting" => "该计划已被确认或取消",
        "transaction_rollback" => "其中一步执行失败，所有写操作已回滚",
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
            CreateLongTermItemArgumentsV1 { Title: { } title } => $"已添加长期事项：{title}",
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


    static bool TryReadArgumentClarification(string json, out PlanArgumentClarificationActionV2 clarification)
    {
        try
        {
            clarification = JsonSerializer.Deserialize<PlanArgumentClarificationActionV2>(json)!;
            return clarification is not null && clarification.Kind == "assistant_argument_clarification_v2" &&
                   !string.IsNullOrWhiteSpace(clarification.Evidence);
        }
        catch (JsonException)
        {
            clarification = default!;
            return false;
        }
    }
    static bool TryReadPlanConfirmation(string json, out PlanConfirmationActionV2 confirmation)
    {
        try
        {
            confirmation = JsonSerializer.Deserialize<PlanConfirmationActionV2>(json)!;
            return confirmation is not null && confirmation.Kind == "assistant_plan_confirmation_v2" &&
                   !string.IsNullOrWhiteSpace(confirmation.ConfirmationId) &&
                   !string.IsNullOrWhiteSpace(confirmation.PlanId);
        }
        catch (JsonException)
        {
            confirmation = default!;
            return false;
        }
    }

    static bool TryReadPlanClarification(string json, out PlanClarificationActionV2 clarification)
    {
        try
        {
            clarification = JsonSerializer.Deserialize<PlanClarificationActionV2>(json)!;
            return clarification is not null && clarification.Kind == "assistant_plan_clarification_v2" &&
                   !string.IsNullOrWhiteSpace(clarification.Evidence) && clarification.Candidates.Count > 1;
        }
        catch (JsonException)
        {
            clarification = default!;
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

    sealed record PlanArgumentClarificationActionV2(
        string Kind,
        ConversationOperationV2 Operation,
        string Evidence,
        IReadOnlyList<string> MissingFields,
        IReadOnlyList<AssistantPlanCandidateBindingV2> Candidates,
        DateTimeOffset ExpiresAt);
    sealed record PlanConfirmationActionV2(
        string Kind,
        string ConfirmationId,
        string PlanId,
        IReadOnlyList<AssistantPlanPreviewStepV2> Preview);

    sealed record PlanClarificationActionV2(
        string Kind,
        ConversationOperationV2 Operation,
        string Evidence,
        IReadOnlyList<AssistantPlanCandidateBindingV2> Candidates);
    sealed record DecompositionDraftAction(string Kind, string DraftId, string Goal);
    sealed record TimeSuggestionAction(string Kind, string Command, string Title, string? Notes, AssistantPriorityV1? Priority, IReadOnlyList<DateTime> Options);


    public static string RecurrenceText(RecurrenceKind recurrence, IReadOnlyList<DayOfWeek> weekdays) => recurrence switch
    {
        RecurrenceKind.Daily => "\u6BCF\u5929",
        RecurrenceKind.Weekdays => "\u5DE5\u4F5C\u65E5",
        RecurrenceKind.OfficialWorkdays => "\u6CD5\u5B9A\u5DE5\u4F5C\u65E5",
        RecurrenceKind.StatutoryHolidays => "\u6CD5\u5B9A\u8282\u5047\u65E5",
        RecurrenceKind.Weekly => "\u6BCF\u5468" + string.Join("\u3001", weekdays.OrderBy(day => day).Select(DayName)),
        _ => throw new ArgumentOutOfRangeException(nameof(recurrence))
    };

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