using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpenIsland.App.Services;
using OpenIsland.App.ViewModels;
using OpenIsland.App.Views;

namespace OpenIsland.App;

public partial class App : System.Windows.Application
{
    ServiceProvider? services;
    LifeMainWindow? main;
    bool openingMain;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);

        var collection = new ServiceCollection();
        collection.AddSingleton<LifeDataService>();
        collection.AddSingleton<ProviderSettingsService>();
        collection.AddSingleton<LifePreferencesService>();
        collection.AddSingleton<OpenAiChatService>();
        collection.AddSingleton<AssistantIntentService>();
        collection.AddSingleton<ConversationRouter>();
        collection.AddSingleton<LocalAgendaQueryService>();
        collection.AddSingleton<AssistantActionService>();
        collection.AddSingleton<WindowsNotificationService>();
        collection.AddSingleton<ReminderService>();
        collection.AddSingleton<LifeTrayService>();
        collection.AddSingleton<IAutoStartRegistry, CurrentUserRunRegistry>();
        collection.AddSingleton<AutoStartService>();
        collection.AddSingleton<LifeViewModel>();
        collection.AddSingleton<LifeIslandWindow>();
        collection.AddTransient<LifeMainWindow>();
        collection.AddTransient<LifeSettingsWindow>();
        collection.AddTransient<LifeManagementWindow>();
        services = collection.BuildServiceProvider();

        var notifications = services.GetRequiredService<WindowsNotificationService>();
        notifications.Register();
        notifications.Activated += (_, target) => Dispatcher.BeginInvoke(() =>
        {
            var island = services.GetRequiredService<LifeIslandWindow>();
            island.ShowReminder(target.Kind, target.Id);
            OpenMain();
        });

        var island = services.GetRequiredService<LifeIslandWindow>();
        island.OpenRequested += (_, _) => OpenMain();
        services.GetRequiredService<ReminderService>().ReminderDue += (_, item) =>
            Dispatcher.BeginInvoke(() => island.ShowReminder(item.Kind, item.Id));
        var tray = services.GetRequiredService<LifeTrayService>();
        tray.OpenRequested += (_, _) => Dispatcher.BeginInvoke(OpenMain);
        tray.SettingsRequested += (_, _) => Dispatcher.BeginInvoke(() => { OpenMain(); main?.OpenSettings(); });
        tray.ExitRequested += (_, _) => Dispatcher.BeginInvoke(Shutdown);
        tray.Initialize();
        island.Show();
        services.GetRequiredService<ReminderService>().Start();
    }

    void OpenMain()
    {
        if (openingMain) return;
        openingMain = true;
        try
        {
            if (main is null || !main.IsLoaded && !main.IsVisible)
            {
                main = services!.GetRequiredService<LifeMainWindow>();
                main.Closed += (_, _) => main = null;
            }
            if (!main.IsVisible) main.Show();
            if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
            main.Activate();
            main.Focus();
        }
        finally { openingMain = false; }
    }

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenIsland");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "life-assistant-errors.log"), $"{DateTime.Now:O}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (services?.GetService<ReminderService>() as IDisposable)?.Dispose();
        (services?.GetService<LifeTrayService>() as IDisposable)?.Dispose();
        (services as IDisposable)?.Dispose();
        base.OnExit(e);
    }
}
