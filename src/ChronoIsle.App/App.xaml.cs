using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ChronoIsle.App.Services;
using ChronoIsle.App.Services.ImportExport;
using ChronoIsle.App.Services.Commanding;
using ChronoIsle.App.Services.Domain;
using ChronoIsle.App.Services.Persistence;
using ChronoIsle.App.Services.Productivity;
using ChronoIsle.App.Services.Reporting;
using ChronoIsle.App.Services.Media;
using ChronoIsle.App.Services.State;
using ChronoIsle.App.ViewModels;
using ChronoIsle.App.Views;

namespace ChronoIsle.App;

public partial class App : System.Windows.Application
{
    ServiceProvider? services;
    LifeMainWindow? main;
    Window? standalonePage;
    bool restoreMainAfterStandalonePage;
    bool openingMain;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);

        LegacyDataMigration.Run();

        var collection = new ServiceCollection();
        collection.AddSingleton<LifeDataService>();
        collection.AddSingleton<ProviderSettingsService>();
        collection.AddSingleton<LifePreferencesService>();
        collection.AddSingleton<OpenAiChatService>();
        collection.AddSingleton<MediaSessionService>();
        collection.AddSingleton<LyricsService>();
        collection.AddSingleton<AudioSpectrumService>();
        collection.AddSingleton<FullscreenAvoidanceService>();
        collection.AddSingleton<SystemTelemetryService>();
        collection.AddSingleton<SystemToastInboxService>();
        collection.AddSingleton<ThemeService>();
        collection.AddSingleton<IChatCompletionClient>(provider => provider.GetRequiredService<OpenAiChatService>());
        collection.AddSingleton<ChinaStatutoryHolidayCalendar>();
        collection.AddSingleton<AssistantIntentService>();
        collection.AddSingleton<IIslandStateCoordinator, IslandStateCoordinator>();
        collection.AddSingleton(provider => new AssistantCommandPipeline(provider.GetRequiredService<LifeDataService>().DatabasePath));
        collection.AddSingleton<IConversationPlanner>(provider =>
            new ConversationPlannerV2(provider.GetRequiredService<IChatCompletionClient>()));
        collection.AddSingleton<IOperationArgumentParser>(provider =>
            new OperationArgumentParserV2(provider.GetRequiredService<IChatCompletionClient>()));
        collection.AddSingleton(provider => new AssistantPlanPipeline(
            provider.GetRequiredService<LifeDataService>().DatabasePath,
            provider.GetRequiredService<AssistantCommandPipeline>()));
        collection.AddSingleton<ConversationRouter>();
        collection.AddSingleton<LocalAgendaQueryService>();
        collection.AddSingleton<MarkdownItemTransferService>();
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
            pipeline: provider.GetRequiredService<AssistantCommandPipeline>(),
            preferences: provider.GetRequiredService<LifePreferencesService>(),
            planner: provider.GetRequiredService<IConversationPlanner>(),
            argumentParser: provider.GetRequiredService<IOperationArgumentParser>(),
            assistantPlanPipeline: provider.GetRequiredService<AssistantPlanPipeline>()));
        collection.AddSingleton<WindowsNotificationService>();
        collection.AddSingleton<ReminderService>();
        collection.AddSingleton<LifeTrayService>();
        collection.AddSingleton<IAutoStartRegistry, CurrentUserRunRegistry>();
        collection.AddSingleton<AutoStartService>();
        collection.AddSingleton<NamingSuggestionService>();
        collection.AddSingleton<LifeViewModel>();
        collection.AddSingleton<LifeIslandWindow>();
        collection.AddTransient<LifeMainWindow>();
        collection.AddTransient<LifeSettingsWindow>();
        collection.AddTransient<LifeManagementWindow>();
        collection.AddTransient<NamingWindow>();
        services = collection.BuildServiceProvider();
        services.GetRequiredService<ThemeService>().Start();

        var uiTestMode = string.Equals(Environment.GetEnvironmentVariable("CHRONOISLE_UI_TEST_MODE"), "1", StringComparison.Ordinal);
        var notifications = services.GetRequiredService<WindowsNotificationService>();
        if (!uiTestMode)
            notifications.Register();
        notifications.Activated += (_, target) => Dispatcher.BeginInvoke(() =>
        {
            var island = services.GetRequiredService<LifeIslandWindow>();
            island.ShowReminder(target.Kind, target.Id);
            OpenMain();
        });

        var island = services.GetRequiredService<LifeIslandWindow>();
        island.OpenRequested += (_, _) => Dispatcher.BeginInvoke(OpenMain);
        island.SettingsRequested += (_, _) => Dispatcher.BeginInvoke(OpenLifeSettings);
        island.NamingRequested += (_, _) => Dispatcher.BeginInvoke(OpenNaming);
        island.ManageRequested += (_, _) => Dispatcher.BeginInvoke(() => OpenLifeManagement());
        island.ItemDetailsRequested += (_, target) => Dispatcher.BeginInvoke(() => OpenLifeManagement(target));
        island.ChatRequested += (_, text) => Dispatcher.BeginInvoke(() =>
        {
            OpenMain();
            main?.SubmitQuickInput(text);
        });
        services.GetRequiredService<ReminderService>().ReminderDue += (_, item) =>
            Dispatcher.BeginInvoke(() => island.ShowReminder(item.Kind, item.Id));
        var tray = services.GetRequiredService<LifeTrayService>();
        tray.OpenRequested += (_, _) => Dispatcher.BeginInvoke(OpenMain);
        tray.SettingsRequested += (_, _) => Dispatcher.BeginInvoke(OpenLifeSettings);
        tray.ManageRequested += (_, _) => Dispatcher.BeginInvoke(() => OpenLifeManagement());
        tray.NamingRequested += (_, _) => Dispatcher.BeginInvoke(OpenNaming);
        tray.ExitRequested += (_, _) => Dispatcher.BeginInvoke(Shutdown);
        tray.Initialize();
        if (!uiTestMode)
            island.Loaded += async (_, _) => await StartToastInboxAsync();
        island.Show();
        if (!uiTestMode)
        {
            var media = services.GetRequiredService<MediaSessionService>();
            _ = media.StartAsync().ContinueWith(task =>
            {
                if (task.Exception is not null)
                    System.Diagnostics.Debug.WriteLine($"Media session start failed: {task.Exception.GetBaseException().Message}");
            }, TaskScheduler.Default);
            services.GetRequiredService<AudioSpectrumService>().Start();

            var fullscreen = services.GetRequiredService<FullscreenAvoidanceService>();
            fullscreen.ContextChanged += context =>
                Dispatcher.BeginInvoke(() => island.SetFullscreenAvoidance(context));
            services.GetRequiredService<LifePreferencesService>().Changed += () =>
                Dispatcher.BeginInvoke(() =>
                {
                    island.SetFullscreenAvoidance(fullscreen.Current);
                    var currentPreferences = services.GetRequiredService<LifePreferencesService>().Load();
                    if (currentPreferences.TelemetryEnabled)
                        services.GetRequiredService<SystemTelemetryService>().Start();
                    if (currentPreferences.ToastInboxEnabled)
                        _ = StartToastInboxAsync();
                });
            fullscreen.Start();

            if (services.GetRequiredService<LifePreferencesService>().Load().TelemetryEnabled)
                services.GetRequiredService<SystemTelemetryService>().Start();
        }
        services.GetRequiredService<ReminderService>().Start();
    }

    async Task StartToastInboxAsync()
    {
        var serviceProvider = services!;
        if (!serviceProvider.GetRequiredService<LifePreferencesService>().Load().ToastInboxEnabled) return;
        await serviceProvider.GetRequiredService<SystemToastInboxService>().StartAsync();
    }

    void OpenMain()
    {
        CloseStandalonePage();
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

    public void OpenLifeSettings()
    {
        var page = services!.GetRequiredService<LifeSettingsWindow>();
        OpenStandalonePage(page);
    }

    public void OpenNaming()
    {
        var page = services!.GetRequiredService<NamingWindow>();
        OpenStandalonePage(page);
    }

    public void OpenLifeManagement(ItemNavigationTarget? target = null)
    {
        var page = services!.GetRequiredService<LifeManagementWindow>();
        if (target is not null) page.OpenItem(target);
        OpenStandalonePage(page);
    }

    void OpenStandalonePage(Window page)
    {
        services!.GetRequiredService<LifeIslandWindow>().CollapsePanel();
        var shouldRestoreMain = main?.IsVisible == true || restoreMainAfterStandalonePage;
        CloseStandalonePage();
        if (main?.IsVisible == true) main.Hide();

        standalonePage = page;
        restoreMainAfterStandalonePage = shouldRestoreMain;
        page.Closed += (_, _) =>
        {
            if (!ReferenceEquals(standalonePage, page)) return;
            standalonePage = null;
            var restoreMain = restoreMainAfterStandalonePage;
            restoreMainAfterStandalonePage = false;
            if (restoreMain) OpenMain();
        };
        page.Show();
        page.Activate();
    }

    void CloseStandalonePage()
    {
        if (standalonePage is not { } page) return;
        standalonePage = null;
        restoreMainAfterStandalonePage = false;
        page.Close();
    }

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChronoIsle");
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
