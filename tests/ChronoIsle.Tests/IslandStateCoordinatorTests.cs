using ChronoIsle.App.Services.State;

namespace ChronoIsle.Tests;

public sealed class IslandStateCoordinatorTests
{
    static readonly DateTimeOffset Now = new(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LowerPriorityState_CannotCauseFlickerDuringMinimumVisibleWindow()
    {
        var coordinator = new IslandStateCoordinator();
        var success = IslandStateCoordinator.AiSucceeded("已生成建议", Now);
        coordinator.Publish(success, Now);

        var idle = Snapshot("idle", 1, "下一项：写日报");
        var shown = coordinator.Publish(idle, Now.AddSeconds(1));

        Assert.Equal("ai:succeeded", shown!.StateKey);
    }

    [Fact]
    public void DueReminder_InterruptsLowerPriorityStateImmediately()
    {
        var coordinator = new IslandStateCoordinator();
        coordinator.Publish(Snapshot("focus", 50, "专注中 18:24", interruptAt: 80), Now);

        var shown = coordinator.Publish(Snapshot("reminder:42", 90, "该喝水了", interruptAt: 100), Now.AddMilliseconds(10));

        Assert.Equal("reminder:42", shown!.StateKey);
    }

    [Fact]
    public void SameStateTextUpdate_DoesNotReplayAnimationOrResetVisibility()
    {
        var coordinator = new IslandStateCoordinator();
        coordinator.Publish(Snapshot("focus", 50, "专注中 18:24", animation: IslandAnimationLevel.Subtle), Now);

        var shown = coordinator.Publish(Snapshot("focus", 50, "专注中 18:23", animation: IslandAnimationLevel.Subtle), Now.AddSeconds(1));

        Assert.Equal("专注中 18:23", shown!.DisplayText);
        Assert.Equal(IslandAnimationLevel.None, shown.AnimationLevel);
    }

    [Theory]
    [InlineData(299, false)]
    [InlineData(300, true)]
    public void AiProcessing_UsesThreeHundredMillisecondGate(int milliseconds, bool expected) =>
        Assert.Equal(expected, IslandStateCoordinator.ShouldShowAiProcessing(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void Publishing_and_clearing_state_notifies_the_shell_bridge()
    {
        var coordinator = new IslandStateCoordinator();
        var notifications = new List<string?>();
        coordinator.StateChanged += snapshot => notifications.Add(snapshot?.StateKey);

        coordinator.Publish(Snapshot("ai:processing", 35, "AI 正在处理…"), Now);
        coordinator.Clear("ai:processing", Now.AddSeconds(1));

        Assert.Equal(new string?[] { "ai:processing", null }, notifications);
    }

    static IslandStateSnapshot Snapshot(
        string key,
        int priority,
        string text,
        int interruptAt = 80,
        IslandAnimationLevel animation = IslandAnimationLevel.None) =>
        new(key, priority, text, null, TimeSpan.FromSeconds(2), interruptAt, animation);
}
