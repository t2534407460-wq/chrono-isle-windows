using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronoIsle.App.Services.Commanding;

public enum ConversationSegmentKindV2
{
    Chat,
    Query,
    Command
}

public enum ConversationOperationV2
{
    None,
    ListItems,
    SummarizePeriod,
    CreateTodo,
    CreateReminder,
    CreateEvent,
    CreateLongTermItem,
    CreateRecurringTask,
    UpdateTodo,
    CompleteTodo,
    DeleteTodo,
    RescheduleItem,
    DecomposeGoal
}

public sealed record ConversationSegmentV2(
    string SegmentRef,
    ConversationSegmentKindV2 Kind,
    ConversationOperationV2 Operation,
    string Evidence,
    IReadOnlyList<string> DependsOn);

public sealed record AssistantConversationPlanV2(
    int SchemaVersion,
    IReadOnlyList<ConversationSegmentV2> Segments)
{
    public const int CurrentSchemaVersion = 2;
    public int WriteOperationCount => Segments.Count(segment => segment.Kind == ConversationSegmentKindV2.Command);
}

public sealed record AssistantCandidateRefV2(string CandidateRef, string Title, string Kind, string? TimeText);

public sealed record AssistantTurnContextV2(
    ConversationOperationV2? PendingOperation = null,
    string? PendingEvidence = null,
    IReadOnlyList<string>? MissingFields = null,
    IReadOnlyList<AssistantCandidateRefV2>? Candidates = null,
    DateTimeOffset? ExpiresAt = null,
    AssistantCandidateRefV2? RecentTarget = null)
{
    public static AssistantTurnContextV2 Empty { get; } = new();
    public bool IsActive(DateTimeOffset now) => PendingOperation is not null && (ExpiresAt is null || ExpiresAt > now);
}

public sealed record ConversationPlanResultV2(
    AssistantConversationPlanV2? Plan,
    string? ErrorCode,
    string? ErrorMessage,
    bool Repaired)
{
    public bool IsValid => Plan is not null;
}

public interface IConversationPlanner
{
    Task<ConversationPlanResultV2> PlanAsync(
        ProviderSettings provider,
        AssistantTurnContextV2 context,
        string input,
        CancellationToken cancellationToken = default);
}

public sealed class ConversationPlannerV2(IChatCompletionClient chat) : IConversationPlanner
{
    const string SystemPrompt = """
        You are ChronoIsle's conversation planner. Return exactly one JSON object and no prose.
        Schema:
        {"schemaVersion":2,"segments":[{"segmentRef":"s1","kind":"chat|query|command","operation":"...","evidence":"an exact contiguous substring copied from the current user input","dependsOn":[]}]}
        Allowed query operations: list_items, summarize_period.
        Allowed command operations: create_todo, create_reminder, create_event, create_long_term_item,
        create_recurring_task,
        update_todo, complete_todo, delete_todo, reschedule_item, decompose_goal.
        The update_todo, complete_todo and delete_todo operation names apply to todo, reminder, event and long-term items.
        Chat segments must use operation "none". Split mixed or multiple intents into ordered segments.
        Return at most 5 segments and at most 3 command segments. Dependencies reference segmentRef values.
        Never include database IDs, confirmation IDs, permissions, confidence, execution results, or extra properties.
        Do not invent an intent that is not explicitly supported by an evidence substring.
        """;

    public async Task<ConversationPlanResultV2> PlanAsync(
        ProviderSettings provider,
        AssistantTurnContextV2 context,
        string input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<ModelMessage>
        {
            new("system", SystemPrompt),
            new("system", $"Current local time: {DateTimeOffset.Now:O}; time zone: {TimeZoneInfo.Local.Id}.")
        };
        if (context.IsActive(DateTimeOffset.Now))
            messages.Add(new("system", "ACTIVE_CONTEXT:\n" + JsonSerializer.Serialize(context)));
        messages.Add(new("user", input));

        var first = await chat.Complete(provider, messages, jsonObject: true, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (ConversationPlanJsonV2.TryDeserialize(first, input, out var plan, out var code, out var message))
        {
            AssistantAiDiagnosticsV2.Write("route", "ok", null, plan!.Segments.Count, stopwatch.Elapsed, first);
            return new(plan, null, null, false);
        }

        var repairMessages = new List<ModelMessage>
        {
            new("system", SystemPrompt),
            new("system", "Repair the candidate JSON. Return only a corrected object. Do not add or remove user intents."),
            new("user", $"CURRENT_USER_INPUT:\n{input}\n\nVALIDATION_ERROR:\n{code}: {message}\n\nCANDIDATE:\n{first}")
        };
        var repaired = await chat.Complete(provider, repairMessages, jsonObject: true, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (ConversationPlanJsonV2.TryDeserialize(repaired, input, out plan, out code, out message))
        {
            AssistantAiDiagnosticsV2.Write("route", "repaired", null, plan!.Segments.Count, stopwatch.Elapsed, repaired);
            return new(plan, null, null, true);
        }

        AssistantAiDiagnosticsV2.Write("route", code ?? "route_invalid", null, 0, stopwatch.Elapsed, repaired);
        return new(null, code ?? "route_invalid", message ?? "无法识别本轮意图。", true);
    }
}

public static class ConversationPlanJsonV2
{
    public static bool TryDeserialize(
        string response,
        string input,
        out AssistantConversationPlanV2? plan,
        out string? errorCode,
        out string? errorMessage)
    {
        plan = null;
        errorCode = null;
        errorMessage = null;
        try
        {
            var json = ExtractObject(response);
            if (json is null) return Fail("route_invalid", "模型未返回 JSON 对象。", out plan, out errorCode, out errorMessage);
            using (var duplicateCheck = JsonDocument.Parse(json,
                       new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
            {
                if (TryFindDuplicateProperty(duplicateCheck.RootElement, "$", out var duplicatePath))
                    return Fail("route_invalid", $"JSON 包含重复字段：{duplicatePath}。", out plan, out errorCode, out errorMessage);
            }
            var wire = JsonSerializer.Deserialize<PlanWire>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            });
            if (wire is null || wire.SchemaVersion != AssistantConversationPlanV2.CurrentSchemaVersion)
                return Fail("route_invalid", "schemaVersion 必须为 2。", out plan, out errorCode, out errorMessage);
            if (wire.Segments is null || wire.Segments.Count is < 1 or > 5)
                return Fail("route_invalid", "segments 数量必须在 1 到 5 之间。", out plan, out errorCode, out errorMessage);

            var refs = new HashSet<string>(StringComparer.Ordinal);
            var segments = new List<ConversationSegmentV2>(wire.Segments.Count);
            foreach (var item in wire.Segments)
            {
                var segmentRef = item.SegmentRef?.Trim();
                var evidence = item.Evidence?.Trim();
                if (string.IsNullOrWhiteSpace(segmentRef) || !refs.Add(segmentRef))
                    return Fail("route_invalid", "segmentRef 不能为空或重复。", out plan, out errorCode, out errorMessage);
                if (segmentRef.Length > 32 || !char.IsLetter(segmentRef[0]) ||
                    segmentRef.Any(value => !char.IsLetterOrDigit(value) && value is not '_' and not '-'))
                    return Fail("route_invalid", "segmentRef 格式无效。", out plan, out errorCode, out errorMessage);
                if (string.IsNullOrWhiteSpace(evidence) || !input.Contains(evidence, StringComparison.Ordinal))
                    return Fail("evidence_mismatch", $"证据“{evidence}”不是用户输入中的连续原文。", out plan, out errorCode, out errorMessage);
                if (!TryKind(item.Kind, out var kind) || !TryOperation(item.Operation, out var operation))
                    return Fail("unsupported_operation", "kind 或 operation 不受支持。", out plan, out errorCode, out errorMessage);
                if (!KindMatches(kind, operation))
                    return Fail("unsupported_operation", $"{kind} 与 {operation} 不匹配。", out plan, out errorCode, out errorMessage);
                var dependencies = (item.DependsOn ?? []).Select(value => value.Trim()).ToArray();
                if (dependencies.Any(string.IsNullOrWhiteSpace) ||
                    dependencies.Distinct(StringComparer.Ordinal).Count() != dependencies.Length)
                    return Fail("route_invalid", "dependsOn 包含空值或重复值。", out plan, out errorCode, out errorMessage);
                segments.Add(new(segmentRef, kind, operation, evidence, dependencies));
            }

            if (segments.Count(segment => segment.Kind == ConversationSegmentKindV2.Command) > 3)
                return Fail("route_invalid", "每轮最多允许 3 个写操作。", out plan, out errorCode, out errorMessage);
            if (segments.Any(segment => segment.DependsOn.Any(reference => !refs.Contains(reference) || reference == segment.SegmentRef)))
                return Fail("route_invalid", "dependsOn 引用了不存在的片段或自身。", out plan, out errorCode, out errorMessage);
            if (HasCycle(segments))
                return Fail("route_invalid", "dependsOn 存在循环依赖。", out plan, out errorCode, out errorMessage);

            plan = new(AssistantConversationPlanV2.CurrentSchemaVersion, TopologicalOrder(segments));
            return true;
        }
        catch (JsonException exception)
        {
            return Fail("route_invalid", exception.Message, out plan, out errorCode, out errorMessage);
        }

        static bool Fail(string code, string message, out AssistantConversationPlanV2? failedPlan,
            out string? failedCode, out string? failedMessage)
        {
            failedPlan = null;
            failedCode = code;
            failedMessage = message;
            return false;
        }
    }

    static bool TryKind(string? value, out ConversationSegmentKindV2 kind)
    {
        kind = value?.Trim().ToLowerInvariant() switch
        {
            "chat" => ConversationSegmentKindV2.Chat,
            "query" => ConversationSegmentKindV2.Query,
            "command" => ConversationSegmentKindV2.Command,
            _ => (ConversationSegmentKindV2)(-1)
        };
        return Enum.IsDefined(kind);
    }

    static bool TryFindDuplicateProperty(JsonElement element, string path, out string duplicatePath)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = path + "." + property.Name;
                if (!names.Add(property.Name))
                {
                    duplicatePath = propertyPath;
                    return true;
                }
                if (TryFindDuplicateProperty(property.Value, propertyPath, out duplicatePath)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindDuplicateProperty(item, $"{path}[{index++}]", out duplicatePath)) return true;
            }
        }
        duplicatePath = string.Empty;
        return false;
    }

    public static bool TryOperation(string? value, out ConversationOperationV2 operation)
    {
        operation = value?.Trim().ToLowerInvariant() switch
        {
            "none" => ConversationOperationV2.None,
            "list_items" => ConversationOperationV2.ListItems,
            "summarize_period" => ConversationOperationV2.SummarizePeriod,
            "create_todo" => ConversationOperationV2.CreateTodo,
            "create_reminder" => ConversationOperationV2.CreateReminder,
            "create_event" => ConversationOperationV2.CreateEvent,
            "create_long_term_item" => ConversationOperationV2.CreateLongTermItem,
            "create_recurring_task" => ConversationOperationV2.CreateRecurringTask,
            "update_todo" => ConversationOperationV2.UpdateTodo,
            "complete_todo" => ConversationOperationV2.CompleteTodo,
            "delete_todo" => ConversationOperationV2.DeleteTodo,
            "reschedule_item" => ConversationOperationV2.RescheduleItem,
            "decompose_goal" => ConversationOperationV2.DecomposeGoal,
            _ => (ConversationOperationV2)(-1)
        };
        return Enum.IsDefined(operation);
    }

    public static string OperationName(ConversationOperationV2 operation) => operation switch
    {
        ConversationOperationV2.None => "none",
        ConversationOperationV2.ListItems => "list_items",
        ConversationOperationV2.SummarizePeriod => "summarize_period",
        ConversationOperationV2.CreateTodo => "create_todo",
        ConversationOperationV2.CreateReminder => "create_reminder",
        ConversationOperationV2.CreateEvent => "create_event",
        ConversationOperationV2.CreateLongTermItem => "create_long_term_item",
        ConversationOperationV2.CreateRecurringTask => "create_recurring_task",
        ConversationOperationV2.UpdateTodo => "update_todo",
        ConversationOperationV2.CompleteTodo => "complete_todo",
        ConversationOperationV2.DeleteTodo => "delete_todo",
        ConversationOperationV2.RescheduleItem => "reschedule_item",
        ConversationOperationV2.DecomposeGoal => "decompose_goal",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    static bool KindMatches(ConversationSegmentKindV2 kind, ConversationOperationV2 operation) => kind switch
    {
        ConversationSegmentKindV2.Chat => operation == ConversationOperationV2.None,
        ConversationSegmentKindV2.Query => operation is ConversationOperationV2.ListItems or ConversationOperationV2.SummarizePeriod,
        ConversationSegmentKindV2.Command => operation is not ConversationOperationV2.None
            and not ConversationOperationV2.ListItems
            and not ConversationOperationV2.SummarizePeriod,
        _ => false
    };

    static bool HasCycle(IReadOnlyList<ConversationSegmentV2> segments)
    {
        var dependencies = segments.ToDictionary(segment => segment.SegmentRef, segment => segment.DependsOn, StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return segments.Any(segment => Visit(segment.SegmentRef));

        bool Visit(string reference)
        {
            if (visited.Contains(reference)) return false;
            if (!visiting.Add(reference)) return true;
            foreach (var dependency in dependencies[reference])
                if (Visit(dependency)) return true;
            visiting.Remove(reference);
            visited.Add(reference);
            return false;
        }
    }

    static IReadOnlyList<ConversationSegmentV2> TopologicalOrder(IReadOnlyList<ConversationSegmentV2> segments)
    {
        var remaining = segments.ToDictionary(segment => segment.SegmentRef, StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<ConversationSegmentV2>(segments.Count);
        while (remaining.Count > 0)
        {
            var next = segments.First(segment => remaining.ContainsKey(segment.SegmentRef) &&
                segment.DependsOn.All(emitted.Contains));
            ordered.Add(next);
            emitted.Add(next.SegmentRef);
            remaining.Remove(next.SegmentRef);
        }
        return ordered;
    }

    internal static string? ExtractObject(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;
        var value = response.Trim();
        if (value.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = value.IndexOf('\n');
            var closing = value.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && closing > firstLine) value = value[(firstLine + 1)..closing].Trim();
        }
        var start = value.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < value.Length; index++)
        {
            var character = value[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }
            if (character == '"') inString = true;
            else if (character == '{') depth++;
            else if (character == '}' && --depth == 0) return value[start..(index + 1)];
        }
        return null;
    }

    sealed class PlanWire
    {
        public int SchemaVersion { get; init; }
        public List<SegmentWire>? Segments { get; init; }
    }

    sealed class SegmentWire
    {
        public string? SegmentRef { get; init; }
        public string? Kind { get; init; }
        public string? Operation { get; init; }
        public string? Evidence { get; init; }
        public List<string>? DependsOn { get; init; }
    }
}

internal static class AssistantAiDiagnosticsV2
{
    static readonly object Gate = new();

    public static void Write(
        string stage,
        string code,
        string? operation,
        int stepCount,
        TimeSpan elapsed,
        string response)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ChronoIsle");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "assistant-v2-diagnostics.log");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(response)));
            var entry = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                stage,
                code,
                operation,
                stepCount,
                elapsedMs = (long)elapsed.TotalMilliseconds,
                responseLength = response.Length,
                responseSha256 = hash
            }) + Environment.NewLine;
            lock (Gate)
            {
                if (File.Exists(path) && new FileInfo(path).Length >= 256 * 1024)
                    File.WriteAllText(path, entry);
                else
                    File.AppendAllText(path, entry);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"AssistantAiDiagnosticsV2.Write failed: {exception.Message}");
        }
    }
}
