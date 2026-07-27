namespace ChronoIsle.App.Services.State;

internal static class CollapsedIslandDisplayPolicy
{
    internal const double MinimumWidth = 96;
    internal const double MaximumWidth = 441;

    internal static double ClampWidth(double desiredWidth) =>
        Math.Clamp(desiredWidth, MinimumWidth, MaximumWidth);
}
