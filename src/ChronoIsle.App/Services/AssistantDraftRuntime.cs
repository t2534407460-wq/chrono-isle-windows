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
        if (turn.InteractionVersion < 3 && turn.State is "NeedsInput" or "Blocked" &&
            turn.Tasks.Any(t => t.Operation is "update_todo" or "delete_todo" or "complete_todo" or "reschedule_item"))
        {
            // Upgrade stuck cards without interpreting text, preparing commands or executing a write.
            if (turn.State == "Blocked" || turn.Fields.Any(f => f.Key == "request"))
            {
                var guided = BlockedDraft(turn, new([], [], turn.Summary, Blocked: turn.Explanation));
                turn = draftStore.Get(turn.RequestId)!;
                if (guided.Interaction?.State == "NeedsInput") return guided.Interaction;
            }
            if (turn.Fields.Any(f => f.Key.EndsWith(".target") || f.Key.EndsWith(".candidate")) || turn.Bindings.Count == 0)
            {
                var candidates = new Dictionary<string, AssistantPlanCandidateBindingV2>();
                var fields = turn.Tasks.Select((task, i) => (task, i))
                    .Where(t => t.task.Operation is "update_todo" or "delete_todo" or "complete_todo" or "reschedule_item")
                    .Select(t => TargetSelection(t.i, turn.Bindings.GetValueOrDefault(t.i)?.ItemId, candidates)).ToArray();
                turn = SaveDraft(turn with { State = "NeedsInput", Fields = fields, Candidates = candidates,
                    Explanation = "请先勾选要操作的事项。原请求保留，确认前不会更改。" });
            }
        }
        // Upgrade only the questions of unfinished drafts. Opening a chat never executes a plan.
        if (turn.InteractionVersion < 2 && turn.State == "NeedsInput")
        {
            var tasks = EnrichDrafts(turn.Tasks, turn.SourceText);
            if (tasks.Any(t => t.Schedule is not null))
            {
                var compiled = AssistantDraftCompiler.Compile(turn with { Tasks = tasks });
                turn = SaveDraft(turn with { Tasks = tasks, Fields = compiled.Fields, Summary = compiled.Summary,
                    Facts = compiled.Facts, Preview = compiled.Preview, Explanation = PlanningExplanation,
                    State = compiled.Blocked is null ? "NeedsInput" : "Blocked", Reply = compiled.Blocked });
            }
        }
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
            turn = SaveDraft(turn with { State = "Understanding", Tasks = EnrichDrafts(MergeScheduleCorrections(turn.Tasks, understanding.Tasks), understanding.Tasks.Count == 1 ? input : source),
                SourceText = source, ConfirmationId = null, Fields = [],
                Bindings = turn.Bindings.Where(p => p.Key < understanding.Tasks.Count &&
                    p.Key < turn.Tasks.Count && understanding.Tasks[p.Key].Operation == turn.Tasks[p.Key].Operation &&
                    understanding.Tasks[p.Key].Target == turn.Tasks[p.Key].Target).ToDictionary(p => p.Key, p => p.Value),
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
        var fields = new List<AssistantInputField>();
        var bindings = new Dictionary<int, AssistantPlanCandidateBindingV2>(turn.Bindings);
        var candidates = new Dictionary<string, AssistantPlanCandidateBindingV2>();
        for (var i = 0; i < turn.Tasks.Count; i++)
        {
            var task = turn.Tasks[i];
            if (task.Operation is not ("update_todo" or "complete_todo" or "delete_todo" or "reschedule_item") || bindings.ContainsKey(i)) continue;
            var matches = string.IsNullOrWhiteSpace(task.Target) ? [] : planPipeline.FindCandidateBindings(task.Target, $"s{i}");
            if (matches.Count == 1) bindings[i] = matches[0];
            else
            {
                var selection = TargetSelection(i, null, candidates);
                if (selection.Options.Count == 0)
                    return Result(SaveDraft(turn with { State = "Blocked", Fields = [],
                        Summary = task.Evidence, Explanation = "当前没有可操作的事项。请在事项工作台查看归档或已完成内容，或取消本次操作。",
                        Reply = "没有可供修改或删除的事项，尚未执行任何操作。" }));
                fields.Add(selection);
            }
        }
        // Resolve the existing item before planning changes or asking for schedule fields.
        turn = turn with { Bindings = bindings, Candidates = candidates };
        if (fields.Count > 0)
            return Result(SaveDraft(turn with { State = "NeedsInput", Fields = fields,
                Summary = string.Join("\n", turn.Tasks.Select(t => t.Evidence)),
                Reply = "请先确定要操作的已有事项；当前没有执行任何修改。" }));
        var recurring = data.RecurringReminders();
        var planning = turn with { Tasks = turn.Tasks.Select((task, i) =>
            task.Operation is "update_todo" or "reschedule_item" && bindings.TryGetValue(i, out var binding) &&
            recurring.FirstOrDefault(r => r.Id == binding.ItemId) is { } existing
                ? AssistantScenarioPlanner.MergeExisting(task, existing, turn) : task).ToArray() };
        if (turn.Tasks.Select((task, i) => (task, i)).Any(t =>
            t.task.Operation is "update_todo" or "reschedule_item" && (t.task.Schedule is not null || t.task.RepeatText is not null) &&
            !recurring.Any(r => r.Id == bindings[t.i].ItemId)))
            return Result(SaveDraft(turn with { State = "Blocked", Fields = [],
                Reply = "目标不是周期提醒，不能直接修改重复规则。请明确要修改的计划。",
                Explanation = "目标不是周期提醒，不能直接修改重复规则。" }));
        var compiled = AssistantDraftCompiler.Compile(planning);
        if (compiled.Blocked is not null)
            return BlockedDraft(turn, compiled);
        var queryReply = QueryContent(turn, out var queryFields);
        fields.AddRange(compiled.Fields.Concat(queryFields));
        var managed = bindings.Count == 0 ? [] : data.ManagedItems();
        var before = bindings.Count == 0 ? "" : "当前事项：\n" + string.Join("\n", bindings.OrderBy(p => p.Key).Select(p =>
        {
            var item = managed.FirstOrDefault(item => item.Id == p.Value.ItemId);
            return $"- {p.Value.Title} · " + (item?.RecurrenceLabel is { } rule ? rule + " · " : "") +
                (item?.ScheduledAt?.ToString("yyyy-MM-dd HH:mm") ?? p.Value.TimeText ?? "未设时间");
        })) + "\n\n本次操作：\n";
        turn = turn with { Fields = fields, Summary = before + (string.IsNullOrWhiteSpace(queryReply) ? compiled.Summary : queryReply + "\n\n待执行：\n" + compiled.Summary), Bindings = bindings, Candidates = candidates, Facts = compiled.Facts, Preview = compiled.Preview,
            Explanation = turn.Tasks.Any(t => t.Schedule is not null && t.Operation is "update_todo" or "reschedule_item")
                ? "将在原计划上更新规则，未指定的内容保留。确认后生效。"
                : turn.Tasks.Any(t => t.Schedule is not null && t.Operation.StartsWith("create_")) ? PlanningExplanation : "已保留你提供的信息，只补充影响执行的条件。" };
        if (fields.Count > 0)
        {
            turn = SaveDraft(turn with { State = "NeedsInput", Reply = fields.FirstOrDefault(f => f.Error is not null) is { } invalid
                ? $"请调整“{invalid.Label}”：{invalid.Error} 已填写的信息已保留。"
                : "请补充下方信息，随后核对要执行的操作。" });
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
            Reply = "请核对下方操作。确认后执行；你也可以修改信息或取消。" });
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
        if (action == "select_targets" && turn.State == "NeedsInput" && turn.Bindings.Count > 0)
        {
            var candidates = new Dictionary<string, AssistantPlanCandidateBindingV2>();
            var fields = turn.Bindings.OrderBy(p => p.Key).Select(p => TargetSelection(p.Key, p.Value.ItemId, candidates)).ToArray();
            return Result(SaveDraft(turn with { Fields = fields, Candidates = candidates,
                Reply = "请重新勾选目标。已提交的修改要求保留，随后按新目标重新预览。" }));
        }
        if (action == "edit" && turn.State == "Blocked")
        {
            var guided = BlockedDraft(turn, new([], [], turn.Summary, Blocked: turn.Explanation));
            if (guided.Interaction?.State == "NeedsInput") return guided;
            turn = draftStore.Get(turn.RequestId)!;
            return Result(SaveDraft(turn with { State = "NeedsInput",
                Fields = [new("request", "调整需求", "text", [], turn.SourceText.Split("\n用户在卡片中补充：")[0],
                    turn.Explanation)], Reply = "修改需求后会重新规划；当前没有执行任何操作。" }));
        }
        if (action == "edit" && turn.Tasks.Count > 0)
        {
            if (turn.ConfirmationId is not null)
            {
                if (planPipeline.ReadPlanResult(turn.ConfirmationId) is { Succeeded: true } receipt) return CompleteDraft(turn, receipt);
                planPipeline.CancelPlan(turn.ConfirmationId);
            }
            var recurring = data.RecurringReminders();
            var planning = turn with { Tasks = turn.Tasks.Select((task, i) =>
                task.Operation is "update_todo" or "reschedule_item" && turn.Bindings.TryGetValue(i, out var binding) &&
                recurring.FirstOrDefault(r => r.Id == binding.ItemId) is { } existing
                    ? AssistantScenarioPlanner.MergeExisting(task, existing, turn) : task).ToArray() };
            var edit = AssistantDraftCompiler.Compile(planning, edit: true);
            var candidates = new Dictionary<string, AssistantPlanCandidateBindingV2>();
            var selections = turn.Tasks.Select((task, i) => (task, i))
                .Where(t => t.task.Operation is "update_todo" or "reschedule_item" or "delete_todo" or "complete_todo")
                .Select(t => TargetSelection(t.i, turn.Bindings.GetValueOrDefault(t.i)?.ItemId, candidates)).ToArray();
            if (edit.Fields.Count == 0 && selections.Length == 0) return Result(turn, "此任务没有可直接编辑的字段，可取消后重新描述。");
            return Result(SaveDraft(turn with { State = "NeedsInput", Fields = selections.Concat(edit.Fields).ToArray(), Candidates = candidates, ConfirmationId = null, Editing = true,
                Reply = "修改后将重新校验，并生成新的确认卡。" }));
        }
        if (action == "retry" && turn.State is "Failed" or "Interrupted" or "Understanding" or "Prepared")
        {
            if (turn.ConfirmationId is not null) return RecoverPrepared(turn);
            return await UnderstandDraftAsync(provider, session, turn, data.Messages(session.Id), turn.SourceText, null, cancellationToken);
        }
        if (action != "submit" || turn.State != "NeedsInput") return Result(turn, "请使用当前卡片提供的操作。");
        if (values.Keys.Any(k => turn.Fields.All(f => f.Key != k))) return Result(turn, "提交包含过期字段，请使用最新卡片。");
        if (turn.Fields.Count == 1 && turn.Fields[0].Key == "request")
        {
            if (!values.TryGetValue("request", out var revised) || string.IsNullOrWhiteSpace(revised) || revised.Length > 2000)
                return Result(turn, "请填写调整后的需求。");
            if (revised.Trim() == turn.Fields[0].Value?.Trim())
                return Result(turn, "需求尚未改变，相同内容不会再次规划。请调整不支持的条件，或取消任务。");
            turn = SaveDraft(turn with { SourceText = revised, Fields = [], ConfirmationId = null });
            return await UnderstandDraftAsync(provider, session, turn, [], revised, null, cancellationToken);
        }
        if (turn.Fields.Any(f => f.Key == "correction.scope"))
        {
            var selection = turn.Fields.Single(f => f.Key.EndsWith(".candidate"));
            if (!values.TryGetValue(selection.Key, out var selectedItems) || string.IsNullOrWhiteSpace(selectedItems) ||
                selectedItems.Split(',').Any(key => selection.Options.All(o => o.Value != key)))
                return Result(turn, "请勾选要操作的事项。");
            if (!values.TryGetValue("correction.scope", out var scope) || scope is not ("partial" or "all") ||
                !values.TryGetValue("correction.times", out var times) || string.IsNullOrWhiteSpace(times) || times.Length > 2000 ||
                scope == "partial" && (!values.TryGetValue("correction.days", out var days) ||
                    AssistantScenarioPlanner.OverrideDayOptions.All(o => o.Value != days)))
                return Result(turn, "请选择修改范围，并填写新的提醒时刻。");
            var patch = turn.Tasks[0] with { Schedule = scope == "partial"
                ? new(DayOverrides: [new(values["correction.days"], times)]) : new(TimesText: times, DayOverrides: []),
                TimeText = null, RepeatText = null, ReminderText = null, UnhandledConstraints = [] };
            turn = turn with { Tasks = [patch], Fields = turn.Fields.Where(f => f.Key.EndsWith(".candidate")).ToArray() };
        }
        var tasks = turn.Tasks.ToArray();
        var bindings = new Dictionary<int, AssistantPlanCandidateBindingV2>(turn.Bindings);
        var additional = new List<(int Index, AssistantPlanCandidateBindingV2 Binding)>();
        foreach (var field in turn.Fields)
        {
            if (field.DependsOn is { } dependency &&
                (!values.TryGetValue(dependency, out var selected) || selected != field.DependsValue)) continue;
            values.TryGetValue(field.Key, out var raw);
            if (field.Required && string.IsNullOrWhiteSpace(raw)) return Result(turn, "请填写或选择卡片中的所有必填项。");
            var value = raw?.Trim() ?? "";
            if (value.Length > 2000 || field.Kind == "choice" && field.Options.All(o => o.Value != value) ||
                field.Kind == "multichoice" && value.Split(',').Any(v => field.Options.All(o => o.Value != v)))
                return Result(turn, "所选内容无效，请从卡片中重新选择。");
            var parts = field.Key.Split('.');
            var index = int.Parse(parts[0]);
            if (parts[1] == "candidate")
            {
                var selectedKeys = value.Split(',').Distinct().ToArray();
                if (selectedKeys.Any(key => !turn.Candidates.ContainsKey(key))) return Result(turn, "目标选项已过期，请重新选择。");
                var candidate = turn.Candidates[selectedKeys[0]];
                bindings[index] = candidate;
                tasks[index] = tasks[index] with { Target = candidate.Title };
                foreach (var key in selectedKeys.Skip(1)) additional.Add((index, turn.Candidates[key]));
            }
            else
            {
                // Values shown from the old item are context, not requests to overwrite a newly selected item.
                if (turn.Editing && value == (field.Value?.Trim() ?? "")) continue;
                tasks[index] = parts[1] == "schedule" ? AssistantScenarioPlanner.Set(tasks[index], parts[2], value)
                    : AssistantDraftCompiler.SetField(tasks[index], parts[1], value);
                if (parts[1] == "target") bindings.Remove(index);
            }
        }
        if (turn.Editing)
            for (var i = 0; i < tasks.Length; i++)
            {
                var ruleFields = turn.Fields.Where(f => f.Key.StartsWith($"{i}.schedule.override")).ToArray();
                if (!ruleFields.Any(f => values.TryGetValue(f.Key, out var raw) && raw.Trim() != (f.Value?.Trim() ?? ""))) continue;
                var rules = ruleFields.Where(f => f.Key.EndsWith("days")).Select(f => new AssistantDayOverrideDraft(
                    values.GetValueOrDefault(f.Key) ?? f.Value,
                    values.GetValueOrDefault(f.Key[..^4] + "times") ?? ruleFields.First(t => t.Key == f.Key[..^4] + "times").Value)).ToArray();
                tasks[i] = tasks[i] with { Schedule = (tasks[i].Schedule ?? new()) with { DayOverrides = rules, ReplaceDayOverrides = true } };
            }
        if (tasks.Count(t => t.Operation is not ("list_items" or "summarize_period")) + additional.Count > 3)
            return SelectionError(turn, values, "一次最多操作 3 个事项，请减少勾选；当前没有执行任何操作。");
        var expanded = tasks.ToList();
        foreach (var extra in additional)
        {
            bindings[expanded.Count] = extra.Binding;
            // Use the edited patch for every selected item; existing rules are merged separately per target.
            expanded.Add(tasks[extra.Index] with { Target = extra.Binding.Title });
        }
        if (bindings.Values.Select(b => b.ItemId).Distinct().Count() != bindings.Count)
            return SelectionError(turn, values, "同一事项只能选择一次，请取消重复勾选。");
        turn = SaveDraft(turn with { Tasks = expanded, Bindings = bindings, State = "Understanding", Fields = [], Editing = false,
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

    AssistantInputField TargetSelection(int index, string? selectedId,
        IDictionary<string, AssistantPlanCandidateBindingV2> candidates)
    {
        var all = planPipeline.AllCandidateBindings($"s{index}");
        var managed = data.ManagedItems().ToDictionary(item => item.Id);
        foreach (var candidate in all) candidates[candidate.CandidateRef] = candidate;
        return new($"{index}.candidate", "勾选要操作的事项", "multichoice",
            all.Select(c => new AssistantInputOption($"{c.Title} · {KindLabel(c.Kind)} · " +
                (managed.GetValueOrDefault(c.ItemId)?.RecurrenceLabel is { } rule ? rule + " · " : "") +
                (managed.GetValueOrDefault(c.ItemId)?.ScheduledAt?.ToString("MM-dd HH:mm") ?? c.TimeText ?? "未设时间"), c.CandidateRef)).ToArray(),
            all.FirstOrDefault(c => c.ItemId == selectedId)?.CandidateRef,
            "已列出全部可操作事项（不含归档、已完成及只读事项）。一次最多 3 项；确认前不会更改。" );
    }

    AssistantConversationResult SelectionError(AssistantDraftTurn turn, IReadOnlyDictionary<string, string> values, string message) =>
        Result(SaveDraft(turn with { Reply = message, Fields = turn.Fields.Select(f => f with
        { Value = values.TryGetValue(f.Key, out var value) ? value : f.Value, Error = f.Key.EndsWith(".candidate") ? message : f.Error }).ToArray() }));

    AssistantConversationResult BlockedDraft(AssistantDraftTurn turn, AssistantDraftCompilation compiled)
    {
        if (turn.Tasks.Count == 1 && turn.Tasks[0].Operation is "update_todo" or "reschedule_item" &&
            turn.Bindings.TryGetValue(0, out var bound) && data.RecurringReminders().Any(r => r.Id == bound.ItemId &&
                r.Recurrence != RecurrenceKind.StatutoryHolidays && r.Schedule?.IntervalMinutes is null))
        {
            var candidates = new Dictionary<string, AssistantPlanCandidateBindingV2>();
            return Result(SaveDraft(turn with { State = "NeedsInput", Summary = compiled.Summary, Preview = [],
                Explanation = compiled.Blocked + "\n可在下方重新指定修改范围和时刻。提交后以表单替代上方未支持的条件，原事项和未修改的字段保留。",
                Reply = "请勾选事项，再选择修改范围和时刻；无需反复重写同一句话。",
                Fields = [TargetSelection(0, bound.ItemId, candidates),
                    new("correction.scope", "修改范围", "choice", [new("仅修改指定日期，其余保持原规则", "partial"), new("全部执行日使用新的时刻（取消分日期时刻）", "all")]),
                    new("correction.days", "要修改哪类日期？", "choice", AssistantScenarioPlanner.OverrideDayOptions,
                        DependsOn: "correction.scope", DependsValue: "partial"),
                    new("correction.times", "新的提醒时刻", "time_list", [], Help: "24 小时制，例如 02:00；多个时刻用逗号分隔。")], Candidates = candidates }));
        }
        return Result(SaveDraft(turn with { State = "Blocked", Fields = [], Summary = compiled.Summary,
            Explanation = compiled.Blocked!, Facts = compiled.Facts, Preview = [], Reply = compiled.Blocked }));
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
            if (string.IsNullOrWhiteSpace(range) && !string.IsNullOrWhiteSpace(task.Target) || range is "所有" or "全部")
            {
                var items = data.ManagedItems().Where(item => string.IsNullOrWhiteSpace(task.Target) ||
                    item.Title.Contains(task.Target, StringComparison.OrdinalIgnoreCase)).ToArray();
                replies.Add(items.Length == 0 ? "未找到匹配的事项。" : string.Join("\n", items.Select(item =>
                    $"- {item.Title} · {item.Kind switch { "recurring" => "周期提醒", "reminder" => "提醒", "event" => "日程", "long_term" => "长期事项", _ => "待办" }}" +
                    (item.IsCompleted ? "（已完成）" : "") +
                    (item.ScheduledAt is { } at ? $" · {at:yyyy-MM-dd HH:mm}" : "") +
                    (item.RecurrenceLabel is { } rule ? "\n  " + rule : "") +
                    (string.IsNullOrWhiteSpace(item.Notes) ? "" : "\n  " + item.Notes))));
                continue;
            }
            if (task.Evidence.Contains("长期") && string.IsNullOrWhiteSpace(range)) { replies.Add(LongTermQueryText(data.LongTermItems())); continue; }
            var now = turn.ReferenceTime.Date;
            var start = range switch
            {
                "今天" => now, "明天" => now.AddDays(1), "后天" => now.AddDays(2),
                "本周" or "这周" => now.AddDays(-((int)now.DayOfWeek + 6) % 7),
                "下周" => now.AddDays(7 - ((int)now.DayOfWeek + 6) % 7),
                "本月" or "这个月" => new DateTime(now.Year, now.Month, 1), _ => (DateTime?)null
            };
            if (range == "自定义" && DateTime.TryParseExact(task.DueText, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var customStart) &&
                DateTime.TryParseExact(task.EndText, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var customEnd) &&
                customEnd >= customStart && (customEnd - customStart).TotalDays <= 366)
            {
                replies.Add(localQueries.Query(new(customStart, customEnd.AddDays(1), $"{customStart:MM-dd} 至 {customEnd:MM-dd}"), task.Target).ListText);
                continue;
            }
            if (start is null)
            {
                fields.Add(new($"{i}.timeText", "想查看哪个时间范围？", "choice",
                    new[] { "今天", "明天", "后天", "本周", "下周", "本月", "自定义" }.Select(s => new AssistantInputOption(s, s)).ToArray(), range));
                fields.Add(new($"{i}.dueText", "开始日期", "date", [], task.DueText, "自定义范围最多 366 天。", DependsOn: $"{i}.timeText", DependsValue: "自定义"));
                fields.Add(new($"{i}.endText", "结束日期", "date", [], task.EndText, "包含结束当天；需晚于或等于开始日期。", DependsOn: $"{i}.timeText", DependsValue: "自定义"));
                continue;
            }
            var end = range is "本月" or "这个月" ? start.Value.AddMonths(1) : range is "本周" or "这周" or "下周" ? start.Value.AddDays(7) : start.Value.AddDays(1);
            var query = localQueries.Query(new(start.Value, end, range!), task.Target);
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
        var next = turn with { Revision = turn.Revision + 1, InteractionVersion = 3 };
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
            turn.State == "NeedsConfirmation" ? ConfirmationTitle(turn) : turn.State == "NeedsInput" ? "补充操作信息" : turn.State == "Blocked" ? "需要调整需求" : "任务已保留",
            turn.Summary, turn.Fields, turn.State == "NeedsConfirmation" && turn.Preview is { Count: > 0 } && turn.Tasks.All(t => t.Operation.StartsWith("create_"))
                ? "确认后创建完整的重复计划。错过多次时仅补最近一次；休息时段不补发。"
                : turn.State is "Failed" or "Interrupted" ? turn.Reply ?? turn.Explanation : turn.Explanation, turn.Facts, turn.Preview, turn.Bindings.Count > 0);

    static string ConfirmationTitle(AssistantDraftTurn turn) => turn.Tasks.Count != 1 ? "核对操作" : turn.Tasks[0].Operation switch
    {
        "update_todo" or "reschedule_item" => "确认修改", "delete_todo" => "确认删除",
        "complete_todo" => "确认完成", _ => "确认新增"
    };

    static IReadOnlyList<AssistantTaskDraft> MergeScheduleCorrections(IReadOnlyList<AssistantTaskDraft> previous, IReadOnlyList<AssistantTaskDraft> incoming) =>
        incoming.Select(task =>
        {
            var matching = previous.Where(old => old.Operation == task.Operation && old.Title == task.Title && old.Target == task.Target).ToArray();
            if (matching.Length != 1 || matching[0].Schedule is not { } old || task.RepeatText == "不重复") return task;
            var next = task.Schedule ?? new AssistantScheduleDraft();
            var newTimes = next.TimesText ?? (task.Operation is "update_todo" or "reschedule_item" ? task.TimeText ?? task.ReminderText : null);
            var newInterval = next.IntervalText is not null && newTimes is null;
            return task with { Schedule = new(
                newTimes is not null ? null : next.IntervalText ?? old.IntervalText, newTimes is not null ? null : next.WindowText ?? old.WindowText,
                next.ExclusionText ?? (newTimes is not null ? "none" : old.ExclusionText),
                next.DaysText ?? (task.RepeatText != matching[0].RepeatText ? task.RepeatText : null) ?? old.DaysText,
                newTimes ?? (newInterval ? null : old.TimesText), next.StartText ?? old.StartText, next.UntilText ?? old.UntilText,
                next.FirstTrigger ?? old.FirstTrigger, next.Rhythm ?? old.Rhythm, next.WeekdaysText ?? old.WeekdaysText,
                AssistantScenarioPlanner.MergeOverrides(old.DayOverrides, next.DayOverrides),
                next.ReplaceDayOverrides || old.ReplaceDayOverrides) };
        }).ToArray();

    const string PlanningExplanation = "先确定执行日、有效时段与计时方式，再预览实际提醒时刻。确认后创建一个完整的重复计划。";

    static IReadOnlyList<AssistantTaskDraft> EnrichDrafts(IReadOnlyList<AssistantTaskDraft> tasks, string source) =>
        tasks.Select(t => AssistantScenarioPlanner.Enrich(t, tasks.Count == 1 ? source : t.Evidence)).ToArray();

    static AssistantConversationResult Result(AssistantDraftTurn turn, string? reply = null) =>
        new(reply ?? turn.Reply ?? "请继续处理下方任务。", null, turn.State == "Failed",
            turn.RefreshReminders, Interaction: Card(turn));

    static string KindLabel(AssistantItemKindV1 kind) => kind switch
    { AssistantItemKindV1.Todo => "待办", AssistantItemKindV1.Reminder => "提醒", AssistantItemKindV1.Event => "日程", _ => "长期事项" };
}
