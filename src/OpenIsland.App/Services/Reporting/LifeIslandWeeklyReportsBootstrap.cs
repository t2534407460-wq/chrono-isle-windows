using System.Runtime.CompilerServices;
using System.Windows;
using OpenIsland.App.Views;

namespace OpenIsland.App.Services.Reporting;

internal static class LifeIslandWeeklyReportsBootstrap
{
    static Timer? retryTimer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        retryTimer = new Timer(_ => TryAttach(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    static void TryAttach()
    {
        var application = Application.Current;
        if (application?.Dispatcher.HasShutdownStarted != false) return;
        application.Dispatcher.BeginInvoke(() =>
        {
            var island = application.Windows.OfType<LifeIslandWindow>().FirstOrDefault();
            if (island is null) return;
            island.EnableWeeklyReports();
            retryTimer?.Dispose();
            retryTimer = null;
        });
    }
}
