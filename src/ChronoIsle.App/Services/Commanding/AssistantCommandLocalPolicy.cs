using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ChronoIsle.App.Services.Commanding;

public enum AssistantCommandClarity
{
    Exact,
    Incomplete,
    Ambiguous
}

public sealed record AssistantCommandClarityDecision(
    AssistantCommandClarity Clarity,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> AmbiguityTerms);

public static class AssistantAmbiguityLexicon
{
    public static IReadOnlyList<string> FixedTerms { get; } =
        ["下午", "晚上", "今晚", "周末", "有空", "尽快", "过会儿", "下班后", "睡前", "找个时间", "晚点"];

    public static IReadOnlyList<string> FindMatches(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        return FixedTerms
            .Where(term => input.Contains(term, StringComparison.Ordinal))
            .Where(term => term is not ("下午" or "晚上") || !HasExplicitClock(input, term))
            .ToArray();
    }

    static bool HasExplicitClock(string input, string period) =>
        Regex.IsMatch(
            input,
            Regex.Escape(period) + @"\s*(?:\d{1,2}|[零〇一二两三四五六七八九十]{1,3})\s*(?:点|时|:)",
            RegexOptions.CultureInvariant);
}

/// <summary>Computes clarity from trusted local rules. Model explanation fields are never consulted.</summary>
public static class LocalAssistantCommandClarityClassifier
{
    public static AssistantCommandClarityDecision Classify(AssistantCommandEnvelope envelope, string originalUserInput)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var missing = RequiredMissingFields(envelope).Distinct(StringComparer.Ordinal).ToArray();
        var ambiguities = AssistantAmbiguityLexicon.FindMatches(originalUserInput);
        var clarity = missing.Length > 0
            ? AssistantCommandClarity.Incomplete
            : ambiguities.Count > 0
                ? AssistantCommandClarity.Ambiguous
                : AssistantCommandClarity.Exact;
        return new(clarity, missing, ambiguities);
    }

    static IEnumerable<string> RequiredMissingFields(AssistantCommandEnvelope envelope)
    {
        switch (envelope.Arguments)
        {
            case CreateTodoArgumentsV1 value:
                if (Blank(value.Title)) yield return "title";
                foreach (var field in OptionalRecurrenceMissing(value.Recurrence, "recurrence")) yield return field;
                break;
            case CreateReminderArgumentsV1 value:
                if (Blank(value.Title)) yield return "title";
                if (!Resolvable(value.Remind)) yield return "remind";
                foreach (var field in OptionalRecurrenceMissing(value.Recurrence, "recurrence")) yield return field;
                break;
            case CreateEventArgumentsV1 value:
                if (Blank(value.Title)) yield return "title";
                if (!Resolvable(value.Start)) yield return "start";
                if (!Resolvable(value.End)) yield return "end";
                break;
            case ListItemsArgumentsV1 value:
                foreach (var field in PeriodMissing(value.Range, "range")) yield return field;
                break;
            case UpdateTodoArgumentsV1 value:
                if (value.Target?.HasAnyClue != true) yield return "target";
                if (value.Changes?.HasAnyChange != true) yield return "changes";
                break;
            case CompleteTodoArgumentsV1 value:
                if (value.Target?.HasAnyClue != true) yield return "target";
                break;
            case DeleteTodoArgumentsV1 value:
                if (value.Target?.HasAnyClue != true) yield return "target";
                break;
            case CreateRecurringTaskArgumentsV1 value:
                if (Blank(value.Title)) yield return "title";
                if (value.Kind is null) yield return "kind";
                if (value.WallStart?.LocalTime is null) yield return "wallStart.localTime";
                foreach (var field in RequiredRecurrenceMissing(value.Recurrence, "recurrence")) yield return field;
                break;
            case RescheduleItemArgumentsV1 value:
                if (value.Target?.HasAnyClue != true) yield return "target";
                if (!Resolvable(value.NewTime)) yield return "newTime";
                break;
            case DecomposeGoalArgumentsV1 value:
                if (Blank(value.Goal)) yield return "goal";
                break;
            case SummarizePeriodArgumentsV1 value:
                foreach (var field in PeriodMissing(value.Period, "period")) yield return field;
                break;
        }
    }

    static IEnumerable<string> OptionalRecurrenceMissing(AssistantRecurrenceRuleV1? value, string path) =>
        value is null ? [] : RequiredRecurrenceMissing(value, path);

    static IEnumerable<string> RequiredRecurrenceMissing(AssistantRecurrenceRuleV1? value, string path)
    {
        if (value is null) return [path];
        var missing = new List<string>();
        if (value.Frequency is null) missing.Add(path + ".frequency");
        if (value.Frequency == AssistantRecurrenceFrequencyV1.Weekly && (value.Weekdays?.Count ?? 0) == 0)
            missing.Add(path + ".weekdays");
        if (value.Frequency == AssistantRecurrenceFrequencyV1.Monthly && value.MonthDay is null)
            missing.Add(path + ".monthDay");
        if (value.End is { Kind: null }) missing.Add(path + ".end.kind");
        if (value.End?.Kind == AssistantRecurrenceEndKindV1.Count && value.End.Count is null)
            missing.Add(path + ".end.count");
        if (value.End?.Kind == AssistantRecurrenceEndKindV1.Until && value.End.UntilDate is null)
            missing.Add(path + ".end.untilDate");
        return missing;
    }

    static IEnumerable<string> PeriodMissing(AssistantPeriodV1? value, string path)
    {
        if (value is null) return [path];
        var missing = new List<string>();
        if (value.Kind is null) missing.Add(path + ".kind");
        if (value.Kind == AssistantPeriodKindV1.Custom)
        {
            if (value.StartDate is null) missing.Add(path + ".startDate");
            if (value.EndDate is null) missing.Add(path + ".endDate");
        }
        return missing;
    }

    static bool Resolvable(AssistantTimeExpressionV1? value) => value?.HasResolvableInstantShape == true;
    static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}

public enum AssistantLocalIdKind
{
    UserRequest,
    ParseAttempt,
    ClientRequest,
    Confirmation,
    ActionEvent
}

public interface IAssistantLocalIdFactory
{
    string Create(AssistantLocalIdKind kind);
}

public sealed class GuidAssistantLocalIdFactory : IAssistantLocalIdFactory
{
    public string Create(AssistantLocalIdKind kind) => Prefix(kind) + Guid.NewGuid().ToString("N");

    static string Prefix(AssistantLocalIdKind kind) => kind switch
    {
        AssistantLocalIdKind.UserRequest => "ur_",
        AssistantLocalIdKind.ParseAttempt => "pa_",
        AssistantLocalIdKind.ClientRequest => "cr_",
        AssistantLocalIdKind.Confirmation => "cf_",
        AssistantLocalIdKind.ActionEvent => "ae_",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public sealed record TargetRowVersion(string ItemId, long RowVersion);

public sealed class TargetVersionSnapshot
{
    public static string EmptyHash { get; } = ComputeHash([]);
    public IReadOnlyList<TargetRowVersion> Targets { get; }
    public string Hash { get; }

    public TargetVersionSnapshot(IEnumerable<TargetRowVersion>? targets)
    {
        var normalized = (targets ?? [])
            .OrderBy(target => target.ItemId, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Any(target => string.IsNullOrWhiteSpace(target.ItemId) || target.ItemId.Length > 128))
            throw new ArgumentException("Target item IDs must contain 1 to 128 characters.", nameof(targets));
        if (normalized.Any(target => target.RowVersion < 0))
            throw new ArgumentException("Target row versions cannot be negative.", nameof(targets));
        if (normalized.Select(target => target.ItemId).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new ArgumentException("Target item IDs must be unique.", nameof(targets));
        Targets = normalized;
        Hash = ComputeHash(normalized);
    }

    static string ComputeHash(IEnumerable<TargetRowVersion> targets)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var target in targets.OrderBy(value => value.ItemId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("itemId", target.ItemId);
                writer.WriteNumber("rowVersion", target.RowVersion);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Sha256(stream.ToArray());
    }

    static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public static class AssistantCommandHash
{
    static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Hashes only normalized command data; model explanations never affect the hash.</summary>
    public static string Compute(AssistantCommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        AssistantCommandContractValidator.Validate(envelope);
        var arguments = JsonSerializer.SerializeToElement(envelope.Arguments, envelope.Arguments.GetType(), Options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("arguments");
            WriteCanonical(writer, arguments, null);
            writer.WriteString("command", AssistantCommandEnvelopeJson.CommandName(envelope.Command));
            writer.WriteNumber("schemaVersion", envelope.SchemaVersion);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    public static bool Matches(AssistantCommandEnvelope envelope, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash) || expectedHash.Length != 64) return false;
        byte[] expected;
        try { expected = Convert.FromHexString(expectedHash); }
        catch (FormatException) { return false; }
        var actual = Convert.FromHexString(Compute(envelope));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, string? propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(value => value.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToArray();
                if (propertyName is "weekdays" or "clearFields")
                    items = items.OrderBy(value => value.GetString(), StringComparer.Ordinal).ToArray();
                writer.WriteStartArray();
                foreach (var item in items) WriteCanonical(writer, item, null);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, false));
        return options;
    }
}
