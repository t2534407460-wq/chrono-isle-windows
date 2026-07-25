namespace ChronoIsle.App.Services.Productivity;

public static class TaskAttributeNumbers
{
    public static int Increase(int value) => Normalize(value) + 1;

    public static int Decrease(int value) => Math.Max(1, Normalize(value) - 1);

    public static int Normalize(int value) => Math.Max(1, value);
}
