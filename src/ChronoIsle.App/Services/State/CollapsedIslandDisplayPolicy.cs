namespace ChronoIsle.App.Services.State;

internal enum CollapsedSummaryKind
{
    None,
    Agenda,
    NetworkSpeed
}

internal static class CollapsedIslandDisplayPolicy
{
    internal const double MinimumWidth = 96;
    internal const double MaximumWidth = 441;

    internal static CollapsedSummaryKind SelectSummary(
        bool showAgenda,
        bool showNetworkSpeed,
        bool telemetryEnabled,
        long elapsedSeconds)
    {
        var networkAvailable = showNetworkSpeed && telemetryEnabled;
        if (!showAgenda) return networkAvailable ? CollapsedSummaryKind.NetworkSpeed : CollapsedSummaryKind.None;
        if (!networkAvailable) return CollapsedSummaryKind.Agenda;
        return elapsedSeconds / 5 % 2 == 0
            ? CollapsedSummaryKind.Agenda
            : CollapsedSummaryKind.NetworkSpeed;
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
