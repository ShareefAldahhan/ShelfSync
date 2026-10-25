using Npgsql;
using ShelfSync.Api;

namespace ShelfSync.Tests;

public sealed class DatabaseConnectionSettingsTests
{
    [Theory]
    [InlineData("", 80)]
    [InlineData(";Maximum Pool Size=12", 12)]
    [InlineData(";MaxPoolSize=20", 20)]
    [InlineData(";Maximum Pool Size=100", 100)]
    public void AppliesHeadroomDefaultAndPreservesExplicitConnectionBudgets(string suffix, int expected)
    {
        var actual = new NpgsqlConnectionStringBuilder(DatabaseConnectionSettings.Normalize(
            "Host=localhost;Database=unused;Include Error Detail=true;Log Parameters=true" + suffix));
        Assert.Equal(expected, actual.MaxPoolSize);
        Assert.False(actual.IncludeErrorDetail);
        Assert.False(actual.LogParameters);
    }
}
