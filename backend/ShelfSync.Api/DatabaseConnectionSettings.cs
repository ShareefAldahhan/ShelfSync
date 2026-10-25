using System.Data.Common;
using Npgsql;

namespace ShelfSync.Api;

public static class DatabaseConnectionSettings
{
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
