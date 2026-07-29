using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChronoIsle.App.Services.Commanding;

public interface IOperationArgumentParser
{
    Task<AssistantCommandParseResult> ParseAsync(
        ProviderSettings provider,
        ConversationSegmentV2 segment,
        AssistantTurnContextV2 context,
        CancellationToken cancellationToken = default);
}

public sealed class OperationArgumentParserV2(IChatCompletionClient chat) : IOperationArgumentParser
{
    public async Task<AssistantCommandParseResult> ParseAsync(
        ProviderSettings provider,
        ConversationSegmentV2 segment,
        AssistantTurnContextV2 context,
        CancellationToken cancellationToken = default)
    {
        if (segment.Kind != ConversationSegmentKindV2.Command)
            return new(null, string.Empty, "argument_invalid：只有 command 片段可以解析命令参数。");
        cancellationToken.ThrowIfCancellationRequested();
        var operation = ConversationPlanJsonV2.OperationName(segment.Operation);
        var systemPrompt = BuildPrompt(operation, SchemaFor(segment.Operation));
        var messages = new List<ModelMessage>
        {
            new("system", systemPrompt),
            new("system", $"Current local time: {DateTimeOffset.Now:O}; time zone: {TimeZoneInfo.Local.Id}.")
        };
        if (context.IsActive(DateTimeOffset.Now))
            messages.Add(new("system", "ACTIVE_CONTEXT:\n" + JsonSerializer.Serialize(context)));
        messages.Add(new("user", segment.Evidence));

        var stopwatch = Stopwatch.StartNew();
        var first = await chat.Complete(provider, messages, jsonObject: true);
        cancellationToken.ThrowIfCancellationRequested();
        if (TryNormalizeAndParse(first, segment.Operation, out var parsed, out var error))
        {
            AssistantAiDiagnosticsV2.Write("arguments", "ok", operation, 1, stopwatch.Elapsed, first);
            return new(parsed, first, null);
        }

        var repair = new List<ModelMessage>
        {
            new("system", systemPrompt),
            new("system", "Repair the candidate JSON. Preserve the user's intent. Return only the corrected command object."),
            new("user", $"EVIDENCE:\n{segment.Evidence}\n\nVALIDATION_ERROR:\n{error}\n\nCANDIDATE:\n{first}")
        };
        var repaired = await chat.Complete(provider, repair, jsonObject: true);
        cancellationToken.ThrowIfCancellationRequested();
        if (TryNormalizeAndParse(repaired, segment.Operation, out parsed, out error))
        {
            AssistantAiDiagnosticsV2.Write("arguments", "repaired", operation, 1, stopwatch.Elapsed, repaired);
            return new(parsed, repaired, null);
        }

        AssistantAiDiagnosticsV2.Write("arguments", "argument_invalid", operation, 1, stopwatch.Elapsed, repaired);
        return new(null, repaired, $"argument_invalid：{error}");
    }

    static string BuildPrompt(string operation, string schema) => $$"""
        You are ChronoIsle's {{operation}} argument parser. Return exactly one JSON object and no prose.
        Return this envelope and no extra properties:
        {"schemaVersion":1,"command":"{{operation}}","arguments":{{schema}},"missingFields":[],"ambiguityReasons":[]}
        Copy time wording into originalText. Never invent a time, target, database ID, confirmation ID,
        For target operations, choose only a candidateRef from ACTIVE_CONTEXT.candidates. Never copy or
        invent an item ID. If there is no unique candidate, leave candidateRef null.
        permission, confidence, completion state, or execution result. Put missing required fields in
        missingFields and vague values in ambiguityReasons. Use null for unknown optional fields.
        """;

    static string SchemaFor(ConversationOperationV2 operation) => operation switch
    {
        ConversationOperationV2.CreateTodo =>
            """{"title":null,"notes":null,"due":null,"remind":null,"recurrence":null,"priority":null}""",
        ConversationOperationV2.CreateReminder =>
            """{"title":null,"notes":null,"remind":null,"due":null,"recurrence":null,"priority":null}""",
        ConversationOperationV2.CreateEvent =>
            """{"title":null,"notes":null,"start":null,"end":null,"remind":null}""",
        ConversationOperationV2.CreateLongTermItem =>
            """{"title":null,"notes":null,"due":null,"remind":null,"priority":null}""",
        ConversationOperationV2.CreateRecurringTask =>
            """{"title":null,"notes":null,"kind":null,"wallStart":null,"recurrence":null,"priority":null}""",
        ConversationOperationV2.UpdateTodo =>
            """{"target":{"candidateRef":null,"title":null,"kind":null,"timeHint":null},"changes":{"title":null,"notes":null,"due":null,"remind":null,"priority":null,"clearFields":[]}}""",
        ConversationOperationV2.CompleteTodo or ConversationOperationV2.DeleteTodo =>
            """{"target":{"candidateRef":null,"title":null,"kind":null,"timeHint":null}}""",
        ConversationOperationV2.RescheduleItem =>
            """{"target":{"candidateRef":null,"title":null,"kind":null,"timeHint":null},"newTime":null,"newReminder":null}""",
        ConversationOperationV2.DecomposeGoal =>
            """{"goal":null,"constraints":[],"maxItems":null,"proposedTasks":[]}""",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), "该操作不使用命令参数解析器。")
    };

    internal static bool TryNormalizeAndParse(
        string response,
        ConversationOperationV2 operation,
        out AssistantCommandEnvelope? envelope,
        out string? error)
    {
        envelope = null;
        error = null;
        try
        {
            var json = ConversationPlanJsonV2.ExtractObject(response);
            if (json is null)
            {
                error = "模型未返回 JSON 对象。";
                return false;
            }
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null)
            {
                error = "命令必须是 JSON 对象。";
                return false;
            }
            var sourceArguments = Property(root, "arguments") as JsonObject ?? root;
            var canonical = new JsonObject
            {
                ["schemaVersion"] = AssistantCommandSchema.V1,
                ["command"] = ConversationPlanJsonV2.OperationName(operation),
                ["arguments"] = NormalizeArguments(operation, sourceArguments),
                ["missingFields"] = NormalizeStringArray(Property(root, "missingFields")),
                ["ambiguityReasons"] = NormalizeStringArray(Property(root, "ambiguityReasons"))
            };
            envelope = AssistantCommandEnvelopeJson.Deserialize(canonical.ToJsonString());
            return true;
        }
        catch (Exception exception) when (exception is JsonException or AssistantCommandContractException or InvalidOperationException)
        {
            Debug.WriteLine($"OperationArgumentParserV2 normalize failed: {exception.Message}");
            error = exception is AssistantCommandContractException contract
                ? $"{contract.Code} at {contract.Path}: {contract.Message}"
                : exception.Message;
            return false;
        }
    }

    static JsonObject NormalizeArguments(ConversationOperationV2 operation, JsonObject source) => operation switch
    {
        ConversationOperationV2.CreateTodo => Pick(source,
            ("title", Node), ("notes", Node), ("due", Time), ("remind", Time),
            ("recurrence", Recurrence), ("priority", EnumValue)),
        ConversationOperationV2.CreateReminder => Pick(source,
            ("title", Node), ("notes", Node), ("remind", Time), ("due", Time),
            ("recurrence", Recurrence), ("priority", EnumValue)),
        ConversationOperationV2.CreateEvent => Pick(source,
            ("title", Node), ("notes", Node), ("start", Time), ("end", Time), ("remind", Time)),
        ConversationOperationV2.CreateLongTermItem => Pick(source,
            ("title", Node), ("notes", Node), ("due", Time), ("remind", Time),
            ("priority", EnumValue)),
        ConversationOperationV2.CreateRecurringTask => Pick(source,
            ("title", Node), ("notes", Node), ("kind", EnumValue), ("wallStart", Time),
            ("recurrence", Recurrence), ("priority", EnumValue)),
        ConversationOperationV2.UpdateTodo => Pick(source,
            ("target", Target), ("changes", Changes)),
        ConversationOperationV2.CompleteTodo or ConversationOperationV2.DeleteTodo => Pick(source,
            ("target", Target)),
        ConversationOperationV2.RescheduleItem => Pick(source,
            ("target", Target), ("newTime", Time), ("newReminder", Time)),
        ConversationOperationV2.DecomposeGoal => Pick(source,
            ("goal", Node), ("constraints", StringArray), ("maxItems", Node), ("proposedTasks", ProposedTasks)),
        _ => throw new InvalidOperationException("该操作没有命令参数结构。")
    };

    delegate JsonNode? Normalizer(JsonNode? value);

    static JsonObject Pick(JsonObject source, params (string Name, Normalizer Normalize)[] fields)
    {
        var result = new JsonObject();
        foreach (var field in fields)
            result[field.Name] = field.Normalize(Property(source, field.Name));
        return result;
    }

    static JsonNode? Property(JsonObject source, string name)
    {
        var property = source.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
        return property.Key is null ? null : property.Value;
    }

    static JsonNode? Node(JsonNode? value) => value?.DeepClone();

    static JsonNode? EnumValue(JsonNode? value)
    {
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text)) return Node(value);
        return JsonValue.Create(text.Trim().Replace('-', '_').ToLowerInvariant());
    }

    static JsonNode StringArray(JsonNode? value) => NormalizeStringArray(value);

    static JsonArray NormalizeStringArray(JsonNode? value)
    {
        var result = new JsonArray();
        if (value is not JsonArray values) return result;
        foreach (var item in values)
        {
            if (item is JsonValue scalar && scalar.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                result.Add(text.Trim());
        }
        return result;
    }

    static JsonNode? Time(JsonNode? value)
    {
        if (value is not JsonObject source) return Node(value);
        return Pick(source,
            ("localDate", Node), ("localTime", Node), ("relativeExpression", Node),
            ("timeZoneHint", Node), ("originalText", Node));
    }

    static JsonNode? Target(JsonNode? value)
    {
        if (value is not JsonObject source) return Node(value);
        return Pick(source, ("candidateRef", Node), ("title", Node), ("kind", EnumValue), ("timeHint", Time));
    }

    static JsonNode? Changes(JsonNode? value)
    {
        if (value is not JsonObject source) return Node(value);
        return Pick(source,
            ("title", Node), ("notes", Node), ("due", Time), ("remind", Time),
            ("priority", EnumValue), ("clearFields", EnumArray));
    }

    static JsonNode? Recurrence(JsonNode? value)
    {
        if (value is not JsonObject source) return Node(value);
        return Pick(source,
            ("frequency", EnumValue), ("interval", Node), ("weekdays", EnumArray),
            ("monthDay", Node), ("end", RecurrenceEnd));
    }

    static JsonNode? RecurrenceEnd(JsonNode? value)
    {
        if (value is not JsonObject source) return Node(value);
        return Pick(source, ("kind", EnumValue), ("count", Node), ("untilDate", Node));
    }

    static JsonNode EnumArray(JsonNode? value)
    {
        var result = new JsonArray();
        if (value is not JsonArray values) return result;
        foreach (var item in values) result.Add(EnumValue(item));
        return result;
    }

    static JsonNode ProposedTasks(JsonNode? value)
    {
        var result = new JsonArray();
        if (value is not JsonArray values) return result;
        foreach (var item in values)
        {
            if (item is not JsonObject source) continue;
            result.Add(Pick(source,
                ("title", Node), ("priority", EnumValue), ("estimatedMinutes", Node), ("category", Node)));
        }
        return result;
    }
}
