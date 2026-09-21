using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class NotificationAppLauncherTests
{
    [Fact]
    public async Task RunningApp_FindsItsHiddenTopLevelWindow()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            System.Windows.Window? window = null;
            try
            {
                window = new System.Windows.Window { Title = "Notification launcher regression" };
                var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                Assert.False(window.IsVisible);
                var existing = NotificationAppLauncher.FindRunningWindow(Environment.ProcessPath);
                Assert.True(existing.IsRunning);
                Assert.Equal(handle, existing.Window);
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
            finally { window?.Close(); System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunningApp_ActivatesItsWindowWithoutLaunchingAgain(bool activated)
    {
        var launched = false;
        var result = NotificationAppLauncher.Open("QQ", _ => (true, new IntPtr(42)),
            window => { Assert.Equal(new IntPtr(42), window); return activated; }, _ => launched = true);
        Assert.Equal(activated, result);
        Assert.False(launched);
    }

    [Fact]
    public void RunningAppWithoutWindow_DoesNotLaunchAnotherInstance()
    {
        Assert.False(NotificationAppLauncher.Open("QQ", _ => (true, IntPtr.Zero),
            _ => throw new InvalidOperationException("No window to activate"),
            _ => throw new InvalidOperationException("Must not start another instance")));
    }

    [Fact]
    public void AppNotRunning_UsesTheRegisteredLaunchEntry()
    {
        string? launched = null;
        Assert.True(NotificationAppLauncher.Open("QQ", _ => (false, IntPtr.Zero),
            _ => throw new InvalidOperationException("No existing window"), id => launched = id));
        Assert.Equal("QQ", launched);
    }
}
