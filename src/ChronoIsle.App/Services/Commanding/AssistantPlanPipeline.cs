using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChronoIsle.App.Services.Domain;
using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.App.Services.Commanding;

public enum AssistantPlanPipelineState
{
    Succeeded,
    AwaitingConfirmation,
    ClarificationRequired,
    Failed,
    Cancelled,
    Expired,
    Stale
}

public sealed record AssistantPlanPreviewStepV2(
    int StepIndex,
    string Operation,
    string Description,
    IReadOnlyList<string> Targets);

public sealed record AssistantPlanCandidateBindingV2(
    string CandidateRef,
    string ItemId,
    long RowVersion,
    string Title,
    AssistantItemKindV1 Kind,
    string? TimeText);

public sealed record AssistantPlanPipelineResultV2(
    AssistantPlanPipelineState State,
    string PlanId,
    string? ConfirmationId,
    string Code,
    IReadOnlyList<AssistantPlanPreviewStepV2> Preview,
    IReadOnlyList<string> ItemIds,
    string? ResultJson)
{
    public bool Succeeded => State == AssistantPlanPipelineState.Succeeded;
}

sealed record AssistantPlanTargetSnapshotV2(int StepIndex, string ItemId, long RowVersion);

sealed record StoredAssistantPlanV2(
    string ConfirmationId,
    string PlanId,
    string PlanHash,
    string PlanJson,
    string TargetSnapshotHash,
    string TargetSnapshotJson,
    string PreviewJson,
    string Status,
    DateTimeOffset ExpiresAt,
    string? ResultJson);

/// <summary>
/// Trusted V2 execution boundary. A plan confirmation represents all writes in one user turn.
/// Confirmation replay is idempotent and confirmed commands execute in one database savepoint.
/// </summary>
public sealed class AssistantPlanPipeline
{
    readonly IDbWriteQueue writeQueue;
    readonly SqliteConnectionFactory connections;
    readonly AssistantCommandPipeline singleCommandPipeline;
    readonly Func<DateTimeOffset> utcNow;
    readonly ITimeZoneCatalog timeZones;
    readonly IOccurrenceTimeResolver occurrenceResolver;

    public AssistantPlanPipeline(
        string databasePath,
        AssistantCommandPipeline? singleCommandPipeline = null,
        Func<DateTimeOffset>? utcNow = null,
        ITimeZoneCatalog? timeZones = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _ = new LifeDataService(databasePath);
        var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(databasePath);
        writeQueue = runtime.WriteQueue;
        connections = runtime.ConnectionFactory;
        this.singleCommandPipeline = singleCommandPipeline ?? new AssistantCommandPipeline(runtime.WriteQueue);
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.timeZones = timeZones ?? new SystemTimeZoneCatalog();
        occurrenceResolver = new OccurrenceTimeResolver(this.timeZones);
        EnsureCreatedAndInvalidateV1();
    }

    public AssistantPlanPipelineResultV2 SubmitPlan(
        string sourceText,
        IReadOnlyList<AssistantCommandEnvelope> commands,
        IReadOnlyList<AssistantPlanCandidateBindingV2>? candidateBindings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(commands), "A plan must contain between 1 and 3 write commands.");
        foreach (var command in commands) AssistantCommandContractValidator.Validate(command);

        if (commands.Count == 1)
        {
            var preflight = Preflight(sourceText, commands, candidateBindings ?? [], cancellationToken);
            if (preflight.State == AssistantPlanPipelineState.ClarificationRequired) return preflight;
            if (preflight.State != AssistantPlanPipelineState.AwaitingConfirmation)
            {
                var single = singleCommandPipeline.SubmitParsed(sourceText, commands[0], cancellationToken);
                return FromSingle(single, preflight.Preview);
            }
            return preflight;
        }

        return Preflight(sourceText, commands, candidateBindings ?? [], cancellationToken, forceConfirmation: true);
    }

    public IReadOnlyList<AssistantPlanCandidateBindingV2> FindCandidateBindings(
        string evidence,
        string segmentRef,
        int maximum = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentRef);
        if (maximum is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(maximum));
        using var connection = connections.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,row_version,title,COALESCE(item_type,kind),
                   COALESCE(due_local_datetime,remind_local_datetime,start_local_datetime,
                            due_utc_instant,remind_utc_instant,start_utc_instant)
            FROM life_items
            WHERE deleted_at IS NULL AND status NOT IN ('Completed','Cancelled','Ignored')
            ORDER BY updated_at DESC
            """;
        using var reader = command.ExecuteReader();
        var matches = new List<(string Id, long Version, string Title, AssistantItemKindV1 Kind, string? Time)>();
        while (reader.Read())
        {
            var title = reader.GetString(2);
            if (!evidence.Contains(title, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Enum.TryParse<AssistantItemKindV1>(reader.GetString(3), true, out var kind)) continue;
            matches.Add((reader.GetString(0), reader.GetInt64(1), title, kind,
                reader.IsDBNull(4) ? null : reader.GetString(4)));
            if (matches.Count == maximum) break;
        }
        return matches.Select((item, index) => new AssistantPlanCandidateBindingV2(
            $"{segmentRef}_c{index + 1}", item.Id, item.Version, item.Title, item.Kind, item.Time)).ToArray();
    }
    public AssistantPlanPipelineResultV2 PrepareDraftPlan(string requestId, int revision,
        IReadOnlyList<AssistantCommandEnvelope> commands,
        IReadOnlyList<AssistantPlanCandidateBindingV2> bindings,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(requestId, "N", out _) || revision < 1)
            throw new ArgumentException("Invalid local draft identity.");
        if (commands.Count is < 1 or > 3) throw new ArgumentException("Expected 1 to 3 commands.");
        foreach (var command in commands) AssistantCommandContractValidator.Validate(command);
        return Preflight("本地已校验的任务草稿", commands, bindings, cancellationToken,
            forceConfirmation: commands.Count > 1 || commands.Any(c => c.Arguments is CreateRecurringTaskArgumentsV1 { DailySchedule: not null }), draftConfirmationId: $"draft_{requestId}_{revision}");
    }

    public AssistantPlanPipelineResultV2? ReadPlanResult(string confirmationId) =>
        writeQueue.Execute<AssistantPlanPipelineResultV2?>(uow =>
        {
            var stored = Read(uow, confirmationId);
            if (stored is null) return null;
            var preview = JsonSerializer.Deserialize<AssistantPlanPreviewStepV2[]>(stored.PreviewJson) ?? [];
            return Terminal(stored, preview, StatusState(stored.Status), "stored_result");
        });

    public AssistantPlanPipelineResultV2 ConfirmPlan(
        string confirmationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationId);
        var now = utcNow().ToUniversalTime();
        return writeQueue.Execute<AssistantPlanPipelineResultV2>(unitOfWork =>
        {
            var stored = Read(unitOfWork, confirmationId)
                ?? throw new KeyNotFoundException("Plan confirmation does not exist.");
            var preview = JsonSerializer.Deserialize<AssistantPlanPreviewStepV2[]>(stored.PreviewJson) ?? [];
            if (stored.Status == "Succeeded")
            {
                var priorIds = ResultItemIds(stored.ResultJson);
                return new(AssistantPlanPipelineState.Succeeded, stored.PlanId, confirmationId,
                    "idempotent_replay", preview, priorIds, stored.ResultJson);
            }
            if (stored.Status != "AwaitingConfirmation")
                return Terminal(stored, preview, StatusState(stored.Status), "confirmation_not_awaiting");
            if (now >= stored.ExpiresAt)
            {
                SetStatus(unitOfWork, confirmationId, "AwaitingConfirmation", "Expired", now);
                return Terminal(stored, preview, AssistantPlanPipelineState.Expired, "confirmation_expired");
            }

            IReadOnlyList<AssistantCommandEnvelope> commands;
            try { commands = AssistantCommandPlanJsonV2.Deserialize(stored.PlanJson); }
            catch (Exception exception) when (exception is JsonException or AssistantCommandContractException)
            {
                SetStatus(unitOfWork, confirmationId, "AwaitingConfirmation", "Stale", now);
                return Terminal(stored, preview, AssistantPlanPipelineState.Stale, "command_schema_stale");
            }
            if (!string.Equals(Hash(stored.PlanJson), stored.PlanHash, StringComparison.Ordinal))
            {
                SetStatus(unitOfWork, confirmationId, "AwaitingConfirmation", "Stale", now);
                return Terminal(stored, preview, AssistantPlanPipelineState.Stale, "plan_hash_changed");
            }

            var expected = JsonSerializer.Deserialize<AssistantPlanTargetSnapshotV2[]>(stored.TargetSnapshotJson) ?? [];
            var current = ReadCurrentTargets(unitOfWork, expected);
            if (!string.Equals(TargetHash(current), stored.TargetSnapshotHash, StringComparison.Ordinal))
            {
                SetStatus(unitOfWork, confirmationId, "AwaitingConfirmation", "Stale", now);
                return Terminal(stored, preview, AssistantPlanPipelineState.Stale, "target_version_changed");
            }
            if (!SetStatus(unitOfWork, confirmationId, "AwaitingConfirmation", "Executing", now))
                throw new InvalidOperationException("Plan confirmation was concurrently consumed.");

            var results = new List<CommandHandlerResult>(commands.Count);
            ExecuteSavepoint(unitOfWork, "SAVEPOINT assistant_plan_v2");
            try
            {
                for (var index = 0; index < commands.Count; index++)
                {
                    var targets = ReadExecutionTargets(unitOfWork, current, index);
                    var result = CanonicalCommandHandlers.Execute(
                        new AssistantCommandExecutionContext(unitOfWork, now, targets, occurrenceResolver, timeZones),
                        commands[index]);
                    if (!result.Succeeded)
                        throw new AssistantPlanExecutionException(result.Code, result.ResultJson);
                    results.Add(result);
                }
                ExecuteSavepoint(unitOfWork, "RELEASE SAVEPOINT assistant_plan_v2");
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                               TimeZoneNotFoundException or AssistantPlanExecutionException or
                                               Microsoft.Data.Sqlite.SqliteException)
            {
                ExecuteSavepoint(unitOfWork, "ROLLBACK TO SAVEPOINT assistant_plan_v2");
                ExecuteSavepoint(unitOfWork, "RELEASE SAVEPOINT assistant_plan_v2");
                var code = exception is AssistantPlanExecutionException planError
                    ? planError.Code
                    : exception is CanonicalConcurrencyException ? "concurrency_conflict" : "transaction_rollback";
                var failure = JsonSerializer.Serialize(new { code, error = exception.Message });
                SetResultAndStatus(unitOfWork, confirmationId, "Failed", failure, now);
                AssistantAiDiagnosticsV2.Write("execute", code, null, commands.Count, TimeSpan.Zero, failure);
                return new(AssistantPlanPipelineState.Failed, stored.PlanId, confirmationId,
                    code, preview, [], failure);
            }

            var resultJson = JsonSerializer.Serialize(new
            {
                planId = stored.PlanId,
                steps = results.Select((result, index) => new
                {
                    stepIndex = index,
                    code = result.Code,
                    itemIds = result.ItemIds,
                    result = JsonNode.Parse(result.ResultJson)
                })
            });
            SetResultAndStatus(unitOfWork, confirmationId, "Succeeded", resultJson, now);
            var itemIds = results.SelectMany(result => result.ItemIds).Distinct(StringComparer.Ordinal).ToArray();
            AssistantAiDiagnosticsV2.Write("execute", "ok", null, commands.Count, TimeSpan.Zero, resultJson);
            return new(AssistantPlanPipelineState.Succeeded, stored.PlanId, confirmationId,
                "plan_succeeded", preview, itemIds, resultJson);
        }, cancellationToken);
    }

    public AssistantPlanPipelineResultV2 CancelPlan(
        string confirmationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationId);
        var now = utcNow().ToUniversalTime();
        return writeQueue.Execute<AssistantPlanPipelineResultV2>(unitOfWork =>
        {
            var stored = Read(unitOfWork, confirmationId)
                ?? throw new KeyNotFoundException("Plan confirmation does not exist.");
            var preview = JsonSerializer.Deserialize<AssistantPlanPreviewStepV2[]>(stored.PreviewJson) ?? [];
            if (stored.Status == "AwaitingConfirmation")
                SetStatus(unitOfWork, confirmationId, "AwaitingConfirmation", "Cancelled", now);
            return Terminal(stored, preview, AssistantPlanPipelineState.Cancelled, "plan_cancelled");
        }, cancellationToken);
    }

    AssistantPlanPipelineResultV2 Preflight(
        string sourceText,
        IReadOnlyList<AssistantCommandEnvelope> commands,
        IReadOnlyList<AssistantPlanCandidateBindingV2> candidateBindings,
        CancellationToken cancellationToken,
        bool forceConfirmation = false,
        string? draftConfirmationId = null)
    {
        var now = utcNow().ToUniversalTime();
        return writeQueue.Execute<AssistantPlanPipelineResultV2>(unitOfWork =>
        {
            if (draftConfirmationId is not null && Read(unitOfWork, draftConfirmationId) is { } existing)
            {
                if (!string.Equals(existing.PlanHash, Hash(AssistantCommandPlanJsonV2.Serialize(commands)), StringComparison.Ordinal))
                    throw new InvalidOperationException("The prepared draft has changed.");
                return Terminal(existing, JsonSerializer.Deserialize<AssistantPlanPreviewStepV2[]>(existing.PreviewJson) ?? [],
                    StatusState(existing.Status), "idempotent_replay");
            }
            var allTargets = new List<AssistantPlanTargetSnapshotV2>();
            var preview = new List<AssistantPlanPreviewStepV2>(commands.Count);
            var needsConfirmation = forceConfirmation;
            for (var index = 0; index < commands.Count; index++)
            {
                var command = commands[index];
                var targets = ResolveTargets(unitOfWork, command, candidateBindings);
                allTargets.AddRange(targets.Select(target =>
                    new AssistantPlanTargetSnapshotV2(index, target.ItemId, target.RowVersion)));
                var facts = new AssistantLocalExecutionFacts(
                    ProposedItemCount: commands.Count,
                    TargetMatchCount: RequiresTarget(command.Command) ? targets.Count : null,
                    HasScheduleConflict: HasEventConflict(unitOfWork, command, now));
                var decision = AssistantExecutionPolicy.Evaluate(command, sourceText, facts);
                preview.Add(Preview(unitOfWork, index, command, targets, decision.Reason));
                if (decision.Disposition == AssistantExecutionDisposition.RequireClarification)
                    return new(AssistantPlanPipelineState.ClarificationRequired, NewId("plan"), null,
                        decision.Reason, preview, [], null);
                needsConfirmation |= decision.Disposition == AssistantExecutionDisposition.RequireConfirmation;
            }
            if (!needsConfirmation && draftConfirmationId is null)
                return new(AssistantPlanPipelineState.Succeeded, NewId("plan"), null,
                    "preflight_clear", preview, [], null);

            var planId = NewId("plan");
            var confirmationId = draftConfirmationId ?? NewId("confirm");
            var planJson = AssistantCommandPlanJsonV2.Serialize(commands);
            var targetJson = JsonSerializer.Serialize(allTargets);
            var targetHash = TargetHash(allTargets);
            var previewJson = JsonSerializer.Serialize(preview);
            Insert(unitOfWork, confirmationId, planId, Hash(planJson), planJson, targetHash, targetJson,
                previewJson, now.AddMinutes(15), now);
            return new(AssistantPlanPipelineState.AwaitingConfirmation, planId, confirmationId,
                !needsConfirmation ? "ready_to_execute" : commands.Count > 1 ? "multi_write_confirmation" : "risk_confirmation",
                preview, allTargets.Select(target => target.ItemId).Distinct(StringComparer.Ordinal).ToArray(), null);
        }, cancellationToken);
    }

    AssistantPlanPreviewStepV2 Preview(
        IUnitOfWork unitOfWork,
        int index,
        AssistantCommandEnvelope command,
        IReadOnlyList<TargetRowVersion> targets,
        string reason)
    {
        var names = new List<string>();
        foreach (var target in targets)
        {
            using var query = unitOfWork.Connection.CreateCommand();
            query.Transaction = unitOfWork.Transaction;
            query.CommandText = "SELECT title,COALESCE(item_type,kind) FROM life_items WHERE id=$id";
            query.Parameters.AddWithValue("$id", target.ItemId);
            using var reader = query.ExecuteReader();
            if (reader.Read()) names.Add($"{reader.GetString(0)}（{reader.GetString(1)}）");
        }
        var operation = AssistantCommandEnvelopeJson.CommandName(command.Command);
        var description = names.Count == 0
            ? $"{operation}：{CreateDescription(command)}"
            : $"{operation}：{string.Join("、", names)}";
        if (!string.IsNullOrWhiteSpace(reason)) description += $" · {reason}";
        return new(index, operation, description, names);
    }

    static string CreateDescription(AssistantCommandEnvelope command) => command.Arguments switch
    {
        CreateTodoArgumentsV1 value => value.Title ?? "未命名待办",
        CreateReminderArgumentsV1 value => value.Title ?? "未命名提醒",
        CreateEventArgumentsV1 value => value.Title ?? "未命名事件",
        CreateLongTermItemArgumentsV1 value => value.Title ?? "未命名长期事项",
        CreateRecurringTaskArgumentsV1 value => value.Title ?? "未命名周期任务",
        DecomposeGoalArgumentsV1 value => value.Goal ?? "未命名目标",
        _ => "待执行操作"
    };

    static AssistantPlanPipelineResultV2 FromSingle(
        AssistantCommandPipelineResult result,
        IReadOnlyList<AssistantPlanPreviewStepV2> preview) => new(
        result.State switch
        {
            AssistantCommandPipelineState.Succeeded => AssistantPlanPipelineState.Succeeded,
            AssistantCommandPipelineState.AwaitingConfirmation => AssistantPlanPipelineState.AwaitingConfirmation,
            AssistantCommandPipelineState.ClarificationRequired => AssistantPlanPipelineState.ClarificationRequired,
            AssistantCommandPipelineState.Cancelled => AssistantPlanPipelineState.Cancelled,
            AssistantCommandPipelineState.Expired => AssistantPlanPipelineState.Expired,
            AssistantCommandPipelineState.Stale => AssistantPlanPipelineState.Stale,
            _ => AssistantPlanPipelineState.Failed
        },
        result.UserRequestId,
        result.ConfirmationId,
        result.Code,
        preview,
        result.ItemIds,
        result.ResultJson);

    IReadOnlyList<TargetRowVersion> ResolveTargets(IUnitOfWork unitOfWork, AssistantCommandEnvelope envelope,
        IReadOnlyList<AssistantPlanCandidateBindingV2> candidateBindings)
    {
        var selector = envelope.Arguments switch
        {
            UpdateTodoArgumentsV1 value => value.Target,
            CompleteTodoArgumentsV1 value => value.Target,
            DeleteTodoArgumentsV1 value => value.Target,
            RescheduleItemArgumentsV1 value => value.Target,
            _ => null
        };
        if (selector is null) return [];
        if (!string.IsNullOrWhiteSpace(selector.CandidateRef))
        {
            var binding = candidateBindings.SingleOrDefault(candidate =>
                string.Equals(candidate.CandidateRef, selector.CandidateRef, StringComparison.Ordinal));
            if (binding is null) return [];
            using var byReference = unitOfWork.Connection.CreateCommand();
            byReference.Transaction = unitOfWork.Transaction;
            byReference.CommandText = """
                SELECT row_version FROM life_items
                WHERE id=$id AND row_version=$version AND deleted_at IS NULL
                """;
            byReference.Parameters.AddWithValue("$id", binding.ItemId);
            byReference.Parameters.AddWithValue("$version", binding.RowVersion);
            var currentVersion = byReference.ExecuteScalar();
            return currentVersion is null
                ? []
                : [new TargetRowVersion(binding.ItemId, Convert.ToInt64(currentVersion))];
        }

        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            SELECT id,row_version FROM life_items
            WHERE deleted_at IS NULL
              AND ($title IS NULL OR title=$title COLLATE NOCASE)
              AND ($kind IS NULL
                   OR ($kind='LongTerm' AND item_type='LongTerm')
                   OR ($kind<>'LongTerm' AND kind=$kind AND item_type IS NULL))
            ORDER BY updated_at DESC LIMIT 20
            """;
        command.Parameters.AddWithValue("$title",
            string.IsNullOrWhiteSpace(selector.Title) ? DBNull.Value : selector.Title.Trim());
        command.Parameters.AddWithValue("$kind", selector.Kind is null ? DBNull.Value : selector.Kind.ToString());
        using var reader = command.ExecuteReader();
        var values = new List<TargetRowVersion>();
        while (reader.Read()) values.Add(new(reader.GetString(0), reader.GetInt64(1)));
        return values;
    }

    static IReadOnlyList<AssistantPlanTargetSnapshotV2> ReadCurrentTargets(
        IUnitOfWork unitOfWork,
        IReadOnlyList<AssistantPlanTargetSnapshotV2> expected)
    {
        var values = new List<AssistantPlanTargetSnapshotV2>();
        foreach (var target in expected)
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = "SELECT row_version FROM life_items WHERE id=$id AND deleted_at IS NULL";
            command.Parameters.AddWithValue("$id", target.ItemId);
            var value = command.ExecuteScalar();
            if (value is not null)
                values.Add(new(target.StepIndex, target.ItemId, Convert.ToInt64(value)));
        }
        return values;
    }
    static IReadOnlyList<TargetRowVersion> ReadExecutionTargets(
        IUnitOfWork unitOfWork,
        IReadOnlyList<AssistantPlanTargetSnapshotV2> snapshots,
        int stepIndex)
    {
        var values = new List<TargetRowVersion>();
        foreach (var itemId in snapshots.Where(target => target.StepIndex == stepIndex)
                     .Select(target => target.ItemId).Distinct(StringComparer.Ordinal))
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = "SELECT row_version FROM life_items WHERE id=$id AND deleted_at IS NULL";
            command.Parameters.AddWithValue("$id", itemId);
            var value = command.ExecuteScalar();
            if (value is not null) values.Add(new(itemId, Convert.ToInt64(value)));
        }
        return values;
    }

    bool HasEventConflict(IUnitOfWork unitOfWork, AssistantCommandEnvelope envelope, DateTimeOffset now)
    {
        if (envelope.Arguments is not CreateEventArgumentsV1 value || value.Start is null || value.End is null)
            return false;
        try
        {
            var context = new AssistantCommandExecutionContext(unitOfWork, now, [], occurrenceResolver, timeZones);
            var start = AssistantCommandTimeResolver.Resolve(context, value.Start).UtcInstant;
            var end = AssistantCommandTimeResolver.Resolve(context, value.End).UtcInstant;
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = """
                SELECT COUNT(*) FROM life_items
                WHERE kind='Event' AND deleted_at IS NULL AND status NOT IN ('Completed','Cancelled','Ignored')
                  AND start_utc_instant < $end AND end_utc_instant > $start
                """;
            command.Parameters.AddWithValue("$start", start.ToString("O"));
            command.Parameters.AddWithValue("$end", end.ToString("O"));
            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }
        catch (InvalidOperationException) { return false; }
    }

    void EnsureCreatedAndInvalidateV1()
    {
        writeQueue.Execute(unitOfWork =>
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS assistant_plan_confirmations_v2(
                    confirmation_id TEXT PRIMARY KEY,
                    plan_id TEXT NOT NULL UNIQUE,
                    plan_hash TEXT NOT NULL,
                    plan_json TEXT NOT NULL,
                    target_snapshot_hash TEXT NOT NULL,
                    target_snapshot_json TEXT NOT NULL,
                    preview_json TEXT NOT NULL,
                    status TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    result_json TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_assistant_plan_confirmations_v2_status
                    ON assistant_plan_confirmations_v2(status,expires_at);
                CREATE TABLE IF NOT EXISTS assistant_protocol_state(
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    updated_at TEXT NOT NULL);
                """;
            command.ExecuteNonQuery();

            using var exists = unitOfWork.Connection.CreateCommand();
            exists.Transaction = unitOfWork.Transaction;
            exists.CommandText = "SELECT COUNT(*) FROM assistant_protocol_state WHERE key='v2_cutover_completed'";
            if (Convert.ToInt64(exists.ExecuteScalar()) != 0) return 0;

            using var invalidate = unitOfWork.Connection.CreateCommand();
            invalidate.Transaction = unitOfWork.Transaction;
            invalidate.CommandText = """
                UPDATE assistant_confirmations
                SET status='Stale',updated_at=$now
                WHERE status IN ('AwaitingConfirmation','Executing');
                UPDATE assistant_actions
                SET status='superseded',error_message='assistant_protocol_v2_cutover',updated_at=$localNow
                WHERE status IN ('awaiting_confirmation','clarifying','holiday_batch_time_pending');
                INSERT INTO assistant_protocol_state(key,value,updated_at)
                VALUES('v2_cutover_completed','2',$now);
                """;
            invalidate.Parameters.AddWithValue("$now", utcNow().ToUniversalTime().ToString("O"));
            invalidate.Parameters.AddWithValue("$localNow", DateTime.Now.ToString("O"));
            invalidate.ExecuteNonQuery();
            return 0;
        });
    }

    static void Insert(
        IUnitOfWork unitOfWork,
        string confirmationId,
        string planId,
        string planHash,
        string planJson,
        string targetHash,
        string targetJson,
        string previewJson,
        DateTimeOffset expiresAt,
        DateTimeOffset now)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            INSERT INTO assistant_plan_confirmations_v2(
                confirmation_id,plan_id,plan_hash,plan_json,target_snapshot_hash,target_snapshot_json,
                preview_json,status,expires_at,result_json,created_at,updated_at)
            VALUES($confirmation,$plan,$hash,$json,$targetHash,$targets,$preview,'AwaitingConfirmation',
                $expires,NULL,$now,$now)
            """;
        command.Parameters.AddWithValue("$confirmation", confirmationId);
        command.Parameters.AddWithValue("$plan", planId);
        command.Parameters.AddWithValue("$hash", planHash);
        command.Parameters.AddWithValue("$json", planJson);
        command.Parameters.AddWithValue("$targetHash", targetHash);
        command.Parameters.AddWithValue("$targets", targetJson);
        command.Parameters.AddWithValue("$preview", previewJson);
        command.Parameters.AddWithValue("$expires", expiresAt.ToString("O"));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();
    }

    static StoredAssistantPlanV2? Read(IUnitOfWork unitOfWork, string confirmationId)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            SELECT confirmation_id,plan_id,plan_hash,plan_json,target_snapshot_hash,target_snapshot_json,
                   preview_json,status,expires_at,result_json
            FROM assistant_plan_confirmations_v2 WHERE confirmation_id=$id
            """;
        command.Parameters.AddWithValue("$id", confirmationId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                DateTimeOffset.Parse(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9))
            : null;
    }

    static bool SetStatus(
        IUnitOfWork unitOfWork,
        string confirmationId,
        string expected,
        string next,
        DateTimeOffset now)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            UPDATE assistant_plan_confirmations_v2 SET status=$next,updated_at=$now
            WHERE confirmation_id=$id AND status=$expected
            """;
        command.Parameters.AddWithValue("$id", confirmationId);
        command.Parameters.AddWithValue("$expected", expected);
        command.Parameters.AddWithValue("$next", next);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        return command.ExecuteNonQuery() == 1;
    }

    static void SetResultAndStatus(
        IUnitOfWork unitOfWork,
        string confirmationId,
        string status,
        string resultJson,
        DateTimeOffset now)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            UPDATE assistant_plan_confirmations_v2
            SET status=$status,result_json=$result,updated_at=$now
            WHERE confirmation_id=$id
            """;
        command.Parameters.AddWithValue("$id", confirmationId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$result", resultJson);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();
    }

    static AssistantPlanPipelineResultV2 Terminal(
        StoredAssistantPlanV2 stored,
        IReadOnlyList<AssistantPlanPreviewStepV2> preview,
        AssistantPlanPipelineState state,
        string code) =>
        new(state, stored.PlanId, stored.ConfirmationId, code, preview, ResultItemIds(stored.ResultJson), stored.ResultJson);

    static AssistantPlanPipelineState StatusState(string status) => status switch
    {
        "AwaitingConfirmation" => AssistantPlanPipelineState.AwaitingConfirmation,
        "Succeeded" => AssistantPlanPipelineState.Succeeded,
        "Cancelled" => AssistantPlanPipelineState.Cancelled,
        "Expired" => AssistantPlanPipelineState.Expired,
        "Stale" => AssistantPlanPipelineState.Stale,
        _ => AssistantPlanPipelineState.Failed
    };

    static IReadOnlyList<string> ResultItemIds(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return [];
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (!document.RootElement.TryGetProperty("steps", out var steps)) return [];
            return steps.EnumerateArray()
                .SelectMany(step => step.TryGetProperty("itemIds", out var ids)
                    ? ids.EnumerateArray().Select(value => value.GetString()).Where(value => value is not null)!
                    : [])
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException) { return []; }
    }

    static bool RequiresTarget(AssistantCommandName command) => command is
        AssistantCommandName.UpdateTodo or AssistantCommandName.CompleteTodo or
        AssistantCommandName.DeleteTodo or AssistantCommandName.RescheduleItem;

    static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    static string TargetHash(IEnumerable<AssistantPlanTargetSnapshotV2> targets) =>
        Hash(JsonSerializer.Serialize(targets.OrderBy(target => target.StepIndex).ThenBy(target => target.ItemId)));

    static void ExecuteSavepoint(IUnitOfWork unitOfWork, string sql)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    sealed class AssistantPlanExecutionException(string code, string message) : InvalidOperationException(message)
    {
        public string Code { get; } = code;
    }
}

public static class AssistantCommandPlanJsonV2
{
    public static string Serialize(IReadOnlyList<AssistantCommandEnvelope> commands)
    {
        var array = new JsonArray();
        foreach (var command in commands)
            array.Add(JsonNode.Parse(AssistantCommandEnvelopeJson.Serialize(command)));
        return new JsonObject
        {
            ["schemaVersion"] = 2,
            ["commands"] = array
        }.ToJsonString();
    }

    public static IReadOnlyList<AssistantCommandEnvelope> Deserialize(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Plan must be an object.");
        if (root["schemaVersion"]?.GetValue<int>() != 2)
            throw new JsonException("Plan schemaVersion must be 2.");
        var commands = root["commands"] as JsonArray ?? throw new JsonException("commands must be an array.");
        if (commands.Count is < 1 or > 3)
            throw new JsonException("commands must contain between 1 and 3 values.");
        return commands.Select(command =>
            AssistantCommandEnvelopeJson.Deserialize(command?.ToJsonString() ?? "null")).ToArray();
    }
}
