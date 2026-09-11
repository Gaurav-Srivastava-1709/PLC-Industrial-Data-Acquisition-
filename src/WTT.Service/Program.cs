using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using WTT.Infrastructure;
using WTT.Infrastructure.Persistence;

Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WTT", "Logs"));
using var ownership = new Mutex(initiallyOwned: true, "Global\\WTT.PipelineService", out var ownsPipeline);
if (!ownsPipeline) throw new InvalidOperationException("Another WTT pipeline service instance is already running.");

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "WTT Industrial Data Service")
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
        services.AddWttPipelineWorkers();
    })
    .Build();

await host.Services.GetRequiredService<DatabaseInitializer>().VerifySchemaCurrentAsync();
await host.RunAsync();
