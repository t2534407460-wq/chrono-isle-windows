namespace ChronoIsle.App.Services.State;

internal enum CollapsedSummaryKind
{
    None,
    Agenda,
    NetworkSpeed,
    CpuUsage,
    MemoryUsage
}

internal static class CollapsedIslandDisplayPolicy
{
    internal const double MinimumWidth = 96;
    internal const double MaximumWidth = 441;

    internal static CollapsedSummaryKind SelectSummary(
        bool showAgenda,
        bool showNetworkSpeed,
        bool showCpuUsage,
        bool showMemoryUsage,
        bool telemetryEnabled,
        long elapsedSeconds)
    {
        var sources = new List<CollapsedSummaryKind>(4);
        if (showAgenda) sources.Add(CollapsedSummaryKind.Agenda);
        if (telemetryEnabled)
        {
            if (showNetworkSpeed) sources.Add(CollapsedSummaryKind.NetworkSpeed);
            if (showCpuUsage) sources.Add(CollapsedSummaryKind.CpuUsage);
            if (showMemoryUsage) sources.Add(CollapsedSummaryKind.MemoryUsage);
        }

        if (sources.Count == 0) return CollapsedSummaryKind.None;
        return sources[(int)(elapsedSeconds / 5 % sources.Count)];
    }

    internal static double ClampWidth(double desiredWidth) =>
        Math.Clamp(desiredWidth, MinimumWidth, MaximumWidth);

    internal static double SelectWidth(
        double compactWidth,
        double fullContentWidth,
        bool pointerHover) =>
        ClampWidth(pointerHover
            ? Math.Max(compactWidth, fullContentWidth)
            : compactWidth);
}
