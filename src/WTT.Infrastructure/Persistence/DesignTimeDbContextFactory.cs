using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WTT.Infrastructure.Persistence;

/// <summary>Used only by the `dotnet ef` tooling to create migrations.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("WTT_DESIGN_CONNECTION")
            ?? "Server=.;Database=WTT;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=WTT EF Design";
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString);
        return new AppDbContext(builder.Options);
    }
}
