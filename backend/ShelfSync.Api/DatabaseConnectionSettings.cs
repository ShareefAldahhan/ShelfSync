using System.Data.Common;
using Npgsql;

namespace ShelfSync.Api;

public static class DatabaseConnectionSettings
{
    public static void RequireDemoIsolation(string actual, string expected)
    {
        var configured = new NpgsqlConnectionStringBuilder(actual);
        var requested = new NpgsqlConnectionStringBuilder(expected);
        if (requested.Database is not ("shelfsync_demo" or "shelfsync_test") ||
            requested.Host is not ("localhost" or "127.0.0.1") ||
            configured.Host != requested.Host || configured.Port != requested.Port ||
            configured.Database != requested.Database || configured.Username != requested.Username || configured.Password != requested.Password)
            throw new InvalidOperationException("Demo isolation check failed before startup. The store database must never be used by the demo host.");
    }
    public static string Normalize(string connectionString)
    {
        var options = new NpgsqlConnectionStringBuilder(connectionString)
        {
            IncludeErrorDetail = false,
            LogParameters = false
        };
        // Npgsql.ContainsKey also recognizes supported-but-unset keywords.
        // Parse the normalized, explicitly serialized keys to preserve overrides.
        var explicitOptions = new DbConnectionStringBuilder { ConnectionString = options.ConnectionString };
        if (!explicitOptions.ContainsKey("Maximum Pool Size")) options.MaxPoolSize = 80;
        return options.ConnectionString;
    }
}
