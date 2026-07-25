using System.Text.Json;
using Microsoft.Data.Sqlite;
using ChronoIsle.App.Services.Domain;
using ChronoIsle.App.Services.Persistence;

namespace ChronoIsle.App.Services.Commanding;

public sealed record AssistantCommandPipelineResult(
    AssistantCommandPipelineState State,
    string UserRequestId,
    string ParseAttemptId,
    string ClientRequestId,
    string? ConfirmationId,
    string Code,
    string? ResultJson,
    IReadOnlyList<string> ItemIds);

/// <summary>
/// Trusted local boundary between a parsed model envelope and database effects. Model and network
/// work happens before this type is called; every identifier used here is generated locally.
/// </summary>
public sealed partial class AssistantCommandPipeline
{
    readonly AssistantCommandStore store;
    readonly IAssistantLocalIdFactory ids;
    readonly Func<DateTimeOffset> utcNow;
    readonly ITimeZoneCatalog timeZones;
    readonly IOccurrenceTimeResolver occurrenceResolver;

    public AssistantCommandPipeline(
        string databasePath,
        IAssistantLocalIdFactory? ids = null,
        Func<DateTimeOffset>? utcNow = null,
        ITimeZoneCatalog? timeZones = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _ = new LifeDataService(databasePath);
        var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(databasePath);
        store = new AssistantCommandStore(runtime.WriteQueue);
        this.ids = ids ?? new GuidAssistantLocalIdFactory();
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.timeZones = timeZones ?? new SystemTimeZoneCatalog();
        occurrenceResolver = new OccurrenceTimeResolver(this.timeZones);
        store.EnsureCreated();
    }

    public AssistantCommandPipeline(
        IDbWriteQueue writeQueue,
        IAssistantLocalIdFactory? ids = null,
        Func<DateTimeOffset>? utcNow = null,
        ITimeZoneCatalog? timeZones = null)
    {
        store = new AssistantCommandStore(writeQueue);
        this.ids = ids ?? new GuidAssistantLocalIdFactory();
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.timeZones = timeZones ?? new SystemTimeZoneCatalog();
        occurrenceResolver = new OccurrenceTimeResolver(this.timeZones);
        store.EnsureCreated();
    }

    public AssistantCommandPipelineResult SubmitParsed(
        string originalUserInput,
        AssistantCommandEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalUserInput);
        ArgumentNullException.ThrowIfNull(envelope);
        AssistantCommandContractValidator.Validate(envelope);

        var userRequestId = ids.Create(AssistantLocalIdKind.UserRequest);
        var parseAttemptId = ids.Create(AssistantLocalIdKind.ParseAttempt);
        var clientRequestId = ids.Create(AssistantLocalIdKind.ClientRequest);
        var actionEventId = ids.Create(AssistantLocalIdKind.ActionEvent);
        var now = utcNow().ToUniversalTime();

        return store.WriteQueue.Execute(unitOfWork =>
        {
            var targets = ResolveTargets(unitOfWork, envelope);
            var snapshot = new TargetVersionSnapshot(targets);
            var facts = new AssistantLocalExecutionFacts(
                ProposedItemCount: 1,
                TargetMatchCount: RequiresTarget(envelope.Command) ? targets.Count : null,
                HasScheduleConflict: HasEventConflict(unitOfWork, envelope, now));
            var decision = AssistantExecutionPolicy.Evaluate(envelope, originalUserInput, facts);

            store.InsertRequestAndParse(unitOfWork, userRequestId, parseAttemptId, originalUserInput, envelope,
                decision.Disposition.ToString(), now);

            if (decision.Disposition == AssistantExecutionDisposition.RequireClarification)
            {
                store.SetRequestStatus(unitOfWork, userRequestId, "ClarificationRequired", now);
                return new AssistantCommandPipelineResult(
                    AssistantCommandPipelineState.ClarificationRequired, userRequestId, parseAttemptId,
                    clientRequestId, null, decision.Reason, null, []);
            }

            if (decision.Disposition == AssistantExecutionDisposition.RequireConfirmation)
            {
                var confirmation = ConfirmationRecord.Create(
                    ids, clientRequestId, envelope, snapshot, now,
                    JsonSerializer.Serialize(new
                    {
                        command = AssistantCommandEnvelopeJson.CommandName(envelope.Command),
                        targets = snapshot.Targets
                    }));
                store.InsertConfirmation(unitOfWork, confirmation, userRequestId, parseAttemptId, snapshot, envelope, now);
                store.SetRequestStatus(unitOfWork, userRequestId, "AwaitingConfirmation", now);
                return new AssistantCommandPipelineResult(
                    AssistantCommandPipelineState.AwaitingConfirmation, userRequestId, parseAttemptId,
                    clientRequestId, confirmation.ConfirmationId, decision.Reason, null,
                    targets.Select(value => value.ItemId).ToArray());
            }

            return ExecuteWithAudit(
                unitOfWork, envelope, originalUserInput, userRequestId, parseAttemptId, clientRequestId,
                actionEventId, null, targets, now);
        }, cancellationToken);
    }

    public AssistantCommandPipelineResult Confirm(string confirmationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationId);
        var now = utcNow().ToUniversalTime();
        return store.WriteQueue.Execute(unitOfWork =>
        {
            var stored = store.ReadConfirmation(unitOfWork, confirmationId)
                ?? throw new KeyNotFoundException("Confirmation does not exist.");
            var priorResult = store.FindActionResult(unitOfWork, stored.ClientRequestId);
            if (stored.Status == "Succeeded" && priorResult is not null)
                return new AssistantCommandPipelineResult(
                    AssistantCommandPipelineState.Succeeded, stored.UserRequestId, stored.ParseAttemptId,
                    stored.ClientRequestId, stored.ConfirmationId, "idempotent_replay", priorResult,
                    ResultItemIds(priorResult));

            if (stored.Status != "AwaitingConfirmation")
                return ExistingTerminalResult(stored);

            if (now >= stored.ExpiresAtUtc)
            {
                store.TrySetConfirmationStatus(unitOfWork, confirmationId, "AwaitingConfirmation", "Expired", now);
                store.SetRequestStatus(unitOfWork, stored.UserRequestId, "Expired", now);
                return Terminal(stored, AssistantCommandPipelineState.Expired, "confirmation_expired");
            }

            AssistantCommandEnvelope envelope;
            try { envelope = AssistantCommandEnvelopeJson.Deserialize(stored.EnvelopeJson); }
            catch (AssistantCommandContractException)
            {
                store.TrySetConfirmationStatus(unitOfWork, confirmationId, "AwaitingConfirmation", "Stale", now);
                store.SetRequestStatus(unitOfWork, stored.UserRequestId, "Stale", now);
                return Terminal(stored, AssistantCommandPipelineState.Stale, "command_schema_stale");
            }
            if (!AssistantCommandHash.Matches(envelope, stored.CommandHash))
            {
                store.TrySetConfirmationStatus(unitOfWork, confirmationId, "AwaitingConfirmation", "Stale", now);
                store.SetRequestStatus(unitOfWork, stored.UserRequestId, "Stale", now);
                return Terminal(stored, AssistantCommandPipelineState.Stale, "command_hash_changed");
            }

            var expectedTargets = JsonSerializer.Deserialize<TargetRowVersion[]>(stored.TargetSnapshotJson) ?? [];
            var currentTargets = ReadCurrentTargets(unitOfWork, expectedTargets);
            var currentSnapshot = new TargetVersionSnapshot(currentTargets);
            if (!string.Equals(currentSnapshot.Hash, stored.TargetSnapshotHash, StringComparison.Ordinal))
            {
                store.TrySetConfirmationStatus(unitOfWork, confirmationId, "AwaitingConfirmation", "Stale", now);
                store.SetRequestStatus(unitOfWork, stored.UserRequestId, "Stale", now);
                return Terminal(stored, AssistantCommandPipelineState.Stale, "target_version_changed");
            }

            if (!store.TrySetConfirmationStatus(unitOfWork, confirmationId, "AwaitingConfirmation", "Executing", now, true))
                throw new InvalidOperationException("Confirmation was concurrently consumed.");

            var result = ExecuteWithAudit(
                unitOfWork, envelope, "[confirmed command]", stored.UserRequestId, stored.ParseAttemptId,
                stored.ClientRequestId, ids.Create(AssistantLocalIdKind.ActionEvent), confirmationId,
                currentTargets, now);
            store.TrySetConfirmationStatus(unitOfWork, confirmationId, "Executing",
                result.State == AssistantCommandPipelineState.Succeeded ? "Succeeded" : "Failed", now);
            return result;
        }, cancellationToken);
    }

    public int RedactAuditOlderThan30Days() => store.RedactDetailedAudit(utcNow().ToUniversalTime().AddDays(-30));
    public int ClearLocalDetailedAudit() => store.ClearDetailedAudit();

    AssistantCommandPipelineResult ExecuteWithAudit(
        IUnitOfWork unitOfWork,
        AssistantCommandEnvelope envelope,
        string sourceText,
        string userRequestId,
        string parseAttemptId,
        string clientRequestId,
        string actionEventId,
        string? confirmationId,
        IReadOnlyList<TargetRowVersion> targets,
        DateTimeOffset now)
    {
        CommandHandlerResult handlerResult;
        string? failureMessage = null;
        CreateSavepoint(unitOfWork, "assistant_command");
        try
        {
            handlerResult = CanonicalCommandHandlers.Execute(
                new AssistantCommandExecutionContext(unitOfWork, now, targets, occurrenceResolver, timeZones), envelope);
            ReleaseSavepoint(unitOfWork, "assistant_command");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or TimeZoneNotFoundException)
        {
            RollbackSavepoint(unitOfWork, "assistant_command");
            failureMessage = exception.Message;
            handlerResult = new(false,
                exception is CanonicalConcurrencyException ? "concurrency_conflict" : "validation_failed",
                JsonSerializer.Serialize(new { error = exception.Message }), []);
        }

        var succeeded = handlerResult.Succeeded;
        store.InsertActionEvent(
            unitOfWork, actionEventId, userRequestId, parseAttemptId, clientRequestId, confirmationId,
            envelope, sourceText, succeeded ? "Succeeded" : "Failed", handlerResult.ResultJson,
            succeeded ? null : handlerResult.Code, failureMessage, now);
        store.SetRequestStatus(unitOfWork, userRequestId, succeeded ? "Succeeded" : "Failed", now);
        return new AssistantCommandPipelineResult(
            succeeded ? AssistantCommandPipelineState.Succeeded : AssistantCommandPipelineState.Failed,
            userRequestId, parseAttemptId, clientRequestId, confirmationId,
            handlerResult.Code, handlerResult.ResultJson, handlerResult.ItemIds);
    }

    static IReadOnlyList<TargetRowVersion> ResolveTargets(IUnitOfWork unitOfWork, AssistantCommandEnvelope envelope)
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
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = """
            SELECT id,row_version FROM life_items
            WHERE deleted_at IS NULL
              AND ($title IS NULL OR title=$title COLLATE NOCASE)
              AND ($kind IS NULL OR kind=$kind)
            ORDER BY updated_at DESC LIMIT 20
            """;
        command.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(selector.Title) ? DBNull.Value : selector.Title.Trim());
        command.Parameters.AddWithValue("$kind", selector.Kind is null ? DBNull.Value : selector.Kind.ToString());
        using var reader = command.ExecuteReader();
        var values = new List<TargetRowVersion>();
        while (reader.Read()) values.Add(new(reader.GetString(0), reader.GetInt64(1)));
        return values;
    }

    static IReadOnlyList<TargetRowVersion> ReadCurrentTargets(IUnitOfWork unitOfWork, IReadOnlyList<TargetRowVersion> expected)
    {
        var values = new List<TargetRowVersion>();
        foreach (var target in expected)
        {
            using var command = unitOfWork.Connection.CreateCommand();
            command.Transaction = unitOfWork.Transaction;
            command.CommandText = "SELECT row_version FROM life_items WHERE id=$id AND deleted_at IS NULL";
            command.Parameters.AddWithValue("$id", target.ItemId);
            var value = command.ExecuteScalar();
            if (value is not null) values.Add(new(target.ItemId, Convert.ToInt64(value)));
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
                WHERE kind='Event' AND deleted_at IS NULL
                  AND start_utc_instant < $end AND end_utc_instant > $start
                """;
            command.Parameters.AddWithValue("$start", start.ToString("O"));
            command.Parameters.AddWithValue("$end", end.ToString("O"));
            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }
        catch (InvalidOperationException) { return false; }
    }

    static bool RequiresTarget(AssistantCommandName command) => command is
        AssistantCommandName.UpdateTodo or AssistantCommandName.CompleteTodo or
        AssistantCommandName.DeleteTodo or AssistantCommandName.RescheduleItem;

    static AssistantCommandPipelineResult ExistingTerminalResult(StoredAssistantConfirmation stored)
    {
        var state = stored.Status switch
        {
            "Expired" => AssistantCommandPipelineState.Expired,
            "Stale" => AssistantCommandPipelineState.Stale,
            "Cancelled" => AssistantCommandPipelineState.Cancelled,
            "Failed" => AssistantCommandPipelineState.Failed,
            "Succeeded" => AssistantCommandPipelineState.Succeeded,
            _ => AssistantCommandPipelineState.Failed
        };
        return Terminal(stored, state, "confirmation_not_awaiting");
    }

    static AssistantCommandPipelineResult Terminal(
        StoredAssistantConfirmation stored,
        AssistantCommandPipelineState state,
        string code) => new(
            state, stored.UserRequestId, stored.ParseAttemptId, stored.ClientRequestId,
            stored.ConfirmationId, code, null, []);

    static IReadOnlyList<string> ResultItemIds(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.TryGetProperty("itemId", out var value) && value.ValueKind == JsonValueKind.String
                ? [value.GetString()!]
                : [];
        }
        catch (JsonException) { return []; }
    }

    static void CreateSavepoint(IUnitOfWork unitOfWork, string name) => ExecuteSavepoint(unitOfWork, $"SAVEPOINT {name}");
    static void ReleaseSavepoint(IUnitOfWork unitOfWork, string name) => ExecuteSavepoint(unitOfWork, $"RELEASE SAVEPOINT {name}");
    static void RollbackSavepoint(IUnitOfWork unitOfWork, string name)
    {
        ExecuteSavepoint(unitOfWork, $"ROLLBACK TO SAVEPOINT {name}");
        ExecuteSavepoint(unitOfWork, $"RELEASE SAVEPOINT {name}");
    }

    static void ExecuteSavepoint(IUnitOfWork unitOfWork, string sql)
    {
        using var command = unitOfWork.Connection.CreateCommand();
        command.Transaction = unitOfWork.Transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
