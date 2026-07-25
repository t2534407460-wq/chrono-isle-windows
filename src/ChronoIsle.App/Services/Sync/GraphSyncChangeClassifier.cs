namespace ChronoIsle.App.Services.Sync;

public enum SyncChangeDisposition { NoChange, ApplyLocalToRemote, ApplyRemoteToLocal, Conflict }

public sealed record SyncChangeSnapshot(
    string? LastSyncedLocalFingerprint,
    string? CurrentLocalFingerprint,
    string? LastSyncedRemoteFingerprint,
    string? CurrentRemoteFingerprint);

public static class GraphSyncChangeClassifier
{
    public static SyncChangeDisposition Classify(SyncChangeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var localChanged = !StringComparer.Ordinal.Equals(
            snapshot.LastSyncedLocalFingerprint, snapshot.CurrentLocalFingerprint);
        var remoteChanged = !StringComparer.Ordinal.Equals(
            snapshot.LastSyncedRemoteFingerprint, snapshot.CurrentRemoteFingerprint);

        return (localChanged, remoteChanged) switch
        {
            (false, false) => SyncChangeDisposition.NoChange,
            (true, false) => SyncChangeDisposition.ApplyLocalToRemote,
            (false, true) => SyncChangeDisposition.ApplyRemoteToLocal,
            _ => SyncChangeDisposition.Conflict
        };
    }
}
