using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using OpenIsland.App.Services;
using OpenIsland.App.Services.Commanding;
using OpenIsland.App.Services.Persistence;
using OpenIsland.App.Services.Productivity;
using OpenIsland.App.Services.Reporting;
using OpenIsland.App.Services.State;
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
        collection.AddSingleton<IChatCompletionClient>(provider => provider.GetRequiredService<OpenAiChatService>());
        collection.AddSingleton<ChinaStatutoryHolidayCalendar>();
        collection.AddSingleton<AssistantIntentService>();
        collection.AddSingleton<IIslandStateCoordinator, IslandStateCoordinator>();
        collection.AddSingleton<AssistantCommandIntentService>();
        collection.AddSingleton(provider => new AssistantCommandPipeline(provider.GetRequiredService<LifeDataService>().DatabasePath));
        collection.AddSingleton<ConversationRouter>();
        collection.AddSingleton<LocalAgendaQueryService>();
        collection.AddSingleton(provider => new TodayDashboardService(provider.GetRequiredService<LifeDataService>()));
        collection.AddSingleton(provider =>
        {
            var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(provider.GetRequiredService<LifeDataService>().DatabasePath);
            return new TaskAttributesService(runtime.ConnectionFactory, runtime.WriteQueue);
        });
        collection.AddSingleton(provider =>
        {
            var path = provider.GetRequiredService<LifeDataService>().DatabasePath;
            var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(path);
            new ProductivitySchemaInitializer(runtime.WriteQueue).Initialize();
            return new FocusService(runtime.WriteQueue, runtime.ConnectionFactory);
        });
        collection.AddSingleton(provider =>
        {
            var runtime = LifeDataStoreRuntimeRegistry.GetOrCreate(provider.GetRequiredService<LifeDataService>().DatabasePath);
            return new ReportService(runtime.WriteQueue);
        });
        collection.AddSingleton(provider => new AssistantActionService(
            provider.GetRequiredService<LifeDataService>(),
            provider.GetRequiredService<ChinaStatutoryHolidayCalendar>(),
            provider.GetRequiredService<ConversationRouter>(),
            provider.GetRequiredService<LocalAgendaQueryService>(),
            provider.GetRequiredService<IChatCompletionClient>(),
            provider.GetRequiredService<AssistantCommandIntentService>(),
            provider.GetRequiredService<AssistantCommandPipeline>(),
            provider.GetRequiredService<LifePreferencesService>()));
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
        if (!string.Equals(Environment.GetEnvironmentVariable("OPENISLAND_UI_TEST_MODE"), "1", StringComparison.Ordinal))
            notifications.Register();
        notifications.Activated += (_, target) => Dispatcher.BeginInvoke(() =>
        {
            var island = services.GetRequiredService<LifeIslandWindow>();
            island.ShowReminder(target.Kind, target.Id);
            OpenMain();
        });

        var island = services.GetRequiredService<LifeIslandWindow>();
        island.OpenRequested += (_, _) => Dispatcher.BeginInvoke(OpenMain);
        island.SettingsRequested += (_, _) => Dispatcher.BeginInvoke(() => { OpenMain(); main?.OpenSettings(); });
        island.ChatRequested += (_, text) => Dispatcher.BeginInvoke(() =>
        {
            OpenMain();
            main?.SubmitQuickInput(text);
        });
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
