using ChronoIsle.App.Services.Domain;

namespace ChronoIsle.App.Services.Productivity;

public static class TaskDisplayLabels
{
    public static string Priority(LifePriority value) => value switch
    {
        LifePriority.Low => "低",
        LifePriority.Normal => "普通",
        LifePriority.High => "高",
        LifePriority.Urgent => "紧急",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string Energy(EnergyLevel value) => value switch
    {
        EnergyLevel.Low => "低精力",
        EnergyLevel.Medium => "中等精力",
        EnergyLevel.High => "高精力",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
