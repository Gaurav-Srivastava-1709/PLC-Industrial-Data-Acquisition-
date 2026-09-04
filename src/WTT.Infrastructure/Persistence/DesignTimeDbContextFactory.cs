using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using WTT.Application.Options;

namespace WTT.Infrastructure.Persistence;

/// <summary>Used only by the `dotnet ef` tooling to create migrations.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DatabaseOptions { Path = Path.Combine(Path.GetTempPath(), "wtt-design.db") };
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(DatabasePathResolver.BuildConnectionString(options));
        return new AppDbContext(builder.Options);
    }
}
