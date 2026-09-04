using Microsoft.Data.SqlClient;
using WTT.Application.Interfaces;
using WTT.Domain.Entities;

namespace WTT.Infrastructure.Services;

/// <summary>Builds SQL Server connection strings from stored (encrypted) credentials.</summary>
public sealed class SqlConnectionStringFactory
{
    private readonly ISecureStorage _secureStorage;

    public SqlConnectionStringFactory(ISecureStorage secureStorage) => _secureStorage = secureStorage;

    public string ForServer(DeviceServer s, string? overridePassword = null, int connectTimeoutSeconds = 15) =>
        Build(s.ServerName, s.DatabaseName, s.UseIntegratedSecurity, s.UserId,
              overridePassword ?? _secureStorage.Unprotect(s.ProtectedPassword), s.Encrypt, s.TrustServerCertificate, connectTimeoutSeconds, "WTT Transfer");

    public string ForQuery(DeviceQuery q, string? overridePassword = null, int connectTimeoutSeconds = 15) =>
        Build(q.ServerName, q.DatabaseName, q.UseIntegratedSecurity, q.UserId,
              overridePassword ?? _secureStorage.Unprotect(q.ProtectedPassword), q.Encrypt, q.TrustServerCertificate, connectTimeoutSeconds, "WTT External Query");

    public static string Build(string server, string database, bool integrated, string? userId, string? password,
        bool encrypt, bool trustCert, int connectTimeoutSeconds, string appName)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            IntegratedSecurity = integrated,
            Encrypt = encrypt,
            TrustServerCertificate = trustCert,
            ConnectTimeout = connectTimeoutSeconds,
            ApplicationName = appName,
            MultipleActiveResultSets = false,
            Pooling = true,
        };
        if (!integrated)
        {
            b.UserID = userId ?? string.Empty;
            b.Password = password ?? string.Empty;
        }
        return b.ConnectionString;
    }
}
