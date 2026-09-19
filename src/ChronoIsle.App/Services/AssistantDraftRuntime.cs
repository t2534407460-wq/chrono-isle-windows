using System.Diagnostics;
using ChronoIsle.App.Services.Commanding;
using ChronoIsle.App.Services.Domain;

namespace ChronoIsle.App.Services;

public sealed partial class AssistantActionService
{
    readonly SemaphoreSlim draftGate = new(1, 1);

    bool UseDraftRuntime(ChatSession session, IReadOnlyList<ChatMessage> history, string input)
    {
        if (draftInterpreter is null) return false;
        if (draftStore.Active(session.Id) is not null) return true;
        // These established special workflows keep their existing calendar and confirmation semantics.
        return data.ActiveConfirmation(session.Id) is null && data.ActiveClarification(session.Id) is null &&
            data.ActiveHolidayReminderBatch(session.Id) is null &&
            !TryParseOfficialSleepSchedule(input, out _) &&
            !(TryParseExplicitClock(input, out _) && LastUserRequestedHolidayBatch(history));
    }

    public AssistantInteraction? GetInteraction(string sessionId)
    {
        var turn = draftStore.Active(sessionId);
        if (turn is null) return LegacyInteraction(sessionId);
        if (turn.State == "Understanding")
            return Card(turn with { State = "Interrupted" });
        return Card(turn);
    }

    AssistantInteraction? LegacyInteraction(string sessionId)
    {
        var action = data.ActiveClarification(sessionId) ?? data.ActiveHolidayReminderBatch(sessionId);
        if (action is null) return null;
        IReadOnlyList<AssistantInputField> fields;
        if (TryReadPlanClarification(action.IntentJson, out var selection))
            fields = [new("selection", "选择要操作的事项", "choice",
                selection.Candidates.Select(c => new AssistantInputOption(
                    $"{c.Title} · {KindLabel(c.Kind)} · {c.TimeText ?? "未设时间"}", c.CandidateRef)).ToArray())];
        else if (TryReadTimeSuggestion(action.IntentJson, out var suggestion))
            fields = [new("selection", "选择提醒时间", "choice",
                suggestion.Options.Select((time, index) => new AssistantInputOption(
                    time.ToString("MM-dd HH:mm"), (index + 1).ToString())).ToArray())];
        else if (action.Status == "holiday_batch_time_pending")
            fields = [new("time", "节假日提醒时间", "time", [])];
        else if (TryReadArgumentClarification(action.IntentJson, out var details))
            fields = [new("details", details.MissingFields.Count == 0 ? "补充任务信息" :
                "补充：" + string.Join("、", details.MissingFields), "text", [])];
        else fields = [new("details", "补充任务信息", "text", [])];
        return new("legacy:" + action.Id, 1, "NeedsInput", "继续之前的任务", action.SourceText, fields);
    }

    async Task<AssistantConversationResult> InteractLegacyAsync(ProviderSettings provider, ChatSession session,
        string requestId, string action, IReadOnlyDictionary<string, string> values, CancellationToken token)
    {
        var card = LegacyInteraction(session.Id);
        if (card is null || card.RequestId != requestId)
            return new("此任务已更新，请使用最新卡片。", null, false, Interaction: GetInteraction(session.Id));
        if (action == "cancel")
        {
            data.SetActionStatus(requestId["legacy:".Length..], "cancelled");
            return new("已取消待补充任务。", null, false);
        }
        if (action != "submit" || values.Count != card.Fields.Count)
            return new("请使用当前卡片的选项。", null, false, Interaction: card);
        foreach (var field in card.Fields)
            if (!values.TryGetValue(field.Key, out var value) || string.IsNullOrWhiteSpace(value) ||
                value.Length > 2000 || field.Options.Count > 0 && field.Options.All(o => o.Value != value))
                return new("请完成卡片中的选项。", null, false, Interaction: card);
        var input = string.Join("；", card.Fields.Select(f => values[f.Key]));
        var result = await HandleAsync(provider, session, data.Messages(session.Id), input, cancellationToken: token);
        return result with { Interaction = GetInteraction(session.Id) };
    }

    async Task<AssistantConversationResult> HandleDraftTurnAsync(ProviderSettings provider,
        ChatSession session, IReadOnlyList<ChatMessage> history, string input,
        Action<string>? onDelta, CancellationToken cancellationToken)
    {
        await draftGate.WaitAsync(cancellationToken);
        try
        {
            var active = draftStore.Active(session.Id);
            if (active is not null && (IsConfirmationReply(input) || IsCancellationReply(input)))
                return await InteractCoreAsync(provider, session, active.RequestId, active.Revision,
                    IsCancellationReply(input) ? "cancel" : "confirm", new Dictionary<string, string>(), cancellationToken);
            if (active is { ConfirmationId: not null } && active.State == "Prepared")
                return RecoverPrepared(active);
            if (active is not null && active.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                Expire(active);
                active = null;
            }
            var turn = active ?? new AssistantDraftTurn(Guid.NewGuid().ToString("N"), session.Id, 1,
                "Understanding", input, DateTimeOffset.Now, new SystemTimeZoneCatalog().LocalIanaTimeZoneId,
                DateTimeOffset.UtcNow.AddMinutes(15), [], [], new Dictionary<int, AssistantPlanCandidateBindingV2>(),
                new Dictionary<string, AssistantPlanCandidateBindingV2>());
            if (active is null) draftStore.Save(turn);
            return await UnderstandDraftAsync(provider, session, turn, history, input, onDelta, cancellationToken);
        }
        finally { draftGate.Release(); }
    }

    async Task<AssistantConversationResult> UnderstandDraftAsync(ProviderSettings provider, ChatSession session,
        AssistantDraftTurn turn, IReadOnlyList<ChatMessage> history, string input, Action<string>? onDelta,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var understanding = turn.Tasks.Count == 0 ? AssistantDraftInterpreter.TryLocal(input) : null;
            understanding ??= await draftInterpreter!.UnderstandAsync(provider, input, turn, history, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (understanding.Kind == "chat")
            {
                var text = onDelta is null ? understanding.Reply ?? "请描述你想处理的事项。" :
                    (await HandleGeneralChatAsync(provider, history, input, onDelta, deadline.Token)).Reply;
                if (turn.Tasks.Count > 0) return Result(turn, text + "\n\n原任务仍保留在下方卡片中。");
                turn = SaveDraft(turn with { State = "Succeeded", Reply = text });
                return Result(turn);
            }
            if (turn.ConfirmationId is not null)
            {
                var existing = planPipeline.ReadPlanResult(turn.ConfirmationId);
                if (existing?.Succeeded == true) return CompleteDraft(turn, existing);
                planPipeline.CancelPlan(turn.ConfirmationId);
            }
            var source = turn.SourceText == input ? input : turn.SourceText + "\n" + input;
            turn = SaveDraft(turn with { State = "Understanding", Tasks = understanding.Tasks,
                SourceText = source, ConfirmationId = null, Fields = [],
                Bindings = new Dictionary<int, AssistantPlanCandidateBindingV2>(),
                Candidates = new Dictionary<string, AssistantPlanCandidateBindingV2>() });
            AssistantAiDiagnosticsV2.Write("draft", "understood", null, turn.Tasks.Count, stopwatch.Elapsed, turn.RequestId);
            return ProcessDraft(session, turn, deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            turn = draftStore.Get(turn.RequestId) ?? turn;
            if (turn.ConfirmationId is not null) return RecoverPrepared(turn);
            turn = SaveDraft(turn with { State = "Interrupted", Reply = "已停止理解，任务已保留，尚未执行。" });
            return Result(turn);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            turn = draftStore.Get(turn.RequestId) ?? turn;
            if (turn.ConfirmationId is not null) return RecoverPrepared(turn);
            var message = e switch
            {
                AssistantModelException model => model.Message,
                OperationCanceledException => "模型响应超时，输入已保留，可以重试。",
                InvalidOperationException when e.Message.Contains("API Key", StringComparison.Ordinal) => "请先在设置中填写 API Key。",
                _ => "这次未能整理任务，已保留输入，请重试。"
            };
            AssistantAiDiagnosticsV2.Write("draft", e is AssistantModelException failure ? failure.Code : e.GetType().Name,
                null, turn.Tasks.Count, stopwatch.Elapsed, turn.RequestId);
            turn = SaveDraft(turn with { State = "Failed", Reply = message });
            return Result(turn);
        }
    }

    AssistantConversationResult ProcessDraft(ChatSession session, AssistantDraftTurn turn, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (turn.Tasks.All(t => t.Operation is "list_items" or "summarize_period"))
            return QueryDraft(turn);
        var compiled = AssistantDraftCompiler.Compile(turn);
        var queryReply = QueryContent(turn, out var queryFields);
        var fields = compiled.Fields.Concat(queryFields).ToList();
        var bindings = new Dictionary<int, AssistantPlanCandidateBindingV2>(turn.Bindings);
        var candidates = new Dictionary<string, AssistantPlanCandidateBindingV2>();
        for (var i = 0; i < turn.Tasks.Count; i++)
        {
            var task = turn.Tasks[i];
            if (task.Operation is not ("update_todo" or "complete_todo" or "delete_todo" or "reschedule_item") || bindings.ContainsKey(i)) continue;
            var matches = string.IsNullOrWhiteSpace(task.Target) ? [] : planPipeline.FindCandidateBindings(task.Target, $"s{i}");
            if (matches.Count == 1) bindings[i] = matches[0];
            else if (matches.Count == 0)
                fields.Add(new($"{i}.target", "要操作哪个事项？请输入完整名称", "text", [], task.Target));
            else
            {
                foreach (var candidate in matches) candidates[candidate.CandidateRef] = candidate;
                fields.Add(new($"{i}.candidate", "找到同名事项，请选择目标", "choice",
                    matches.Select(c => new AssistantInputOption($"{c.Title} · {KindLabel(c.Kind)} · {c.TimeText ?? "未设时间"}", c.CandidateRef)).ToArray()));
            }
        }
        turn = turn with { Fields = fields, Summary = string.IsNullOrWhiteSpace(queryReply) ? compiled.Summary : queryReply + "\n\n待执行：\n" + compiled.Summary, Bindings = bindings, Candidates = candidates };
        if (fields.Count > 0)
        {
            turn = SaveDraft(turn with { State = "NeedsInput", Reply = "还需要补充以下信息。请在卡片中选择或填写，已有内容会保留。" });
            return Result(turn);
        }
        if (compiled.Commands.Any(c => c.Command == AssistantCommandName.DecomposeGoal))
        {
            if (compiled.Commands.Count != 1)
                return Result(SaveDraft(turn with { State = "Failed", Reply = "目标拆解需要单独处理，请分开发送；当前未执行任何操作。" }));
            var result = PrepareDecompositionDraft(session, turn.SourceText, (DecomposeGoalArgumentsV1)compiled.Commands[0].Arguments, null);
            SaveDraft(turn with { State = result.PendingAction is null ? "Failed" : "Superseded", Reply = result.Reply });
            return result;
        }
        // Bind the target to a local snapshot only after the user has selected it.
        var commandIndexes = turn.Tasks.Select((task, index) => (task, index))
            .Where(t => t.task.Operation is not ("list_items" or "summarize_period")).Select(t => t.index).ToArray();
        var commands = compiled.Commands.Select((c, i) => bindings.TryGetValue(commandIndexes[i], out var b) ? AttachCandidate(c, b) : c).ToArray();
        var nextRevision = turn.Revision + 1;
        turn = SaveDraft(turn with { State = "Prepared", ConfirmationId = $"draft_{turn.RequestId}_{nextRevision}", Reply = null });
        var plan = planPipeline.PrepareDraftPlan(turn.RequestId, turn.Revision, commands, bindings.Values.ToArray(), cancellationToken);
        if (plan.Succeeded) return CompleteDraft(turn, plan);
        if (plan.State == AssistantPlanPipelineState.ClarificationRequired)
            return Result(SaveDraft(turn with { State = "Failed", ConfirmationId = null,
                Reply = "任务仍有未明确的信息，请修改卡片或重新描述。" }));
        if (plan.State != AssistantPlanPipelineState.AwaitingConfirmation)
            return Result(SaveDraft(turn with { State = "Failed", Reply = "此计划已失效，请重新发起操作。" }));
        if (plan.Code == "ready_to_execute")
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CompleteDraft(turn, planPipeline.ConfirmPlan(plan.ConfirmationId!, cancellationToken));
        }
        turn = SaveDraft(turn with { State = "NeedsConfirmation",
            Summary = turn.Summary + "\n" + string.Join("\n", plan.Preview.Select(p => p.Description)),
            Reply = "请核对下方计划。确认后整体执行；你也可以修改信息或取消。" });
        return Result(turn);
    }

    public async Task<AssistantConversationResult> InteractAsync(ProviderSettings provider, ChatSession session,
        string requestId, int revision, string action, IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        await draftGate.WaitAsync(cancellationToken);
        try { return await InteractCoreAsync(provider, session, requestId, revision, action, values, cancellationToken); }
        finally { draftGate.Release(); }
    }

    async Task<AssistantConversationResult> InteractCoreAsync(ProviderSettings provider, ChatSession session,
        string requestId, int revision, string action, IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        if (requestId.StartsWith("legacy:", StringComparison.Ordinal))
            return await InteractLegacyAsync(provider, session, requestId, action, values, cancellationToken);
        var turn = draftStore.Get(requestId);
        if (turn is null || turn.SessionId != session.Id) return new("这张卡片已不可用，请重新发起操作。", null, true);
        if (turn.State == "Succeeded") return Result(turn);
        if (turn.State is "Cancelled" or "Expired" or "Superseded") return new("这张卡片已经结束，请使用最新任务。", null, false);
        if (turn.Revision != revision) return Result(turn, "任务已更新，请使用最新卡片。");
        if (turn.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            if (turn.ConfirmationId is not null && planPipeline.ReadPlanResult(turn.ConfirmationId) is { Succeeded: true } done)
                return CompleteDraft(turn, done);
            Expire(turn);
            return new("这张卡片已过期，未执行新操作。请重新发起任务。", null, false);
        }
        if (action == "cancel")
        {
            if (turn.ConfirmationId is not null && planPipeline.ReadPlanResult(turn.ConfirmationId) is { } receipt)
            {
                if (receipt.Succeeded) return CompleteDraft(turn, receipt);
                planPipeline.CancelPlan(turn.ConfirmationId);
            }
            return Result(SaveDraft(turn with { State = "Cancelled", Reply = "已取消任务，没有执行新的写操作。" }));
        }
        if (action == "confirm" && turn.State == "NeedsConfirmation" && turn.ConfirmationId is not null)
            return CompleteDraft(turn, planPipeline.ConfirmPlan(turn.ConfirmationId, cancellationToken));
        if (action == "edit" && turn.Tasks.Count > 0)
        {
            if (turn.ConfirmationId is not null)
            {
                if (planPipeline.ReadPlanResult(turn.ConfirmationId) is { Succeeded: true } receipt) return CompleteDraft(turn, receipt);
                planPipeline.CancelPlan(turn.ConfirmationId);
            }
            var edit = AssistantDraftCompiler.Compile(turn, edit: true);
            if (edit.Fields.Count == 0) return Result(turn, "此任务没有可直接编辑的字段，可取消后重新描述。");
            return Result(SaveDraft(turn with { State = "NeedsInput", Fields = edit.Fields, ConfirmationId = null,
                Reply = "修改后将重新校验，并生成新的确认卡。" }));
        }
        if (action == "retry" && turn.State is "Failed" or "Interrupted" or "Understanding" or "Prepared")
        {
            if (turn.ConfirmationId is not null) return RecoverPrepared(turn);
            return await UnderstandDraftAsync(provider, session, turn, data.Messages(session.Id), turn.SourceText, null, cancellationToken);
        }
        if (action != "submit" || turn.State != "NeedsInput") return Result(turn, "请使用当前卡片提供的操作。");
        if (values.Keys.Any(k => turn.Fields.All(f => f.Key != k))) return Result(turn, "提交包含过期字段，请使用最新卡片。");
        var tasks = turn.Tasks.ToArray();
        var bindings = new Dictionary<int, AssistantPlanCandidateBindingV2>(turn.Bindings);
        foreach (var field in turn.Fields)
        {
            if (!values.TryGetValue(field.Key, out var raw) || string.IsNullOrWhiteSpace(raw)) return Result(turn, "请填写或选择卡片中的所有必填项。");
            var value = raw.Trim();
            if (value.Length > 2000 || field.Options.Count > 0 && field.Options.All(o => o.Value != value))
                return Result(turn, "所选内容无效，请从卡片中重新选择。");
            var parts = field.Key.Split('.');
            var index = int.Parse(parts[0]);
            if (parts[1] == "candidate")
            {
                if (!turn.Candidates.TryGetValue(value, out var candidate)) return Result(turn, "目标选项已过期，请重新选择。");
                bindings[index] = candidate;
            }
            else
            {
                tasks[index] = AssistantDraftCompiler.SetField(tasks[index], parts[1], value);
                if (parts[1] == "target") bindings.Remove(index);
            }
        }
        turn = SaveDraft(turn with { Tasks = tasks, Bindings = bindings, State = "Understanding", Fields = [],
            SourceText = turn.SourceText + "\n用户在卡片中补充：" + string.Join("；", values.Select(p => $"{p.Key}={p.Value}")) });
        try { return ProcessDraft(session, turn, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            turn = draftStore.Get(turn.RequestId)!;
            return turn.ConfirmationId is null ? Result(SaveDraft(turn with { State = "Interrupted", Reply = "操作已停止，补充信息已保留。" })) : RecoverPrepared(turn);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException)
        {
            turn = draftStore.Get(turn.RequestId)!;
            if (turn.ConfirmationId is not null) return RecoverPrepared(turn);
            return Result(SaveDraft(turn with { State = "Failed", Reply = "补充信息未通过校验，请修改后继续。" }));
        }
    }

    AssistantConversationResult QueryDraft(AssistantDraftTurn turn)
    {
        var text = QueryContent(turn, out var fields);
        return fields.Count > 0
            ? Result(SaveDraft(turn with { State = "NeedsInput", Fields = fields, Reply = "请选择要查看的时间范围。" }))
            : Result(SaveDraft(turn with { State = "Succeeded", Reply = text, Fields = [] }));
    }

    string QueryContent(AssistantDraftTurn turn, out List<AssistantInputField> fields)
    {
        fields = new List<AssistantInputField>();
        var replies = new List<string>();
        for (var i = 0; i < turn.Tasks.Count; i++)
        {
            var task = turn.Tasks[i];
            if (task.Operation is not ("list_items" or "summarize_period")) continue;
            var range = task.TimeText;
            if (task.Evidence.Contains("长期") && string.IsNullOrWhiteSpace(range)) { replies.Add(LongTermQueryText(data.LongTermItems())); continue; }
            var now = turn.ReferenceTime.Date;
            var start = range switch
            {
                "今天" => now, "明天" => now.AddDays(1), "后天" => now.AddDays(2),
                "本周" or "这周" => now.AddDays(-((int)now.DayOfWeek + 6) % 7),
                "下周" => now.AddDays(7 - ((int)now.DayOfWeek + 6) % 7),
                "本月" or "这个月" => new DateTime(now.Year, now.Month, 1), _ => (DateTime?)null
            };
            if (start is null)
            {
                fields.Add(new($"{i}.timeText", "选择查询范围", "choice",
                    new[] { "今天", "明天", "后天", "本周", "下周", "本月" }.Select(s => new AssistantInputOption(s, s)).ToArray()));
                continue;
            }
            var end = range is "本月" or "这个月" ? start.Value.AddMonths(1) : range is "本周" or "这周" or "下周" ? start.Value.AddDays(7) : start.Value.AddDays(1);
            var query = localQueries.Query(new(start.Value, end, range!));
            replies.Add(query.ListText);
        }
        return string.Join("\n\n", replies);
    }

    AssistantConversationResult RecoverPrepared(AssistantDraftTurn turn)
    {
        var receipt = turn.ConfirmationId is null ? null : planPipeline.ReadPlanResult(turn.ConfirmationId);
        if (receipt?.Succeeded == true) return CompleteDraft(turn, receipt);
        if (receipt?.State == AssistantPlanPipelineState.AwaitingConfirmation)
            return Result(SaveDraft(turn with { State = "NeedsConfirmation", Reply = "任务已准备好但尚未执行，请核对后确认。" }));
        return Result(SaveDraft(turn with { State = "Failed", ConfirmationId = null,
            Reply = "任务没有完成，草稿已保留，请修改信息后再试。" }));
    }

    AssistantConversationResult CompleteDraft(AssistantDraftTurn turn, AssistantPlanPipelineResultV2 result)
    {
        if (!result.Succeeded)
            return Result(SaveDraft(turn with { State = "Failed", ConfirmationId = null,
                Reply = "计划没有执行：" + PlanConfirmationFailureText(result.Code) + "。请修改信息后重新核对。" }));
        // The receipt and domain writes were committed together by AssistantPlanPipeline.
        return Result(SaveDraft(turn with { State = "Succeeded", Fields = [], RefreshReminders = true,
            Reply = "已执行：\n" + turn.Summary }));
    }

    AssistantDraftTurn SaveDraft(AssistantDraftTurn turn)
    {
        var next = turn with { Revision = turn.Revision + 1 };
        draftStore.Save(next, turn.Revision);
        return next;
    }

    void Expire(AssistantDraftTurn turn)
    {
        if (turn.ConfirmationId is not null) planPipeline.CancelPlan(turn.ConfirmationId);
        SaveDraft(turn with { State = "Expired", Reply = "任务已过期。" });
    }

    static AssistantInteraction? Card(AssistantDraftTurn turn) => turn.State is "Succeeded" or "Cancelled" or "Expired" or "Superseded"
        ? null : new(turn.RequestId, turn.Revision, turn.State,
            turn.State == "NeedsConfirmation" ? "核对并执行" : turn.State == "NeedsInput" ? "补充任务信息" : "任务已保留",
            turn.Summary, turn.Fields);

    static AssistantConversationResult Result(AssistantDraftTurn turn, string? reply = null) =>
        new(reply ?? turn.Reply ?? "请继续处理下方任务。", null, turn.State == "Failed",
            turn.RefreshReminders, Interaction: Card(turn));

    static string KindLabel(AssistantItemKindV1 kind) => kind switch
    { AssistantItemKindV1.Todo => "待办", AssistantItemKindV1.Reminder => "提醒", AssistantItemKindV1.Event => "日程", _ => "长期事项" };
}
