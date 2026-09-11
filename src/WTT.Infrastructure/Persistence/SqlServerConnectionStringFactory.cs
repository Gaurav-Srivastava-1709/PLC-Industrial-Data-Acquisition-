using Microsoft.Data.SqlClient;
using WTT.Application.Options;

namespace WTT.Infrastructure.Persistence;

/// <summary>Validates the local SQL Server connection and enforces password-free Windows authentication.</summary>
public static class SqlServerConnectionStringFactory
{
    public static bool IsValid(DatabaseOptions options)
    {
        try { _ = Build(options); return true; }
        catch (ArgumentException) { return false; }
    }

    public static string Build(DatabaseOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new ArgumentException("Database connection string is required.");

        var builder = new SqlConnectionStringBuilder(options.ConnectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource))
            throw new ArgumentException("Database server is required.");
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
            throw new ArgumentException("Database name is required.");
        if (!builder.IntegratedSecurity)
            throw new ArgumentException("The local database must use Windows Integrated Security.");
        if (!builder.Encrypt)
            throw new ArgumentException("The local database connection must use encryption.");
        if (!string.IsNullOrWhiteSpace(builder.AttachDBFilename))
            throw new ArgumentException("AttachDbFilename and LocalDB file databases are not supported.");

        builder.PersistSecurityInfo = false;
        builder.MultipleActiveResultSets = false;
        builder.Pooling = true;
        if (string.IsNullOrWhiteSpace(builder.ApplicationName)) builder.ApplicationName = "WTT";
        return builder.ConnectionString;
    }
}
