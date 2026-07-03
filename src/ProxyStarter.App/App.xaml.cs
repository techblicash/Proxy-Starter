using System;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.Services;
using ProxyStarter.App.ViewModels;
using ProxyStarter.App.Views;
using Wpf.Ui;
using Wpf.Ui.Abstractions;

namespace ProxyStarter.App;

public partial class App : System.Windows.Application
{
    private readonly IHost _host;

    public static IServiceProvider Services => ((App)Current)._host.Services;

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddHttpClient();
                services.AddHttpClient("MihomoApi", client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(3);
                });
                services.AddHttpClient("SubscriptionDirect")
                    .ConfigureHttpClient(client =>
                    {
                        client.Timeout = TimeSpan.FromSeconds(30);
                    })
                    .ConfigurePrimaryHttpMessageHandler(() =>
                    {
                        return new System.Net.Http.HttpClientHandler
                        {
                            AutomaticDecompression = System.Net.DecompressionMethods.All,
                            UseProxy = false
                        };
                    });

                services.AddHttpClient("SubscriptionSystemProxy")
                    .ConfigureHttpClient(client =>
                    {
                        client.Timeout = TimeSpan.FromSeconds(30);
                    })
                    .ConfigurePrimaryHttpMessageHandler(() =>
                    {
                        return new System.Net.Http.WinHttpHandler
                        {
                            AutomaticDecompression = System.Net.DecompressionMethods.All,
                            WindowsProxyUsePolicy = System.Net.Http.WindowsProxyUsePolicy.UseWinInetProxy
                        };
                    });
                services.AddSingleton<AppSettingsStore>();
                services.AddSingleton<SubscriptionStore>();
                services.AddSingleton<DefaultRulesService>();
                services.AddSingleton<SubscriptionCacheStore>();
                services.AddSingleton<ProxyCatalogStore>();
                services.AddSingleton<SubscriptionParser>();
                services.AddSingleton<SubscriptionService>();
                services.AddSingleton<RulesStore>();
                services.AddSingleton<ConfigWriter>();
                services.AddSingleton<SingBoxConfigWriter>();
                services.AddSingleton<MihomoCoreAdapter>();
                services.AddSingleton<SingBoxCoreAdapter>();
                services.AddSingleton<ProxyCoreAdapterFactory>();
                services.AddSingleton<CoreDownloadService>();
                services.AddSingleton<SoftwareUpdateService>();
                services.AddSingleton<SystemProxyService>();
                services.AddSingleton<IMihomoProcessService, MihomoProcessService>();
                services.AddSingleton<CoreController>();
                services.AddSingleton<TrafficMonitorService>();
                services.AddSingleton<ConnectionMonitorService>();
                services.AddSingleton<ConnectionsMonitorService>();
                services.AddSingleton<MihomoLogService>();
                services.AddSingleton<MihomoApiClient>();
                services.AddSingleton<LatencyTestService>();
                services.AddSingleton<SubscriptionAutoUpdateService>();
                services.AddSingleton<AutoStartService>();
                services.AddSingleton<QosPolicyService>();
                services.AddSingleton<IDialogService, DialogService>();
                services.AddSingleton<AppThemeService>();
                services.AddSingleton<LocalizationService>();
                services.AddSingleton<IWindowService, WindowService>();
                services.AddSingleton<INavigationViewPageProvider, NavigationViewPageProvider>();
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<MainWindow>();

                services.AddTransient<DashboardViewModel>();
                services.AddTransient<ProfilesViewModel>();
                services.AddSingleton<NodesViewModel>();
                services.AddTransient<RulesViewModel>();
                services.AddTransient<LogsViewModel>();
                services.AddTransient<ConnectionsViewModel>();
                services.AddTransient<SettingsViewModel>();

                services.AddTransient<DashboardPage>();
                services.AddTransient<ProfilesPage>();
                services.AddTransient<NodesPage>();
                services.AddTransient<RulesPage>();
                services.AddTransient<LogsPage>();
                services.AddTransient<ConnectionsPage>();
                services.AddTransient<SettingsPage>();
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        RegisterCrashHandlers();

        try
        {
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        }
        catch
        {
        }

        DpiHelper.EnsurePerMonitorV2();
        await _host.StartAsync();

        var settingsStore = _host.Services.GetRequiredService<AppSettingsStore>();
        var systemProxyService = _host.Services.GetRequiredService<SystemProxyService>();
        systemProxyService.Restore();
        var localizationService = _host.Services.GetRequiredService<LocalizationService>();
        localizationService.ApplyLanguage(settingsStore.Settings.Language);

        try
        {
            var subscriptionStore = _host.Services.GetRequiredService<SubscriptionStore>();
            var subscriptionService = _host.Services.GetRequiredService<SubscriptionService>();
            subscriptionService.RebuildCatalogFromCache(subscriptionStore.Load());
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "Startup: RebuildCatalogFromCache");
        }

        var qosPolicyService = _host.Services.GetRequiredService<QosPolicyService>();
        _ = qosPolicyService.ApplyAsync(settingsStore.Settings, silent: true);

        var subscriptionAutoUpdate = _host.Services.GetRequiredService<SubscriptionAutoUpdateService>();
        subscriptionAutoUpdate.Start();

        var logService = _host.Services.GetRequiredService<MihomoLogService>();
        logService.Start();

        var window = _host.Services.GetRequiredService<MainWindow>();
        Current.MainWindow = window;
        var themeService = _host.Services.GetRequiredService<AppThemeService>();
        themeService.ApplyTheme(settingsStore.Settings.Theme, window);
        window.Show();

        base.OnStartup(e);
    }

    private void RegisterCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            if (args.Exception is not null)
            {
                CrashLogger.Log(args.Exception, "DispatcherUnhandledException");
            }

            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                CrashLogger.Log(ex, "AppDomain.UnhandledException");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            if (args.Exception is not null)
            {
                CrashLogger.Log(args.Exception, "TaskScheduler.UnobservedTaskException");
            }

            args.SetObserved();
        };
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            var coreController = _host.Services.GetService<CoreController>();
            if (coreController is not null)
            {
                await coreController.StopAsync();
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "OnExit: Stop core");
        }

        await _host.StopAsync();
        _host.Dispose();

        base.OnExit(e);
    }
}
