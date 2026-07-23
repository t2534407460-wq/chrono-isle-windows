namespace OpenIsland.App.Services.Productivity;

public sealed record TaskRankCandidate(string Id, DateTimeOffset? DueAtUtc, string Priority, int? EstimatedMinutes);

public static class LocalTaskRanker
{
    public static IReadOnlyList<TaskRankCandidate> Rank(IEnumerable<TaskRankCandidate> items, DateTimeOffset now) =>
        items.OrderByDescending(item => item.DueAtUtc < now)
            .ThenByDescending(item => Priority(item.Priority))
            .ThenBy(item => item.DueAtUtc is null)
            .ThenBy(item => item.DueAtUtc)
            .ThenBy(item => item.EstimatedMinutes ?? int.MaxValue)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

    static int Priority(string priority) => priority switch
    {
        "Urgent" => 3, "High" => 2, "Normal" => 1, "Low" => 0, _ => -1
    };
}
