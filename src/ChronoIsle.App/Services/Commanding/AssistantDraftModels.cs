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
    IReadOnlyList<string>? ProposedTasks = null);

public sealed record AssistantUnderstanding(int Version, string Kind,
    IReadOnlyList<AssistantTaskDraft> Tasks, string? Reply = null);

public sealed record AssistantInputOption(string Label, string Value);
public sealed record AssistantInputField(string Key, string Label, string Kind,
    IReadOnlyList<AssistantInputOption> Options, string? Value = null);

public sealed record AssistantInteraction(string RequestId, int Revision, string State,
    string Title, string Summary, IReadOnlyList<AssistantInputField> Fields)
{
    public bool CanSubmit => Fields.Count > 0 && State == "NeedsInput";
    public bool CanConfirm => State == "NeedsConfirmation";
    public bool CanRetry => State is "Failed" or "Interrupted" or "Prepared";
    public bool CanEdit => State == "NeedsConfirmation" || State is "Failed" or "Interrupted" && Summary.Length > 0;
}

public sealed record AssistantDraftTurn(
    string RequestId, string SessionId, int Revision, string State,
    string SourceText, DateTimeOffset ReferenceTime, string TimeZone,
    DateTimeOffset ExpiresAt, IReadOnlyList<AssistantTaskDraft> Tasks,
    IReadOnlyList<AssistantInputField> Fields,
    IReadOnlyDictionary<int, AssistantPlanCandidateBindingV2> Bindings,
    IReadOnlyDictionary<string, AssistantPlanCandidateBindingV2> Candidates,
    string Summary = "", string? ConfirmationId = null, string? Reply = null,
    bool RefreshReminders = false);

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
