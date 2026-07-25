namespace ChronoIsle.App.Services.Sync;

public enum SyncProvider { MicrosoftToDo, OutlookCalendar }
public enum SyncResourceKind { TodoTaskList, TodoTask, CalendarEvent }
public enum SyncDirection { Push, Pull }
public enum SyncAccountStatus { Disabled, Connected, ReauthRequired }
public enum SyncAdapterOutcome { Succeeded, NotConfigured, Unsupported, ReauthRequired, Failed }

public sealed record SyncCursor(
    string AccountId,
    SyncProvider Provider,
    SyncResourceKind ResourceKind,
    string ContainerId,
    DateTimeOffset? WindowStartUtc,
    DateTimeOffset? WindowEndUtc,
    string? NextLink,
    string? DeltaLink,
    DateTimeOffset? LastSuccessAtUtc)
{
    public static SyncCursor ForTodo(string accountId, string taskListId, SyncResourceKind resourceKind,
        string? nextLink, string? deltaLink, DateTimeOffset? lastSuccessAtUtc = null)
    {
        if (resourceKind is not (SyncResourceKind.TodoTaskList or SyncResourceKind.TodoTask))
            throw new ArgumentOutOfRangeException(nameof(resourceKind));
        return new(accountId, SyncProvider.MicrosoftToDo, resourceKind, taskListId,
            null, null, nextLink, deltaLink, lastSuccessAtUtc);
    }

    public static SyncCursor ForCalendar(string accountId, string calendarId,
        DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc,
        string? nextLink, string? deltaLink, DateTimeOffset? lastSuccessAtUtc = null)
    {
        if (windowEndUtc <= windowStartUtc)
            throw new ArgumentException("Calendar cursor window end must be later than its start.", nameof(windowEndUtc));
        return new(accountId, SyncProvider.OutlookCalendar, SyncResourceKind.CalendarEvent, calendarId,
            windowStartUtc, windowEndUtc, nextLink, deltaLink, lastSuccessAtUtc);
    }
}

public sealed record SyncPushRequest(string AccountId, string LocalItemId, long LocalRowVersion);
public sealed record SyncPullRequest(string AccountId, SyncCursor Cursor);
public sealed record SyncAdapterResult(SyncAdapterOutcome Outcome, string? Message = null);

public interface ISyncAdapter
{
    SyncProvider Provider { get; }
    Task<SyncAdapterResult> PushAsync(SyncPushRequest request, CancellationToken cancellationToken = default);
    Task<SyncAdapterResult> PullAsync(SyncPullRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Safe boundary used until Graph authentication and transport are explicitly configured.</summary>
public sealed class NotConfiguredSyncAdapter : ISyncAdapter
{
    public NotConfiguredSyncAdapter(SyncProvider provider) => Provider = provider;
    public SyncProvider Provider { get; }

    public Task<SyncAdapterResult> PushAsync(SyncPushRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SyncAdapterResult(SyncAdapterOutcome.NotConfigured,
            $"{Provider} sync is disabled until an authenticated adapter is configured."));

    public Task<SyncAdapterResult> PullAsync(SyncPullRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SyncAdapterResult(SyncAdapterOutcome.NotConfigured,
            $"{Provider} sync is disabled until an authenticated adapter is configured."));
}

public enum ExternalMappingDisposition { Editable, ReadOnlyMirror, Unsupported }

public sealed record ExternalMappingResult(ExternalMappingDisposition Disposition, string? ReadOnlyReason)
{
    public static ExternalMappingResult Editable() => new(ExternalMappingDisposition.Editable, null);
    public static ExternalMappingResult ReadOnly(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(ExternalMappingDisposition.ReadOnlyMirror, reason);
    }
    public static ExternalMappingResult Unsupported(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(ExternalMappingDisposition.Unsupported, reason);
    }
}
