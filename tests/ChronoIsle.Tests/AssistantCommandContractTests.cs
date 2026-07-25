using ChronoIsle.App.Services.Commanding;

namespace ChronoIsle.Tests;

public sealed class AssistantCommandContractTests
{
    public static TheoryData<string, AssistantCommandName, Type> ValidCommands => new()
    {
        { Envelope("create_todo", """{"title":"买牛奶","priority":"normal"}"""), AssistantCommandName.CreateTodo, typeof(CreateTodoArgumentsV1) },
        { Envelope("create_reminder", """{"title":"喝水","remind":{"relativeExpression":"一小时后","originalText":"一小时后"}}"""), AssistantCommandName.CreateReminder, typeof(CreateReminderArgumentsV1) },
        { Envelope("create_event", """{"title":"开会","start":{"localDate":"2026-07-17","localTime":"15:00:00","originalText":"明天下午三点"},"end":{"localDate":"2026-07-17","localTime":"16:00:00","originalText":"下午四点"}}"""), AssistantCommandName.CreateEvent, typeof(CreateEventArgumentsV1) },
        { Envelope("list_items", """{"range":{"kind":"this_week","originalText":"本周"},"includeCompleted":false}"""), AssistantCommandName.ListItems, typeof(ListItemsArgumentsV1) },
        { Envelope("update_todo", """{"target":{"title":"提交日报","kind":"todo"},"changes":{"title":"提交最终日报"}}"""), AssistantCommandName.UpdateTodo, typeof(UpdateTodoArgumentsV1) },
        { Envelope("complete_todo", """{"target":{"title":"提交日报","kind":"todo"}}"""), AssistantCommandName.CompleteTodo, typeof(CompleteTodoArgumentsV1) },
        { Envelope("delete_todo", """{"target":{"title":"提交日报","kind":"todo"}}"""), AssistantCommandName.DeleteTodo, typeof(DeleteTodoArgumentsV1) },
        { Envelope("create_recurring_task", """{"title":"写周报","kind":"todo","wallStart":{"localTime":"18:00:00","timeZoneHint":"Asia/Shanghai","originalText":"每周五十八点"},"recurrence":{"frequency":"weekly","interval":1,"weekdays":["friday"],"end":{"kind":"never"}}}"""), AssistantCommandName.CreateRecurringTask, typeof(CreateRecurringTaskArgumentsV1) },
        { Envelope("reschedule_item", """{"target":{"title":"提交日报","kind":"todo"},"newTime":{"localDate":"2026-07-18","localTime":"09:00:00","originalText":"改到周六九点"}}"""), AssistantCommandName.RescheduleItem, typeof(RescheduleItemArgumentsV1) },
        { Envelope("decompose_goal", """{"goal":"周末整理房间","constraints":["每天不超过两小时"],"maxItems":5,"proposedTasks":[{"title":"整理桌面","priority":"normal","estimatedMinutes":25,"category":"生活"}]}"""), AssistantCommandName.DecomposeGoal, typeof(DecomposeGoalArgumentsV1) },
        { Envelope("summarize_period", """{"period":{"kind":"this_week","originalText":"本周"}}"""), AssistantCommandName.SummarizePeriod, typeof(SummarizePeriodArgumentsV1) }
    };

    [Theory]
    [MemberData(nameof(ValidCommands))]
    public void Deserialize_UsesTheDedicatedV1Schema(string json, AssistantCommandName command, Type argumentType)
    {
        var envelope = AssistantCommandEnvelopeJson.Deserialize(json);

        Assert.Equal(AssistantCommandSchema.V1, envelope.SchemaVersion);
        Assert.Equal(command, envelope.Command);
        Assert.Equal(argumentType, envelope.Arguments.GetType());
        Assert.Equal(command, AssistantCommandEnvelopeJson.Deserialize(AssistantCommandEnvelopeJson.Serialize(envelope)).Command);
    }

    [Theory]
    [InlineData("confidence", "0.99")]
    [InlineData("confirmationId", "\"cf_fake\"")]
    [InlineData("clientRequestId", "\"cr_fake\"")]
    [InlineData("rowVersion", "12")]
    [InlineData("itemId", "\"database-id\"")]
    public void Deserialize_RejectsModelSuppliedTrustMetadataAtTheEnvelope(string field, string value)
    {
        var json = $$"""{"schemaVersion":1,"command":"create_todo","arguments":{"title":"买牛奶"},"missingFields":[],"ambiguityReasons":[],"{{field}}":{{value}}}""";

        var error = Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(json));

        Assert.Equal("schema_rejected", error.Code);
    }

    [Theory]
    [InlineData("todoId")]
    [InlineData("itemId")]
    [InlineData("rowVersion")]
    [InlineData("confidence")]
    public void MutationArguments_RejectModelSuppliedLocalIdentity(string field)
    {
        var json = Envelope("delete_todo", $$"""{"target":{"title":"日报"},"{{field}}":"forged"}""");
        var nested = Envelope("delete_todo", $$$"""{"target":{"title":"日报","{{{field}}}":"forged"}}""");

        Assert.Equal("schema_rejected",
            Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(json)).Code);
        Assert.Equal("schema_rejected",
            Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(nested)).Code);
    }

    [Fact]
    public void Deserialize_RejectsUnknownVersionCommandCrossCommandFieldsAndDuplicates()
    {
        var version = Envelope("create_todo", """{"title":"买牛奶"}""").Replace("\"schemaVersion\":1", "\"schemaVersion\":2");
        var command = Envelope("run_program", "{}");
        var crossCommand = Envelope("create_todo", """{"title":"买牛奶","start":{"originalText":"明天"}}""");
        var duplicate = """{"schemaVersion":1,"command":"create_todo","command":"delete_todo","arguments":{},"missingFields":[],"ambiguityReasons":[]}""";

        Assert.Equal("unsupported_version", Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(version)).Code);
        Assert.Equal("unsupported_command", Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(command)).Code);
        Assert.Equal("schema_rejected", Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(crossCommand)).Code);
        Assert.Equal("duplicate_property", Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(duplicate)).Code);
    }

    [Fact]
    public void Deserialize_RequiresExactlyTheFiveEnvelopeFieldsAndObjectArguments()
    {
        const string missing = """{"schemaVersion":1,"command":"create_todo","arguments":{},"missingFields":[]}""";
        const string scalarArguments = """{"schemaVersion":1,"command":"create_todo","arguments":true,"missingFields":[],"ambiguityReasons":[]}""";

        Assert.Equal("schema_rejected", Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(missing)).Code);
        Assert.Equal("invalid_type", Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(scalarArguments)).Code);
    }

    [Fact]
    public void ModelExplanations_DoNotControlLocalClarity()
    {
        const string misleadingExact = """
            {"schemaVersion":1,"command":"create_reminder","arguments":{"title":"喝水","remind":null},
             "missingFields":[],"ambiguityReasons":[]}
            """;
        const string misleadingIncomplete = """
            {"schemaVersion":1,"command":"create_todo","arguments":{"title":"买牛奶"},
             "missingFields":["title"],"ambiguityReasons":["model guessed"]}
            """;

        var incomplete = LocalAssistantCommandClarityClassifier.Classify(
            AssistantCommandEnvelopeJson.Deserialize(misleadingExact), "提醒我喝水");
        var exact = LocalAssistantCommandClarityClassifier.Classify(
            AssistantCommandEnvelopeJson.Deserialize(misleadingIncomplete), "添加待办买牛奶");

        Assert.Equal(AssistantCommandClarity.Incomplete, incomplete.Clarity);
        Assert.Equal(["remind"], incomplete.MissingFields);
        Assert.Equal(AssistantCommandClarity.Exact, exact.Clarity);
        Assert.Empty(exact.MissingFields);
    }

    public static IEnumerable<object[]> FixedAmbiguityTerms => AssistantAmbiguityLexicon.FixedTerms.Select(term => new object[] { term });

    [Theory]
    [MemberData(nameof(FixedAmbiguityTerms))]
    public void LocalClarity_FlagsEveryFrozenAmbiguityTerm(string term)
    {
        var json = Envelope("create_reminder",
            $$$"""{"title":"喝水","remind":{"relativeExpression":"{{{term}}}","originalText":"{{{term}}}"}}""");
        var decision = LocalAssistantCommandClarityClassifier.Classify(
            AssistantCommandEnvelopeJson.Deserialize(json), $"{term}提醒我喝水");

        Assert.Equal(AssistantCommandClarity.Ambiguous, decision.Clarity);
        Assert.Contains(term, decision.AmbiguityTerms);
    }

    [Fact]
    public void LocalClarity_AllowsExactRelativeTimeAndUnscheduledTodo()
    {
        var reminder = AssistantCommandEnvelopeJson.Deserialize(Envelope("create_reminder",
            """{"title":"喝水","remind":{"relativeExpression":"一小时后","originalText":"一小时后"}}"""));
        var todo = AssistantCommandEnvelopeJson.Deserialize(Envelope("create_todo", """{"title":"买牛奶"}"""));

        Assert.Equal(AssistantCommandClarity.Exact,
            LocalAssistantCommandClarityClassifier.Classify(reminder, "一小时后提醒我喝水").Clarity);
        Assert.Equal(AssistantCommandClarity.Exact,
            LocalAssistantCommandClarityClassifier.Classify(todo, "添加待办买牛奶").Clarity);
    }

    [Fact]
    public void LocalClarity_RequiresMutationTargetAndChangeWithoutTrustingModelIds()
    {
        var update = AssistantCommandEnvelopeJson.Deserialize(Envelope("update_todo", """{"target":null,"changes":null}"""));
        var result = LocalAssistantCommandClarityClassifier.Classify(update, "修改待办");

        Assert.Equal(AssistantCommandClarity.Incomplete, result.Clarity);
        Assert.Equal(["target", "changes"], result.MissingFields);
    }

    [Fact]
    public void Contract_EnforcesRecurrenceAndDecompositionBounds()
    {
        var badWeekly = Envelope("create_recurring_task", """{"title":"周报","kind":"todo","wallStart":{"localTime":"18:00:00","originalText":"十八点"},"recurrence":{"frequency":"weekly","weekdays":[]}}""");
        var tooMany = Envelope("decompose_goal", """{"goal":"整理房间","maxItems":11}""");

        Assert.Equal("invalid_value", Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(badWeekly)).Code);
        Assert.Equal("invalid_value", Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(tooMany)).Code);
    }

    [Fact]
    public void DecompositionProposals_RequireUsableTitlesAndRespectTheRequestedLimit()
    {
        var overLimit = Envelope("decompose_goal", """{"goal":"整理房间","maxItems":1,"proposedTasks":[{"title":"整理桌面"},{"title":"整理衣柜"}]}""");
        var invalidMinutes = Envelope("decompose_goal", """{"goal":"整理房间","proposedTasks":[{"title":"整理桌面","estimatedMinutes":0}]}""");

        Assert.Equal("invalid_value", Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(overLimit)).Code);
        Assert.Equal("invalid_value", Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(invalidMinutes)).Code);
    }

    [Fact]
    public void CommandHash_ExcludesModelExplanationsAndNormalizesSetOrder()
    {
        const string first = """
            {"schemaVersion":1,"command":"create_recurring_task","arguments":{"title":"周报","kind":"todo",
             "wallStart":{"localTime":"18:00:00","originalText":"每周五十八点"},
             "recurrence":{"frequency":"weekly","weekdays":["friday","monday"]}},
             "missingFields":[],"ambiguityReasons":[]}
            """;
        const string second = """
            {"schemaVersion":1,"command":"create_recurring_task","arguments":{"recurrence":{"weekdays":["monday","friday"],"frequency":"weekly"},
             "wallStart":{"originalText":"每周五十八点","localTime":"18:00:00"},"kind":"todo","title":"周报"},
             "missingFields":["untrusted"],"ambiguityReasons":["untrusted"]}
            """;
        var left = AssistantCommandEnvelopeJson.Deserialize(first);
        var right = AssistantCommandEnvelopeJson.Deserialize(second);

        Assert.Equal(AssistantCommandHash.Compute(left), AssistantCommandHash.Compute(right));
        Assert.Matches("^[0-9a-f]{64}$", AssistantCommandHash.Compute(left));
    }

    [Fact]
    public void CommandHash_ChangesWhenBusinessArgumentsChange()
    {
        var left = AssistantCommandEnvelopeJson.Deserialize(Envelope("create_todo", """{"title":"买牛奶"}"""));
        var right = AssistantCommandEnvelopeJson.Deserialize(Envelope("create_todo", """{"title":"买咖啡"}"""));

        Assert.NotEqual(AssistantCommandHash.Compute(left), AssistantCommandHash.Compute(right));
        Assert.True(AssistantCommandHash.Matches(left, AssistantCommandHash.Compute(left)));
        Assert.False(AssistantCommandHash.Matches(right, AssistantCommandHash.Compute(left)));
    }

    [Fact]
    public void LocalIdFactory_OwnsAllPipelineIdentifiers()
    {
        var ids = new GuidAssistantLocalIdFactory();
        var values = Enum.GetValues<AssistantLocalIdKind>().Select(kind => ids.Create(kind)).ToArray();

        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(values, value => value.StartsWith("ur_", StringComparison.Ordinal));
        Assert.Contains(values, value => value.StartsWith("pa_", StringComparison.Ordinal));
        Assert.Contains(values, value => value.StartsWith("cr_", StringComparison.Ordinal));
        Assert.Contains(values, value => value.StartsWith("cf_", StringComparison.Ordinal));
        Assert.Contains(values, value => value.StartsWith("ae_", StringComparison.Ordinal));
    }

    [Fact]
    public void TargetSnapshotHash_IsOrderIndependentAndDetectsRowVersionChanges()
    {
        var left = new TargetVersionSnapshot([new("b", 2), new("a", 1)]);
        var reordered = new TargetVersionSnapshot([new("a", 1), new("b", 2)]);
        var changed = new TargetVersionSnapshot([new("a", 1), new("b", 3)]);

        Assert.Equal(left.Hash, reordered.Hash);
        Assert.NotEqual(left.Hash, changed.Hash);
        Assert.Equal(TargetVersionSnapshot.EmptyHash, new TargetVersionSnapshot([]).Hash);
        Assert.Throws<ArgumentException>(() => new TargetVersionSnapshot([new("a", 1), new("a", 2)]));
    }

    [Fact]
    public void Confirmation_DefaultsToFifteenMinutesAndBindsCommandAndTargets()
    {
        var ids = new GuidAssistantLocalIdFactory();
        var now = DateTimeOffset.Parse("2026-07-16T10:00:00+08:00");
        var command = AssistantCommandEnvelopeJson.Deserialize(Envelope("delete_todo", """{"target":{"title":"日报","kind":"todo"}}"""));
        var targets = new TargetVersionSnapshot([new("todo-1", 4)]);
        var confirmation = ConfirmationRecord.Create(
            ids, ids.Create(AssistantLocalIdKind.ClientRequest), command, targets, now, "删除待办：日报");

        Assert.Equal(TimeSpan.FromMinutes(15), confirmation.ExpiresAtUtc - confirmation.CreatedAtUtc);
        Assert.Equal(AssistantCommandPipelineState.AwaitingConfirmation, confirmation.Status);
        Assert.True(confirmation.Matches(command, targets));
        Assert.Null(confirmation.ConfirmedAtUtc);
    }

    [Fact]
    public void Confirmation_ExecutesOnceAndTerminalStatesCannotReenter()
    {
        var ids = new GuidAssistantLocalIdFactory();
        var now = DateTimeOffset.Parse("2026-07-16T10:00:00Z");
        var command = AssistantCommandEnvelopeJson.Deserialize(Envelope("create_todo", """{"title":"买牛奶"}"""));
        var targets = new TargetVersionSnapshot([]);
        var awaiting = ConfirmationRecord.Create(
            ids, ids.Create(AssistantLocalIdKind.ClientRequest), command, targets, now, "创建待办：买牛奶");

        awaiting.EnsureCanExecute(command, targets, now.AddMinutes(1));
        var executing = awaiting.TransitionTo(AssistantCommandPipelineState.Executing, now.AddMinutes(1));
        var succeeded = executing.TransitionTo(AssistantCommandPipelineState.Succeeded, now.AddMinutes(2));

        Assert.Equal(now.AddMinutes(1), executing.ConfirmedAtUtc);
        Assert.True(AssistantCommandStateMachine.IsTerminal(succeeded.Status));
        Assert.False(AssistantCommandStateMachine.CanTransition(succeeded.Status, AssistantCommandPipelineState.Executing));
        Assert.Throws<InvalidOperationException>(() => succeeded.TransitionTo(AssistantCommandPipelineState.Executing, now.AddMinutes(3)));
    }

    [Fact]
    public void Confirmation_RechecksExpiryCommandHashAndTargetSnapshotAtExecution()
    {
        var ids = new GuidAssistantLocalIdFactory();
        var now = DateTimeOffset.Parse("2026-07-16T10:00:00Z");
        var command = AssistantCommandEnvelopeJson.Deserialize(Envelope("delete_todo", """{"target":{"title":"日报"}}"""));
        var changedCommand = AssistantCommandEnvelopeJson.Deserialize(Envelope("delete_todo", """{"target":{"title":"周报"}}"""));
        var targets = new TargetVersionSnapshot([new("todo-1", 1)]);
        var changedTargets = new TargetVersionSnapshot([new("todo-1", 2)]);
        var confirmation = ConfirmationRecord.Create(
            ids, ids.Create(AssistantLocalIdKind.ClientRequest), command, targets, now, "删除待办：日报");

        Assert.Throws<InvalidOperationException>(() => confirmation.EnsureCanExecute(changedCommand, targets, now.AddMinutes(1)));
        Assert.Throws<InvalidOperationException>(() => confirmation.EnsureCanExecute(command, changedTargets, now.AddMinutes(1)));
        Assert.Throws<InvalidOperationException>(() => confirmation.EnsureCanExecute(command, targets, now.AddMinutes(15)));
        Assert.Throws<InvalidOperationException>(() => confirmation.TransitionTo(AssistantCommandPipelineState.Executing, now.AddMinutes(15)));
        Assert.Equal(AssistantCommandPipelineState.Expired,
            confirmation.TransitionTo(AssistantCommandPipelineState.Expired, now.AddMinutes(15)).Status);
    }

    [Fact]
    public void PipelineStateMachine_AllowsOnlyFrozenPaths()
    {
        Assert.True(AssistantCommandStateMachine.CanTransition(AssistantCommandPipelineState.Received, AssistantCommandPipelineState.Parsing));
        Assert.True(AssistantCommandStateMachine.CanTransition(AssistantCommandPipelineState.Parsing, AssistantCommandPipelineState.Rejected));
        Assert.True(AssistantCommandStateMachine.CanTransition(AssistantCommandPipelineState.PolicyReady, AssistantCommandPipelineState.AutoExecuting));
        Assert.True(AssistantCommandStateMachine.CanTransition(AssistantCommandPipelineState.AwaitingConfirmation, AssistantCommandPipelineState.Stale));
        Assert.False(AssistantCommandStateMachine.CanTransition(AssistantCommandPipelineState.Succeeded, AssistantCommandPipelineState.Executing));
        Assert.False(AssistantCommandStateMachine.CanTransition(AssistantCommandPipelineState.ClarificationRequired, AssistantCommandPipelineState.PolicyReady));
    }

    static string Envelope(string command, string arguments) =>
        $$"""{"schemaVersion":1,"command":"{{command}}","arguments":{{arguments}},"missingFields":[],"ambiguityReasons":[]}""";
}
