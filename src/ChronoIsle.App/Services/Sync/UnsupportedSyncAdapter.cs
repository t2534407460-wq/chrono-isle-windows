namespace ChronoIsle.App.Services.Sync;

/// <summary>Explicit boundary for a configured provider operation that cannot be represented losslessly.</summary>
public sealed class UnsupportedSyncAdapter : ISyncAdapter
{
    readonly string reason;

    public UnsupportedSyncAdapter(SyncProvider provider, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Provider = provider;
        this.reason = reason;
    }

    public SyncProvider Provider { get; }

    public Task<SyncAdapterResult> PushAsync(SyncPushRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SyncAdapterResult(SyncAdapterOutcome.Unsupported, reason));

    public Task<SyncAdapterResult> PullAsync(SyncPullRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SyncAdapterResult(SyncAdapterOutcome.Unsupported, reason));
}

public sealed record SyncConflict(
    string Id,
    string AccountId,
    SyncProvider Provider,
    string LocalItemId,
    string RemoteResourceId,
    string LocalFingerprint,
    string RemoteFingerprint,
    DateTimeOffset CreatedAtUtc);
