using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WTT.Application.Interfaces;
using WTT.Application.Options;
using WTT.Domain.Interfaces;
using WTT.Infrastructure.Capture;
using WTT.Infrastructure.Logging;
using WTT.Infrastructure.Persistence;
using WTT.Infrastructure.PlcDrivers;
using WTT.Infrastructure.Security;
using WTT.Infrastructure.Services;
using WTT.Infrastructure.Transfer;

namespace WTT.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // ---- Options (bound + validated at startup) ----
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .Validate(SqlServerConnectionStringFactory.IsValid, "Database must be a valid SQL Server connection using Windows Integrated Security.")
            .ValidateOnStart();
        services.AddOptions<CaptureOptions>().Bind(configuration.GetSection(CaptureOptions.SectionName))
            .Validate(o => o.ReconcileIntervalSeconds >= 1 && o.MinPollIntervalMs >= 50 && o.TransferredRetentionDays >= 1, "Invalid Capture options.")
            .ValidateOnStart();
        services.AddOptions<TransferOptions>().Bind(configuration.GetSection(TransferOptions.SectionName))
            .Validate(o => o.IntervalSeconds >= 1 && o.BatchSize is >= 100 and <= 100_000 && o.BulkCopyBatchSize >= 100 && o.MaxBatchesPerDevicePerCycle is >= 1 and <= 100, "Invalid Transfer options.")
            .ValidateOnStart();
        services.AddOptions<SecurityOptions>().Bind(configuration.GetSection(SecurityOptions.SectionName))
            .Validate(o => o.SessionTimeoutMinutes >= 1 && o.MaxFailedLogins >= 1 && o.MinPasswordLength >= 6 && o.BcryptWorkFactor is >= 10 and <= 15, "Invalid Security options.")
            .ValidateOnStart();
        services.AddOptions<SimulationOptions>().Bind(configuration.GetSection(SimulationOptions.SectionName));
        services.AddOptions<AlarmOptions>().Bind(configuration.GetSection(AlarmOptions.SectionName))
            .Validate(o => o.RetentionDays >= 0 && o.RetentionDays <= 3650, "Alarm retention must be 0-3650")
            .ValidateOnStart();

        // ---- Persistence: a factory, never a long-lived shared context ----
        services.AddDbContextFactory<AppDbContext>((sp, o) =>
        {
            var dbOptions = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            o.UseSqlServer(SqlServerConnectionStringFactory.Build(dbOptions), sql =>
            {
                sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(5), null);
                sql.CommandTimeout(30);
                sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            });
            o.EnableSensitiveDataLogging(false);
        });
        services.AddSingleton<DatabaseInitializer>();

        // ---- Security / auth ----
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<ISecureStorage, DpapiSecureStorage>();
        services.AddSingleton<ICurrentUserService, CurrentUserService>();
        services.AddSingleton<IAuditService, AuditService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IInitialAdminEnrollmentService, InitialAdminEnrollmentService>();
        services.AddSingleton<IUserService, UserService>();

        // ---- Configuration services ----
        services.AddSingleton<SqlConnectionStringFactory>();
        services.AddSingleton<IExternalSqlPolicy, ExternalSqlSelectPolicy>();
        services.AddSingleton<IInstallationIdentity, FileInstallationIdentity>();
        services.AddSingleton<IDeviceService, DeviceService>();
        services.AddSingleton<ITagProvisioningService, TagProvisioningService>();
        services.AddSingleton<IDeviceAddressService, DeviceAddressService>();
        services.AddSingleton<ITagService, TagService>();
        // SCADA Historian Service: process data trends nikalne ke liye
        services.AddSingleton<IHistorianService, HistorianService>();
        services.AddSingleton<ITriggerService, TriggerService>();
        services.AddSingleton<IDeviceServerService, DeviceServerService>();
        services.AddSingleton<IExternalQueryService, ExternalQueryService>();
        services.AddSingleton<IAppSettingsService, AppSettingsService>();
        services.AddSingleton<IRuntimeSettings, RuntimeSettings>();
        services.AddSingleton<ILogReaderService>(_ => new LogReaderService());
        
        // ---- Alarms ----
        services.AddSingleton<InAppAlarmNotifier>();
        services.AddSingleton<EmailAlarmNotifier>();
        services.AddSingleton<IAlarmService, AlarmEvaluationService>();
        services.AddSingleton<IAlarmNotifier>(sp => new CompositeAlarmNotifier(new IAlarmNotifier[] { sp.GetRequiredService<InAppAlarmNotifier>(), sp.GetRequiredService<EmailAlarmNotifier>() }));
        // Wire notifier into evaluation service (avoid circular constructor)
        services.AddSingleton<IHostedService>(sp =>
        {
            var alarm = sp.GetRequiredService<IAlarmService>() as AlarmEvaluationService;
            var notifier = sp.GetRequiredService<IAlarmNotifier>();
            if (alarm != null) alarm.Notifier = notifier;
            return new NoOpHostedService();
        });

        // ---- PLC ----
        services.AddSingleton<IPlcDriverFactory, PlcDriverFactory>();
        services.AddSingleton<IPlcOperationsService, PlcOperationsService>();
        services.AddSingleton<LiveValueCache>();
        services.AddSingleton<SqlCaptureRuntimeStatusService>();
        services.AddSingleton<ICaptureRuntimeStatusReader>(sp => sp.GetRequiredService<SqlCaptureRuntimeStatusService>());
        services.AddSingleton<ICaptureRuntimeStatusWriter>(sp => sp.GetRequiredService<SqlCaptureRuntimeStatusService>());

        // ---- Pipeline controllers; only the dedicated service/test host starts them. ----
        services.AddSingleton<DataCaptureBackgroundService>();
        services.AddSingleton<IDataCaptureController>(sp => sp.GetRequiredService<DataCaptureBackgroundService>());

        services.AddSingleton<DataTransferBackgroundService>();
        services.AddSingleton<IDataTransferController>(sp => sp.GetRequiredService<DataTransferBackgroundService>());

        return services;
    }

    public static IServiceCollection AddWttPipelineWorkers(this IServiceCollection services)
    {
        services.AddHostedService(sp => sp.GetRequiredService<DataCaptureBackgroundService>());
        services.AddHostedService(sp => sp.GetRequiredService<DataTransferBackgroundService>());
        services.AddHostedService<AlarmRetentionWorker>();
        return services;
    }

    private sealed class NoOpHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
