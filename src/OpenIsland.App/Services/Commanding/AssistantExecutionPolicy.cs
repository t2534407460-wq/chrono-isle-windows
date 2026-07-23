namespace OpenIsland.App.Services.Commanding;

public enum AssistantExecutionDisposition
{
    ExecuteReadOnly,
    AutoExecute,
    RequireClarification,
    RequireConfirmation
}

public sealed record AssistantLocalExecutionFacts(
    int ProposedItemCount = 1,
    int? TargetMatchCount = null,
    bool HasScheduleConflict = false);

public sealed record AssistantExecutionDecision(
    AssistantExecutionDisposition Disposition,
    string Reason,
    AssistantCommandClarityDecision Clarity);

/// <summary>Local-only policy. It never consumes model confidence or model-provided identifiers.</summary>
public static class AssistantExecutionPolicy
{
    static readonly string[] AdditionalAmbiguityTerms = ["找个时间", "晚点"];

    public static AssistantExecutionDecision Evaluate(
        AssistantCommandEnvelope command,
        string originalUserInput,
        AssistantLocalExecutionFacts? facts = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        facts ??= new();
        if (facts.ProposedItemCount < 0) throw new ArgumentOutOfRangeException(nameof(facts));

        var clarity = EffectiveClarity(command, originalUserInput);
        if (clarity.Clarity != AssistantCommandClarity.Exact)
            return new(AssistantExecutionDisposition.RequireClarification,
                clarity.Clarity == AssistantCommandClarity.Incomplete ? "required_fields_missing" : "time_expression_ambiguous",
                clarity);

        if (command.Command is AssistantCommandName.ListItems or AssistantCommandName.SummarizePeriod)
            return new(AssistantExecutionDisposition.ExecuteReadOnly, "read_only_command", clarity);

        if (RequiresTargetResolution(command.Command))
        {
            if (facts.TargetMatchCount is null or 0)
                return new(AssistantExecutionDisposition.RequireClarification, "target_not_found", clarity);
            if (facts.TargetMatchCount > 1)
                return new(AssistantExecutionDisposition.RequireConfirmation, "multiple_targets_require_selection", clarity);
            return new(AssistantExecutionDisposition.RequireConfirmation, "existing_item_mutation_requires_confirmation", clarity);
        }

        if (command.Command == AssistantCommandName.DecomposeGoal)
            return new(AssistantExecutionDisposition.RequireConfirmation, "decomposition_draft_requires_confirmation", clarity);

        if (IsCreate(command.Command))
        {
            if (facts.ProposedItemCount != 1)
                return new(AssistantExecutionDisposition.RequireConfirmation, "batch_create_requires_confirmation", clarity);
            if (facts.HasScheduleConflict)
                return new(AssistantExecutionDisposition.RequireConfirmation, "schedule_conflict_requires_confirmation", clarity);
            return new(AssistantExecutionDisposition.AutoExecute, "exact_single_non_destructive_create", clarity);
        }

        return new(AssistantExecutionDisposition.RequireConfirmation, "command_requires_confirmation", clarity);
    }

    static AssistantCommandClarityDecision EffectiveClarity(AssistantCommandEnvelope command, string input)
    {
        var local = LocalAssistantCommandClarityClassifier.Classify(command, input);
        var additions = AdditionalAmbiguityTerms
            .Where(term => !string.IsNullOrWhiteSpace(input) && input.Contains(term, StringComparison.Ordinal))
            .ToArray();
        if (additions.Length == 0) return local;
        var terms = local.AmbiguityTerms.Concat(additions).Distinct(StringComparer.Ordinal).ToArray();
        return local.Clarity == AssistantCommandClarity.Incomplete
            ? local with { AmbiguityTerms = terms }
            : new(AssistantCommandClarity.Ambiguous, local.MissingFields, terms);
    }

    static bool RequiresTargetResolution(AssistantCommandName command) => command is
        AssistantCommandName.UpdateTodo or AssistantCommandName.CompleteTodo or
        AssistantCommandName.DeleteTodo or AssistantCommandName.RescheduleItem;

    static bool IsCreate(AssistantCommandName command) => command is
        AssistantCommandName.CreateTodo or AssistantCommandName.CreateReminder or
        AssistantCommandName.CreateEvent or AssistantCommandName.CreateRecurringTask;
}
