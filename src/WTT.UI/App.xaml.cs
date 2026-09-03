using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using WTT.Infrastructure.Data;
using Serilog;
using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.DependencyInjection;

namespace WTT.UI;

public partial class App : System.Windows.Application
{
    private readonly IHost _host;
    public IServiceProvider Services => _host.Services;

    public new static App Current => (App)System.Windows.Application.Current;

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((context, builder) =>
            {
                // AppContext.BaseDirectory ensures appsettings.json is loaded from the executable directory
                builder.SetBasePath(AppContext.BaseDirectory);
                builder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            })
            .UseSerilog((context, services, configuration) => configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext())
            .ConfigureServices((context, services) =>
            {
                // Register Views
                services.AddTransient<MainWindow>();
                services.AddTransient<Views.LoginWindow>();

                // Register ViewModels
                services.AddTransient<ViewModels.LoginViewModel>();
                services.AddTransient<ViewModels.MainViewModel>();
                services.AddTransient<ViewModels.DashboardViewModel>();
                services.AddTransient<ViewModels.DevicesViewModel>();
                services.AddTransient<ViewModels.TagsViewModel>();
                services.AddTransient<ViewModels.LiveDataViewModel>();
                services.AddTransient<ViewModels.SettingsViewModel>();
                services.AddTransient<ViewModels.DataTransferViewModel>();
                services.AddTransient<ViewModels.UsersViewModel>();
                services.AddTransient<ViewModels.UserEditViewModel>();

                // Register Pages
                services.AddTransient<Views.Pages.DashboardPage>();
                services.AddTransient<Views.Pages.DevicesPage>();
                services.AddTransient<Views.Pages.TagsPage>();
                services.AddTransient<Views.Pages.LiveDataPage>();
                services.AddTransient<Views.Pages.DataTransferPage>();
                services.AddTransient<Views.Pages.UsersPage>();
                services.AddTransient<Views.Pages.SettingsPage>();
                
                // Register Windows (Modals)
                services.AddTransient<Views.Windows.UserEditWindow>();

                // Register Application Services — WPF-UI Navigation
                services.AddNavigationViewPageProvider();
                services.AddSingleton<Wpf.Ui.INavigationService, Wpf.Ui.NavigationService>();
                services.AddSingleton<Services.SessionManager>();

                // Register Infrastructure/Domain Services
                services.AddScoped<WTT.Application.Interfaces.IAuthService, WTT.Infrastructure.Services.AuthService>();
                services.AddScoped<WTT.Application.Interfaces.IDeviceService, WTT.Infrastructure.Services.DeviceService>();
                services.AddScoped<WTT.Application.Interfaces.ITagService, WTT.Infrastructure.Services.TagService>();
                services.AddScoped<WTT.Application.Interfaces.IUserService, WTT.Infrastructure.Services.UserService>();

                // Background Services
                services.AddSingleton<WTT.Infrastructure.Services.DataCaptureBackgroundService>();
                services.AddSingleton<WTT.Application.Interfaces.IDataCaptureService>(sp => 
                    sp.GetRequiredService<WTT.Infrastructure.Services.DataCaptureBackgroundService>());
                services.AddHostedService(sp => 
                    sp.GetRequiredService<WTT.Infrastructure.Services.DataCaptureBackgroundService>());

                services.AddSingleton<WTT.Infrastructure.Services.DataTransferBackgroundService>();
                services.AddSingleton<WTT.Application.Interfaces.IDataTransferService>(sp => 
                    sp.GetRequiredService<WTT.Infrastructure.Services.DataTransferBackgroundService>());
                services.AddHostedService(sp => 
                    sp.GetRequiredService<WTT.Infrastructure.Services.DataTransferBackgroundService>());

                // PLC Drivers
                services.AddTransient<WTT.Infrastructure.PlcDrivers.MockPlcDriver>();
                services.AddTransient<WTT.Infrastructure.PlcDrivers.ModbusTcpDriver>();
                services.AddTransient<WTT.Infrastructure.PlcDrivers.SiemensS7Driver>();
                services.AddTransient<WTT.Infrastructure.PlcDrivers.AllenBradleyDriver>();
                services.AddSingleton<WTT.Infrastructure.PlcDrivers.PlcDriverFactory>();

                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseSqlite(context.Configuration.GetConnectionString("DefaultConnection"));
                });
            })
            .Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global Exception Handlers
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            Log.Fatal(ev.ExceptionObject as Exception, "Global unhandled exception occurred.");
        };

        DispatcherUnhandledException += (s, ev) =>
        {
            Log.Fatal(ev.Exception, "Dispatcher unhandled exception occurred.");
            ev.Handled = true;
        };

        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
            
            await _host.StartAsync();
            Log.Information("Application started successfully.");

            // Initialize Database & Seed Admin User
            using (var scope = _host.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                dbContext.Database.EnsureCreated();

                var adminUser = dbContext.Users.FirstOrDefault(u => u.Username == "admin");
                if (adminUser == null)
                {
                    dbContext.Users.Add(new WTT.Domain.Entities.User
                    {
                        Username = "admin",
                        PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin"),
                        Role = WTT.Domain.Entities.UserRole.Admin,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    });
                    dbContext.SaveChanges();
                }
                else
                {
                    // Refresh hash to ensure valid matching
                    adminUser.PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin");
                    adminUser.IsActive = true;
                    dbContext.SaveChanges();
                }
            }

            var loginWindow = _host.Services.GetRequiredService<Views.LoginWindow>();
            loginWindow.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application failed to start correctly.");
            MessageBox.Show("A critical error occurred while starting the application. Check the logs for details.", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("Application is exiting.");
        await _host.StopAsync();
        _host.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
