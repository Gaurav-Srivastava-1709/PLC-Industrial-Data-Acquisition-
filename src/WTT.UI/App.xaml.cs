using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Wpf.Ui;
using Wpf.Ui.DependencyInjection;
using WTT.Infrastructure;
using WTT.Infrastructure.Persistence;
using WTT.UI.Services;
using WTT.UI.ViewModels;
using WTT.UI.Views.Pages;
using WTT.UI.Views.Windows;

namespace WTT.UI;

public partial class App : System.Windows.Application
{
    private readonly IHost _host;

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureAppConfiguration(cfg =>
            {
                cfg.Sources.Clear();
                cfg.SetBasePath(AppContext.BaseDirectory)
                   .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                   .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("WTT_ENVIRONMENT") ?? "Production"}.json", optional: true)
                   .AddEnvironmentVariables("WTT_");
            })
            .UseSerilog((ctx, services, cfg) => cfg
                .ReadFrom.Configuration(ctx.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext())
            .ConfigureServices((ctx, services) =>
            {
                services.AddInfrastructure(ctx.Configuration);

                // WPF-UI plumbing
                services.AddNavigationViewPageProvider();
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<IContentDialogService, ContentDialogService>();
                services.AddSingleton<ISnackbarService, SnackbarService>();

                // UI services
                services.AddSingleton<IDialogService, DialogService>();
                services.AddSingleton<NavigationContext>();
                services.AddSingleton<ThemeManager>();
                services.AddSingleton<SessionManager>();
                services.AddSingleton<ShellNavigator>();

                // Windows (transient: a fresh window per show)
                services.AddTransient<LoginWindow>();
                services.AddTransient<LoginViewModel>();
                services.AddTransient<MainWindow>();
                services.AddTransient<MainViewModel>();

                // Pages + view models (transient: NavigationView creates a page per navigation)
                services.AddTransient<DashboardPage>();
                services.AddTransient<DashboardViewModel>();
                services.AddTransient<DevicesPage>();
                services.AddTransient<DevicesViewModel>();
                services.AddTransient<DeviceConfigPage>();
                services.AddTransient<DeviceConfigViewModel>();
                services.AddTransient<LiveDataPage>();
                services.AddTransient<LiveDataViewModel>();
                services.AddTransient<DataTransferPage>();
                services.AddTransient<DataTransferViewModel>();
                services.AddTransient<UsersPage>();
                services.AddTransient<UsersViewModel>();
                services.AddTransient<LogViewerPage>();
                services.AddTransient<LogViewerViewModel>();
                services.AddTransient<SettingsPage>();
                services.AddTransient<SettingsViewModel>();

                // Dialog view models + Func<T> factories so pages can spawn fresh dialog VMs without a service locator
                AddWithFactory<DeviceEditViewModel>(services);
                AddWithFactory<TagEditViewModel>(services);
                AddWithFactory<AddressEditViewModel>(services);
                AddWithFactory<QueryEditViewModel>(services);
                AddWithFactory<UserEditViewModel>(services);
                AddWithFactory<ChangePasswordViewModel>(services);
                AddWithFactory<WriteTagViewModel>(services);
                AddWithFactory<TransferServerEditViewModel>(services);
            })
            .Build();
    }

    private static void AddWithFactory<T>(IServiceCollection services) where T : class
    {
        services.AddTransient<T>();
        services.AddSingleton<Func<T>>(sp => () => sp.GetRequiredService<T>());
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        try
        {
            logger.LogInformation("==== WTT starting (v{Version}, {OS}, user {User}) ====",
                typeof(App).Assembly.GetName().Version, Environment.OSVersion, Environment.UserName);

            // 1. Database first: migrations + seed, before any background service touches it.
            var seededAdmin = await _host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();

            // 2. Theme from saved settings.
            await _host.Services.GetRequiredService<ThemeManager>().LoadAsync();

            // 3. Background services (capture / transfer supervisors).
            await _host.StartAsync();

            // 4. UI.
            var shell = _host.Services.GetRequiredService<ShellNavigator>();
            shell.ShowLogin(seededAdmin
                ? $"First run: sign in with '{DatabaseInitializer.DefaultAdminUsername}' / '{DatabaseInitializer.DefaultAdminPassword}'. You will be asked to set a new password."
                : null);

            logger.LogInformation("WTT started successfully.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "WTT failed to start.");
            MessageBox.Show(
                $"WTT could not start.\n\n{ex.Message}\n\nLog folder: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WTT", "Logs")}",
                "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        var logger = _host.Services.GetService<ILogger<App>>();
        try
        {
            logger?.LogInformation("WTT shutting down...");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _host.StopAsync(cts.Token);
            logger?.LogInformation("==== WTT stopped ====");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error during shutdown.");
        }
        finally
        {
            _host.Dispose();
            await Log.CloseAndFlushAsync();
            base.OnExit(e);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        var logger = _host.Services.GetRequiredService<ILogger<App>>();

        DispatcherUnhandledException += (_, args) =>
        {
            logger.LogError(args.Exception, "Unhandled UI exception.");
            args.Handled = true; // keep the app alive, but TELL the user
            try
            {
                var dialogs = _host.Services.GetRequiredService<IDialogService>();
                _ = dialogs.ShowErrorAsync("Unexpected error", $"{args.Exception.Message}\n\nThe error has been logged. If it keeps happening, send the log file to support.");
            }
            catch (Exception) { MessageBox.Show(args.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error); }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.LogError(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            logger.LogCritical(args.ExceptionObject as Exception, "Fatal unhandled exception (terminating={Terminating}).", args.IsTerminating);
            Log.CloseAndFlush();
        };
    }
}
