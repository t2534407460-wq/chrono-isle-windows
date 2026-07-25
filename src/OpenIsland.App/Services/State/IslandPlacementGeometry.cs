using System.Runtime.CompilerServices;
using System.Windows;

[assembly: InternalsVisibleTo("OpenIsland.Tests")]

namespace OpenIsland.App.Services.State;

internal static class IslandPlacementGeometry
{
    internal readonly record struct HorizontalPlacement(double Left, int GapIndex);

    internal const double MinimumTaskbarHeight = 30;

    internal static bool TryGetBottomTaskbar(Rect bounds, Rect workArea, out Rect taskbar)
    {
        var height = bounds.Bottom - workArea.Bottom;
        if (height < MinimumTaskbarHeight)
        {
            taskbar = Rect.Empty;
            return false;
        }

        taskbar = new Rect(bounds.Left, workArea.Bottom, bounds.Width, height);
        return true;
    }

    internal static bool ShouldSnap(double distance, bool alreadySnapped, double snapThreshold, double unsnapThreshold)
        => Math.Abs(distance) < (alreadySnapped ? unsnapThreshold : snapThreshold);

    internal static double NormalizeHorizontalCenter(double centerX, Rect bounds)
    {
        if (bounds.Width <= 0) return 0.5;
        return Math.Clamp((centerX - bounds.Left) / bounds.Width, 0, 1);
    }

    internal static double LeftForHorizontalRatio(double ratio, double width, Rect bounds)
    {
        if (bounds.Width <= 0) return bounds.Left;
        var safeRatio = double.IsFinite(ratio) ? Math.Clamp(ratio, 0, 1) : 0.5;
        var visibleWidth = Math.Min(Math.Max(0, width), bounds.Width);
        var desiredCenter = bounds.Left + bounds.Width * safeRatio;
        var minimumCenter = bounds.Left + visibleWidth / 2;
        var maximumCenter = bounds.Right - visibleWidth / 2;
        return Math.Clamp(desiredCenter, minimumCenter, maximumCenter) - visibleWidth / 2;
    }

    internal static double ClampLeft(double desiredLeft, double width, Rect bounds)
    {
        if (bounds.Width <= 0) return bounds.Left;
        var visibleWidth = Math.Min(Math.Max(0, width), bounds.Width);
        var fallback = bounds.Left + (bounds.Width - visibleWidth) / 2;
        var finiteLeft = double.IsFinite(desiredLeft) ? desiredLeft : fallback;
        return Math.Clamp(finiteLeft, bounds.Left, bounds.Right - visibleWidth);
    }

    internal static Rect ClampRectToBounds(Rect candidate, Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return candidate;
        var width = Math.Max(0, candidate.Width);
        var height = Math.Max(0, candidate.Height);
        var left = width <= bounds.Width
            ? Math.Clamp(candidate.Left, bounds.Left, bounds.Right - width)
            : bounds.Left;
        var top = height <= bounds.Height
            ? Math.Clamp(candidate.Top, bounds.Top, bounds.Bottom - height)
            : bounds.Top;
        return new Rect(left, top, width, height);
    }

    internal static double LeftAvoidingOccupiedRanges(
        double desiredLeft,
        double width,
        Rect bounds,
        IReadOnlyCollection<Rect> occupied,
        double padding)
        => PlaceAvoidingOccupiedRanges(desiredLeft, width, bounds, occupied, padding).Left;

    internal static HorizontalPlacement PlaceAvoidingOccupiedRanges(
        double desiredLeft,
        double width,
        Rect bounds,
        IReadOnlyCollection<Rect> occupied,
        double padding)
    {
        var clampedDesired = ClampLeft(desiredLeft, width, bounds);
        if (bounds.Width <= 0 || width > bounds.Width)
            return new HorizontalPlacement(clampedDesired, -1);

        var safePadding = double.IsFinite(padding) ? Math.Max(0, padding) : 0;
        var blocked = new List<(double Left, double Right)>();
        foreach (var rectangle in occupied)
        {
            var left = Math.Max(bounds.Left, rectangle.Left - safePadding);
            var right = Math.Min(bounds.Right, rectangle.Right + safePadding);
            if (right > left) blocked.Add((left, right));
        }
        blocked.Sort((first, second) => first.Left.CompareTo(second.Left));

        HorizontalPlacement? bestPlacement = null;
        var bestDistance = double.PositiveInfinity;
        var gapIndex = 0;
        void ConsiderGap(double left, double right)
        {
            if (right - left < width) return;
            var currentGapIndex = gapIndex++;
            var candidate = Math.Clamp(clampedDesired, left, right - width);
            var distance = Math.Abs(candidate - clampedDesired);
            if (distance >= bestDistance) return;
            bestDistance = distance;
            bestPlacement = new HorizontalPlacement(candidate, currentGapIndex);
        }

        var cursor = bounds.Left;
        foreach (var range in blocked)
        {
            ConsiderGap(cursor, range.Left);
            cursor = Math.Max(cursor, range.Right);
        }
        ConsiderGap(cursor, bounds.Right);

        // 图标没有留下足够宽的空隙时，按用户预期恢复为仅限制屏幕边界的自由移动。
        return bestPlacement ?? new HorizontalPlacement(clampedDesired, -1);
    }

    internal static double TaskbarHeaderTop(Rect taskbar, double headerHeight)
        => taskbar.Top + Math.Max(0, taskbar.Height - headerHeight) / 2;

    internal static double WindowTopForHeaderAnchor(double targetHeaderTop, double headerOffset)
        => targetHeaderTop - headerOffset;
}
