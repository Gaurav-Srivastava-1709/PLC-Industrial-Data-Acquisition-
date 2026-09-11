using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WTT.Infrastructure.Persistence;

namespace WTT.Tests;

internal sealed class SqlServerTestDatabase
{
    private readonly string _databaseName = $"WTT_Test_{Guid.NewGuid():N}";
    private readonly string _server = Environment.GetEnvironmentVariable("WTT_TEST_SQLSERVER") ?? ".";
    public string DatabaseName => _databaseName;

    public string ConnectionString => new SqlConnectionStringBuilder
    {
        DataSource = _server,
        InitialCatalog = _databaseName,
        IntegratedSecurity = true,
        Encrypt = true,
        TrustServerCertificate = true,
        ApplicationName = "WTT Tests",
        ConnectTimeout = 10,
        Pooling = true,
    }.ConnectionString;

    public DbContextOptions<AppDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options;

    public async Task DeleteAsync()
    {
        SqlConnection.ClearAllPools();
        var builder = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master", Pooling = false };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = $"IF DB_ID(N'{_databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]; END";
        await command.ExecuteNonQueryAsync();
    }
}
