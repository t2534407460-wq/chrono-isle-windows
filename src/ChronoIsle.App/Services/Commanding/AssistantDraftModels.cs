using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChronoIsle.App.Services.Commanding;

// Model-facing data contains language, never identifiers or execution permissions.
public sealed record AssistantTaskDraft(
    string Operation,
    string Evidence,
    string? Title = null,
    string? Target = null,
    string? TimeText = null,
    string? EndText = null,
    string? DueText = null,
    string? ReminderText = null,
    string? RepeatText = null,
    string? Notes = null,
    string? Priority = null,
    string? ItemKind = null,
    IReadOnlyList<string>? ClearFields = null,
    IReadOnlyList<string>? ProposedTasks = null,
    AssistantScheduleDraft? Schedule = null, string? DurationText = null,
    IReadOnlyList<string>? UnhandledConstraints = null);

public sealed record AssistantScheduleDraft(
    string? IntervalText = null, string? WindowText = null, string? ExclusionText = null,
    string? DaysText = null, string? TimesText = null, string? StartText = null, string? UntilText = null,
    string? FirstTrigger = null, string? Rhythm = null, string? WeekdaysText = null);

public sealed record AssistantUnderstanding(int Version, string Kind,
    IReadOnlyList<AssistantTaskDraft> Tasks, string? Reply = null);

public sealed record AssistantInputOption(string Label, string Value)
{
    // The app's ComboBox template displays selected Content directly.
    public override string ToString() => Label;
}
public sealed record AssistantInputField(string Key, string Label, string Kind,
    IReadOnlyList<AssistantInputOption> Options, string? Value = null,
    string? Help = null, bool Required = true, string? DependsOn = null, string? DependsValue = null, string? Error = null);

public sealed record AssistantInteraction(string RequestId, int Revision, string State,
    string Title, string Summary, IReadOnlyList<AssistantInputField> Fields,
    string Explanation = "", IReadOnlyList<string>? Facts = null, IReadOnlyList<string>? Preview = null)
{
    public bool CanSubmit => Fields.Count > 0 && State == "NeedsInput";
    public bool CanConfirm => State == "NeedsConfirmation";
    public bool CanRetry => State is "Failed" or "Interrupted" or "Prepared";
    public bool CanEdit => State is "NeedsConfirmation" or "NeedsInput" or "Blocked" || State is "Failed" or "Interrupted" && Summary.Length > 0;
}

public sealed record AssistantDraftTurn(
    string RequestId, string SessionId, int Revision, string State,
    string SourceText, DateTimeOffset ReferenceTime, string TimeZone,
    DateTimeOffset ExpiresAt, IReadOnlyList<AssistantTaskDraft> Tasks,
    IReadOnlyList<AssistantInputField> Fields,
    IReadOnlyDictionary<int, AssistantPlanCandidateBindingV2> Bindings,
    IReadOnlyDictionary<string, AssistantPlanCandidateBindingV2> Candidates,
    string Summary = "", string? ConfirmationId = null, string? Reply = null,
    bool RefreshReminders = false, int InteractionVersion = 1,
    string Explanation = "", IReadOnlyList<string>? Facts = null, IReadOnlyList<string>? Preview = null);

public static class AssistantDraftJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException("Empty response.");
}
