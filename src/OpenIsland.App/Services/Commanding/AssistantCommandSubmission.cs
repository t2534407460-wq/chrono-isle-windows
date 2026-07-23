using System.Text.Json;
using OpenIsland.App.Services.Persistence;

namespace OpenIsland.App.Services.Commanding;

/// <summary>
/// Locally minted identity for one user submission. The constructor is internal so model output
/// cannot supply or overwrite any pipeline identity. Retrying with this same instance is durable
/// across callers and is resolved by the persisted client-request id.
/// </summary>
public sealed class AssistantCommandSubmission
{
    internal AssistantCommandSubmission(string userRequestId, string parseAttemptId, string clientRequestId)
    {
        UserRequestId = userRequestId;
        ParseAttemptId = parseAttemptId;
        ClientRequestId = clientRequestId;
    }

    public string UserRequestId { get; }
    public string ParseAttemptId { get; }
    public string ClientRequestId { get; }
}

public sealed partial class AssistantCommandPipeline
{
    /// <summary>Call before model parsing; retain this local token when retrying the same user submission.</summary>
    public AssistantCommandSubmission BeginSubmission() => new(
        ids.Create(AssistantLocalIdKind.UserRequest),
        ids.Create(AssistantLocalIdKind.ParseAttempt),
        ids.Create(AssistantLocalIdKind.ClientRequest));

    public AssistantCommandPipelineResult SubmitParsed(
        AssistantCommandSubmission submission,
        string originalUserInput,
        AssistantCommandEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalUserInput);
        ArgumentNullException.ThrowIfNull(envelope);
        AssistantCommandContractValidator.Validate(envelope);
        var now = utcNow().ToUniversalTime();

        return store.WriteQueue.Execute(unitOfWork =>
        {
            var replay = TryReplay(unitOfWork, submission, envelope);
            if (replay is not null) return replay;

            var targets = ResolveTargets(unitOfWork, envelope);
            var snapshot = new TargetVersionSnapshot(targets);
            var facts = new AssistantLocalExecutionFacts(
                ProposedItemCount: 1,
                TargetMatchCount: RequiresTarget(envelope.Command) ? targets.Count : null,
                HasScheduleConflict: HasEventConflict(unitOfWork, envelope, now));
            var decision = AssistantExecutionPolicy.Evaluate(envelope, originalUserInput, facts);
            store.InsertRequestAndParse(
                unitOfWork, submission.UserRequestId, submission.ParseAttemptId, originalUserInput,
                envelope, decision.Disposition.ToString(), now);

            if (decision.Disposition == AssistantExecutionDisposition.RequireClarification)
            {
                store.SetRequestStatus(unitOfWork, submission.UserRequestId, "ClarificationRequired", now);
                return new AssistantCommandPipelineResult(
                    AssistantCommandPipelineState.ClarificationRequired,
                    submission.UserRequestId, submission.ParseAttemptId, submission.ClientRequestId,
                    null, decision.Reason, null, []);
            }

            if (decision.Disposition == AssistantExecutionDisposition.RequireConfirmation)
            {
                var confirmation = ConfirmationRecord.Create(
                    ids, submission.ClientRequestId, envelope, snapshot, now,
                    JsonSerializer.Serialize(new
                    {
                        command = AssistantCommandEnvelopeJson.CommandName(envelope.Command),
                        targets = snapshot.Targets
                    }));
                store.InsertConfirmation(
                    unitOfWork, confirmation, submission.UserRequestId, submission.ParseAttemptId,
                    snapshot, envelope, now);
                store.SetRequestStatus(unitOfWork, submission.UserRequestId, "AwaitingConfirmation", now);
                return new AssistantCommandPipelineResult(
                    AssistantCommandPipelineState.AwaitingConfirmation,
                    submission.UserRequestId, submission.ParseAttemptId, submission.ClientRequestId,
                    confirmation.ConfirmationId, decision.Reason, null,
                    targets.Select(value => value.ItemId).ToArray());
            }

            return ExecuteWithAudit(
                unitOfWork, envelope, originalUserInput, submission.UserRequestId,
                submission.ParseAttemptId, submission.ClientRequestId,
                ids.Create(AssistantLocalIdKind.ActionEvent), null, targets, now);
        }, cancellationToken);
    }

    AssistantCommandPipelineResult? TryReplay(
        IUnitOfWork unitOfWork,
        AssistantCommandSubmission submission,
        AssistantCommandEnvelope envelope)
    {
        using (var action = unitOfWork.Connection.CreateCommand())
        {
            action.Transaction = unitOfWork.Transaction;
            action.CommandText = """
                SELECT status,result_json,command_json FROM action_events WHERE client_request_id=$client
                """;
            action.Parameters.AddWithValue("$client", submission.ClientRequestId);
            using var reader = action.ExecuteReader();
            if (reader.Read())
            {
                if (!reader.IsDBNull(2) && !SameCommand(reader.GetString(2), envelope))
                    return ReplayRejected(submission, "submission_token_payload_changed");
                var succeeded = string.Equals(reader.GetString(0), "Succeeded", StringComparison.Ordinal);
                var json = reader.IsDBNull(1) ? null : reader.GetString(1);
                return new(
                    succeeded ? AssistantCommandPipelineState.Succeeded : AssistantCommandPipelineState.Failed,
                    submission.UserRequestId, submission.ParseAttemptId, submission.ClientRequestId,
                    null, "idempotent_replay", json, json is null ? [] : ResultItemIds(json));
            }
        }

        using (var confirmation = unitOfWork.Connection.CreateCommand())
        {
            confirmation.Transaction = unitOfWork.Transaction;
            confirmation.CommandText = """
                SELECT confirmation_id,command_hash,status,target_snapshot_json
                FROM assistant_confirmations WHERE client_request_id=$client
                """;
            confirmation.Parameters.AddWithValue("$client", submission.ClientRequestId);
            using var reader = confirmation.ExecuteReader();
            if (reader.Read())
            {
                if (!AssistantCommandHash.Matches(envelope, reader.GetString(1)))
                    return ReplayRejected(submission, "submission_token_payload_changed");
                var state = reader.GetString(2) switch
                {
                    "AwaitingConfirmation" => AssistantCommandPipelineState.AwaitingConfirmation,
                    "Succeeded" => AssistantCommandPipelineState.Succeeded,
                    "Expired" => AssistantCommandPipelineState.Expired,
                    "Stale" => AssistantCommandPipelineState.Stale,
                    "Cancelled" => AssistantCommandPipelineState.Cancelled,
                    _ => AssistantCommandPipelineState.Failed
                };
                var targets = JsonSerializer.Deserialize<TargetRowVersion[]>(reader.GetString(3)) ?? [];
                return new(
                    state, submission.UserRequestId, submission.ParseAttemptId, submission.ClientRequestId,
                    reader.GetString(0), "idempotent_replay", null,
                    targets.Select(value => value.ItemId).ToArray());
            }
        }

        using var request = unitOfWork.Connection.CreateCommand();
        request.Transaction = unitOfWork.Transaction;
        request.CommandText = """
            SELECT u.status,p.envelope_json
            FROM user_requests u JOIN parse_attempts p ON p.user_request_id=u.id
            WHERE u.id=$request AND p.id=$parse
            """;
        request.Parameters.AddWithValue("$request", submission.UserRequestId);
        request.Parameters.AddWithValue("$parse", submission.ParseAttemptId);
        using var requestReader = request.ExecuteReader();
        if (!requestReader.Read()) return null;
        if (!requestReader.IsDBNull(1) && !SameCommand(requestReader.GetString(1), envelope))
            return ReplayRejected(submission, "submission_token_payload_changed");
        return new(
            AssistantCommandPipelineState.ClarificationRequired,
            submission.UserRequestId, submission.ParseAttemptId, submission.ClientRequestId,
            null, "idempotent_replay", null, []);
    }

    static AssistantCommandPipelineResult ReplayRejected(AssistantCommandSubmission submission, string code) => new(
        AssistantCommandPipelineState.Rejected,
        submission.UserRequestId, submission.ParseAttemptId, submission.ClientRequestId,
        null, code, null, []);

    static bool SameCommand(string storedJson, AssistantCommandEnvelope envelope)
    {
        try
        {
            var stored = AssistantCommandEnvelopeJson.Deserialize(storedJson);
            return string.Equals(
                AssistantCommandHash.Compute(stored),
                AssistantCommandHash.Compute(envelope),
                StringComparison.Ordinal);
        }
        catch (AssistantCommandContractException) { return false; }
    }
}
