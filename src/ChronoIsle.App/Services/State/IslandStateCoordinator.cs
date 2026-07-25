namespace ChronoIsle.App.Services.State;

public enum IslandAnimationLevel
{
    None,
    Subtle,
    Prominent
}

public sealed record IslandStateSnapshot(
    string StateKey,
    int Priority,
    string DisplayText,
    DateTimeOffset? ExpiresAt,
    TimeSpan MinimumVisibleDuration,
    int CanBeInterruptedByPriority,
    IslandAnimationLevel AnimationLevel);

public interface IIslandStateCoordinator
{
    event Action<IslandStateSnapshot?>? StateChanged;
    IslandStateSnapshot? Current { get; }
    IslandStateSnapshot? Publish(IslandStateSnapshot candidate, DateTimeOffset now);
    IslandStateSnapshot? Clear(string stateKey, DateTimeOffset now);
}

public sealed class IslandStateCoordinator : IIslandStateCoordinator
{
    readonly object gate = new();
    public event Action<IslandStateSnapshot?>? StateChanged;
    IslandStateSnapshot? current;
    DateTimeOffset visibleSince;

    public IslandStateSnapshot? Current
    {
        get { lock (gate) return current; }
    }

    public IslandStateSnapshot? Publish(IslandStateSnapshot candidate, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.StateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.DisplayText);

        lock (gate)
        {
            if (current is null || IsExpired(current, now))
                return Replace(candidate, now);

            if (string.Equals(current.StateKey, candidate.StateKey, StringComparison.Ordinal))
            {
                // Countdown and clock ticks update only the text payload. They must not restart
                // the minimum-visible window or cause the shell to replay an animation.
                current = candidate with { AnimationLevel = IslandAnimationLevel.None };
                StateChanged?.Invoke(current);
                return current;
            }

            if (candidate.Priority >= current.CanBeInterruptedByPriority)
                return Replace(candidate, now);

            if (now - visibleSince < current.MinimumVisibleDuration)
                return current;

            if (candidate.Priority >= current.Priority)
                return Replace(candidate, now);

            return current;
        }
    }

    public IslandStateSnapshot? Clear(string stateKey, DateTimeOffset now)
    {
        lock (gate)
        {
            if (current is null || !string.Equals(current.StateKey, stateKey, StringComparison.Ordinal))
                return current;
            current = null;
            visibleSince = now;
            StateChanged?.Invoke(null);
            return null;
        }
    }

    static bool IsExpired(IslandStateSnapshot snapshot, DateTimeOffset now) =>
        snapshot.ExpiresAt is not null && snapshot.ExpiresAt <= now;

    IslandStateSnapshot Replace(IslandStateSnapshot candidate, DateTimeOffset now)
    {
        current = candidate;
        visibleSince = now;
        StateChanged?.Invoke(current);
        return candidate;
    }

    public static bool ShouldShowAiProcessing(TimeSpan elapsed) => elapsed >= TimeSpan.FromMilliseconds(300);

    public static IslandStateSnapshot AiSucceeded(string text, DateTimeOffset now) =>
        new("ai:succeeded", 40, text, now.AddSeconds(2), TimeSpan.FromSeconds(2), 80, IslandAnimationLevel.Subtle);
}
