using OpenIsland.App.Services.Commanding;

namespace OpenIsland.Tests;

public sealed class AssistantExecutionPolicyTests
{
    public static TheoryData<string> ExactSingleCreates => new()
    {
        Envelope("create_todo", """{"title":"买牛奶"}"""),
        Envelope("create_reminder", """{"title":"喝水","remind":{"relativeExpression":"一小时后","originalText":"一小时后"}}"""),
        Envelope("create_event", """{"title":"开会","start":{"localDate":"2026-07-17","localTime":"15:00:00","originalText":"明天十五点"},"end":{"localDate":"2026-07-17","localTime":"16:00:00","originalText":"明天十六点"}}"""),
        Envelope("create_recurring_task", """{"title":"写周报","kind":"todo","wallStart":{"localTime":"18:00:00","originalText":"每周五十八点"},"recurrence":{"frequency":"weekly","weekdays":["friday"]}}""")
    };

    [Theory]
    [MemberData(nameof(ExactSingleCreates))]
    public void ExactSingleNonDestructiveCreate_CanAutoExecute(string json)
    {
        var decision = AssistantExecutionPolicy.Evaluate(
            AssistantCommandEnvelopeJson.Deserialize(json), "明确创建", new(ProposedItemCount: 1));

        Assert.Equal(AssistantExecutionDisposition.AutoExecute, decision.Disposition);
        Assert.Equal("exact_single_non_destructive_create", decision.Reason);
    }

    [Fact]
    public void ScheduleConflictOrBatchCreate_AlwaysRequiresConfirmation()
    {
        var command = AssistantCommandEnvelopeJson.Deserialize(
            Envelope("create_event", """{"title":"开会","start":{"localDate":"2026-07-17","localTime":"15:00:00","originalText":"明天十五点"},"end":{"localDate":"2026-07-17","localTime":"16:00:00","originalText":"明天十六点"}}"""));

        Assert.Equal(AssistantExecutionDisposition.RequireConfirmation,
            AssistantExecutionPolicy.Evaluate(command, "明天十五点开会", new(HasScheduleConflict: true)).Disposition);
        Assert.Equal(AssistantExecutionDisposition.RequireConfirmation,
            AssistantExecutionPolicy.Evaluate(command, "明天十五点开会", new(ProposedItemCount: 2)).Disposition);
    }

    [Theory]
    [InlineData("update_todo", """{"target":{"title":"日报"},"changes":{"title":"最终日报"}}""")]
    [InlineData("complete_todo", """{"target":{"title":"日报"}}""")]
    [InlineData("delete_todo", """{"target":{"title":"日报"}}""")]
    [InlineData("reschedule_item", """{"target":{"title":"日报"},"newTime":{"relativeExpression":"一小时后","originalText":"一小时后"}}""")]
    public void ExistingItemMutation_UsesLocalTargetCardinality(string commandName, string arguments)
    {
        var command = AssistantCommandEnvelopeJson.Deserialize(Envelope(commandName, arguments));

        var none = AssistantExecutionPolicy.Evaluate(command, "处理日报", new(TargetMatchCount: 0));
        var one = AssistantExecutionPolicy.Evaluate(command, "处理日报", new(TargetMatchCount: 1));
        var many = AssistantExecutionPolicy.Evaluate(command, "处理日报", new(TargetMatchCount: 2));

        Assert.Equal(AssistantExecutionDisposition.RequireClarification, none.Disposition);
        Assert.Equal("target_not_found", none.Reason);
        Assert.Equal(AssistantExecutionDisposition.RequireConfirmation, one.Disposition);
        Assert.Equal("existing_item_mutation_requires_confirmation", one.Reason);
        Assert.Equal(AssistantExecutionDisposition.RequireConfirmation, many.Disposition);
        Assert.Equal("multiple_targets_require_selection", many.Reason);
    }

    [Theory]
    [InlineData("list_items", """{"range":{"kind":"today","originalText":"今天"}}""")]
    [InlineData("summarize_period", """{"period":{"kind":"this_week","originalText":"本周"}}""")]
    public void ReadOnlyCommand_ExecutesWithoutConfirmation(string commandName, string arguments)
    {
        var decision = AssistantExecutionPolicy.Evaluate(
            AssistantCommandEnvelopeJson.Deserialize(Envelope(commandName, arguments)), "查询");

        Assert.Equal(AssistantExecutionDisposition.ExecuteReadOnly, decision.Disposition);
    }

    [Fact]
    public void DecompositionDraft_AlwaysRequiresConfirmation()
    {
        var command = AssistantCommandEnvelopeJson.Deserialize(
            Envelope("decompose_goal", """{"goal":"整理房间","maxItems":5}"""));

        Assert.Equal(AssistantExecutionDisposition.RequireConfirmation,
            AssistantExecutionPolicy.Evaluate(command, "拆解整理房间", new(ProposedItemCount: 5)).Disposition);
    }

    [Fact]
    public void ThisWeekTimeSuggestion_StaysLocalAndNeverReturnsAPastOption()
    {
        var envelope = Envelope("create_reminder", """{"title":"整理项目计划","notes":null,"remind":null,"due":null,"recurrence":null,"priority":null}""");
        var parsed = AssistantCommandEnvelopeJson.Deserialize(envelope);
        var now = new DateTime(2026, 7, 17, 21, 0, 0);

        var options = AmbiguousTimeSuggestionPlanner.SuggestedTimes(now);

        Assert.True(AmbiguousTimeSuggestionPlanner.CanOffer("这周找个时间提醒我整理项目计划", parsed));
        Assert.Equal(3, options.Count);
        Assert.All(options, option => Assert.True(option > now));
        Assert.Equal([DayOfWeek.Wednesday, DayOfWeek.Saturday, DayOfWeek.Sunday], options.Select(option => option.DayOfWeek));
    }

    [Theory]
    [InlineData("找个时间")]
    [InlineData("晚点")]
    public void AdditionalFrozenAmbiguityTerms_BlockExecution(string term)
    {
        var command = AssistantCommandEnvelopeJson.Deserialize(Envelope("create_reminder",
            $$$"""{"title":"喝水","remind":{"relativeExpression":"{{{term}}}","originalText":"{{{term}}}"}}"""));

        var decision = AssistantExecutionPolicy.Evaluate(command, $"{term}提醒我喝水");

        Assert.Equal(AssistantCommandClarity.Ambiguous, decision.Clarity.Clarity);
        Assert.Contains(term, decision.Clarity.AmbiguityTerms);
        Assert.Equal(AssistantExecutionDisposition.RequireClarification, decision.Disposition);
    }

    [Fact]
    public void IncompleteCommand_IsClarifiedBeforeAnyExecutionPolicy()
    {
        var command = AssistantCommandEnvelopeJson.Deserialize(
            Envelope("create_reminder", """{"title":"喝水","remind":null}"""));

        var decision = AssistantExecutionPolicy.Evaluate(command, "提醒我喝水");

        Assert.Equal(AssistantCommandClarity.Incomplete, decision.Clarity.Clarity);
        Assert.Equal(AssistantExecutionDisposition.RequireClarification, decision.Disposition);
    }

    static string Envelope(string command, string arguments) =>
        $$"""{"schemaVersion":1,"command":"{{command}}","arguments":{{arguments}},"missingFields":[],"ambiguityReasons":[]}""";
}
