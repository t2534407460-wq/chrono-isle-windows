namespace ChronoIsle.App.Services.Commanding;

public enum AssistantCommandPipelineState
{
    Received,
    Parsing,
    Rejected,
    ClarificationRequired,
    PolicyReady,
    AutoExecuting,
    AwaitingConfirmation,
    Executing,
    Succeeded,
    Failed,
    Cancelled,
    Expired,
    Stale
}

public static class AssistantCommandStateMachine
{
    public static bool CanTransition(AssistantCommandPipelineState current, AssistantCommandPipelineState next) => current switch
    {
        AssistantCommandPipelineState.Received => next == AssistantCommandPipelineState.Parsing,
        AssistantCommandPipelineState.Parsing => next is AssistantCommandPipelineState.Rejected or
            AssistantCommandPipelineState.ClarificationRequired or AssistantCommandPipelineState.PolicyReady,
        AssistantCommandPipelineState.PolicyReady => next is AssistantCommandPipelineState.AutoExecuting or
            AssistantCommandPipelineState.AwaitingConfirmation,
        AssistantCommandPipelineState.AutoExecuting => next == AssistantCommandPipelineState.Executing,
        AssistantCommandPipelineState.AwaitingConfirmation => next is AssistantCommandPipelineState.Executing or
            AssistantCommandPipelineState.Cancelled or AssistantCommandPipelineState.Expired or AssistantCommandPipelineState.Stale,
        AssistantCommandPipelineState.Executing => next is AssistantCommandPipelineState.Succeeded or AssistantCommandPipelineState.Failed,
        _ => false
    };

    public static bool IsTerminal(AssistantCommandPipelineState state) => state is
        AssistantCommandPipelineState.Rejected or AssistantCommandPipelineState.ClarificationRequired or
        AssistantCommandPipelineState.Succeeded or AssistantCommandPipelineState.Failed or
        AssistantCommandPipelineState.Cancelled or AssistantCommandPipelineState.Expired or AssistantCommandPipelineState.Stale;
}

public sealed record ConfirmationRecord
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);

    public string ConfirmationId { get; }
    public string ClientRequestId { get; }
    public string CommandHash { get; }
    public string TargetSnapshotHash { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public AssistantCommandPipelineState Status { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? ConfirmedAtUtc { get; }
    public string DisplaySnapshot { get; }

    ConfirmationRecord(
        string confirmationId,
        string clientRequestId,
        string commandHash,
        string targetSnapshotHash,
        DateTimeOffset expiresAtUtc,
        AssistantCommandPipelineState status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? confirmedAtUtc,
        string displaySnapshot)
    {
        if (!ValidLocalId(confirmationId, "cf_")) throw new ArgumentException("Invalid local confirmation ID.", nameof(confirmationId));
        if (!ValidLocalId(clientRequestId, "cr_")) throw new ArgumentException("Invalid local client request ID.", nameof(clientRequestId));
        if (!ValidHash(commandHash)) throw new ArgumentException("Invalid command hash.", nameof(commandHash));
        if (!ValidHash(targetSnapshotHash)) throw new ArgumentException("Invalid target snapshot hash.", nameof(targetSnapshotHash));
        if (string.IsNullOrWhiteSpace(displaySnapshot) || displaySnapshot.Length > 4000)
            throw new ArgumentException("Display snapshot must contain 1 to 4000 characters.", nameof(displaySnapshot));

        var createdUtc = createdAtUtc.ToUniversalTime();
        var expiresUtc = expiresAtUtc.ToUniversalTime();
        var confirmedUtc = confirmedAtUtc?.ToUniversalTime();
        if (expiresUtc <= createdUtc) throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        if (confirmedUtc < createdUtc) throw new ArgumentOutOfRangeException(nameof(confirmedAtUtc));
        if (status == AssistantCommandPipelineState.AwaitingConfirmation && confirmedUtc is not null)
            throw new ArgumentException("Awaiting confirmation cannot already have ConfirmedAtUtc.", nameof(confirmedAtUtc));
        if (status is AssistantCommandPipelineState.Executing or AssistantCommandPipelineState.Succeeded or AssistantCommandPipelineState.Failed && confirmedUtc is null)
            throw new ArgumentException("ConfirmedAtUtc is required after confirmation.", nameof(confirmedAtUtc));
        if (status is not (AssistantCommandPipelineState.AwaitingConfirmation or AssistantCommandPipelineState.Executing or
            AssistantCommandPipelineState.Succeeded or AssistantCommandPipelineState.Failed or AssistantCommandPipelineState.Cancelled or
            AssistantCommandPipelineState.Expired or AssistantCommandPipelineState.Stale))
            throw new ArgumentOutOfRangeException(nameof(status), "Status is not valid for a confirmation record.");

        ConfirmationId = confirmationId;
        ClientRequestId = clientRequestId;
        CommandHash = commandHash;
        TargetSnapshotHash = targetSnapshotHash;
        ExpiresAtUtc = expiresUtc;
        Status = status;
        CreatedAtUtc = createdUtc;
        ConfirmedAtUtc = confirmedUtc;
        DisplaySnapshot = displaySnapshot;
    }

    public static ConfirmationRecord Create(
        IAssistantLocalIdFactory ids,
        string clientRequestId,
        AssistantCommandEnvelope command,
        TargetVersionSnapshot targets,
        DateTimeOffset now,
        string displaySnapshot,
        TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(targets);
        var ttl = lifetime ?? DefaultLifetime;
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        var utc = now.ToUniversalTime();
        DateTimeOffset expires;
        try { expires = utc.Add(ttl); }
        catch (ArgumentOutOfRangeException) { throw new ArgumentOutOfRangeException(nameof(lifetime)); }
        return new(
            ids.Create(AssistantLocalIdKind.Confirmation),
            clientRequestId,
            AssistantCommandHash.Compute(command),
            targets.Hash,
            expires,
            AssistantCommandPipelineState.AwaitingConfirmation,
            utc,
            null,
            displaySnapshot);
    }

    public bool Matches(AssistantCommandEnvelope command, TargetVersionSnapshot targets) =>
        AssistantCommandHash.Matches(command, CommandHash) && FixedHashEquals(targets.Hash, TargetSnapshotHash);

    public void EnsureCanExecute(
        AssistantCommandEnvelope command,
        TargetVersionSnapshot currentTargets,
        DateTimeOffset now)
    {
        if (Status != AssistantCommandPipelineState.AwaitingConfirmation)
            throw new InvalidOperationException("Confirmation is no longer awaiting execution.");
        if (now.ToUniversalTime() >= ExpiresAtUtc)
            throw new InvalidOperationException("Confirmation has expired.");
        if (!AssistantCommandHash.Matches(command, CommandHash))
            throw new InvalidOperationException("Command hash no longer matches.");
        if (!FixedHashEquals(currentTargets.Hash, TargetSnapshotHash))
            throw new InvalidOperationException("Target snapshot is stale.");
    }

    public ConfirmationRecord TransitionTo(AssistantCommandPipelineState next, DateTimeOffset now)
    {
        if (!AssistantCommandStateMachine.CanTransition(Status, next))
            throw new InvalidOperationException($"Illegal confirmation transition: {Status} -> {next}.");
        var utc = now.ToUniversalTime();
        if (utc < CreatedAtUtc) throw new InvalidOperationException("Confirmation cannot move backward in time.");
        if (Status == AssistantCommandPipelineState.AwaitingConfirmation &&
            utc >= ExpiresAtUtc && next != AssistantCommandPipelineState.Expired)
            throw new InvalidOperationException("Confirmation has expired.");
        if (next == AssistantCommandPipelineState.Expired && utc < ExpiresAtUtc)
            throw new InvalidOperationException("Confirmation cannot expire early.");

        var confirmedAt = next == AssistantCommandPipelineState.Executing ? utc : ConfirmedAtUtc;
        return new(
            ConfirmationId,
            ClientRequestId,
            CommandHash,
            TargetSnapshotHash,
            ExpiresAtUtc,
            next,
            CreatedAtUtc,
            confirmedAt,
            DisplaySnapshot);
    }

    static bool ValidLocalId(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) && value.Length == prefix.Length + 32 &&
        value[prefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    static bool ValidHash(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    static bool FixedHashEquals(string left, string right)
    {
        if (!ValidHash(left) || !ValidHash(right)) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left), Convert.FromHexString(right));
    }
}
