using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using WTT.Infrastructure;
using WTT.Infrastructure.Persistence;

var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WTT", "Logs");
Directory.CreateDirectory(logDirectory);
Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
    .WriteTo.File(Path.Combine(logDirectory, "bootstrap-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
    .CreateBootstrapLogger();

try
{
    Log.Information("WTT service bootstrap starting");
    using var ownership = new Mutex(initiallyOwned: true, "Global\\WTT.PipelineService", out var ownsPipeline);
    if (!ownsPipeline) throw new InvalidOperationException("Another WTT pipeline service instance is already running.");

    var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "WTTData")
    .UseContentRoot(AppContext.BaseDirectory)
    .ConfigureAppConfiguration(cfg =>
    {
        cfg.Sources.Clear();
        cfg.SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Service.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("WTT_");
    })
    .UseSerilog((ctx, services, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).ReadFrom.Services(services).Enrich.FromLogContext())
    .ConfigureServices((ctx, services) =>
    {
        services.AddInfrastructure(ctx.Configuration);
        services.AddHostedService<SchemaVerificationHostedService>();
        services.AddWttPipelineWorkers();
    })
        .Build();

    Log.Information("WTT service host built; starting service lifetime");
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "WTT service terminated during startup or execution");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

sealed class SchemaVerificationHostedService(DatabaseInitializer database, ILogger<SchemaVerificationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Verifying WTT database schema");
        await database.VerifySchemaCurrentAsync(cancellationToken);
        logger.LogInformation("WTT database schema is current");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
