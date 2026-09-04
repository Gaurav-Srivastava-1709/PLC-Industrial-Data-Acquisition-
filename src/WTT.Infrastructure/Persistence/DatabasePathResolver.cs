using Microsoft.Data.Sqlite;
using WTT.Application.Options;

namespace WTT.Infrastructure.Persistence;

/// <summary>Resolves the SQLite file location and builds the connection string.</summary>
public static class DatabasePathResolver
{
    public static string ResolvePath(DatabaseOptions options)
    {
        var raw = string.IsNullOrWhiteSpace(options.Path) ? new DatabaseOptions().Path : options.Path;
        var expanded = Environment.ExpandEnvironmentVariables(raw);
        if (!Path.IsPathRooted(expanded))
            expanded = Path.Combine(AppContext.BaseDirectory, expanded);
        var full = Path.GetFullPath(expanded);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return full;
    }

    public static string BuildConnectionString(DatabaseOptions options)
    {
        var path = ResolvePath(options);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 30,
        };
        return builder.ToString();
    }
}
