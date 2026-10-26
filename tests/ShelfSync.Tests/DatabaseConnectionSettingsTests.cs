using Npgsql;
using ShelfSync.Api;

namespace ShelfSync.Tests;

public sealed class DatabaseConnectionSettingsTests
{
    [Theory]
    [InlineData("Host=127.0.0.1;Database=shelfsync_dev;Username=app;Password=test")]
    [InlineData("Host=127.0.0.1;Database=shelfsync_demo;Port=55441;Username=app;Password=test")]
    [InlineData("Host=remote;Database=shelfsync_demo;Username=app;Password=test")]
    public void DemoGuardRejectsTheWrongConnectionBeforeStartup(string actual) =>
        Assert.Throws<InvalidOperationException>(() => DatabaseConnectionSettings.RequireDemoIsolation(actual,
            "Host=127.0.0.1;Database=shelfsync_demo;Port=55442;Username=app;Password=test"));

    [Fact]
    public void DemoGuardAcceptsOnlyTheRequestedIsolatedConnection()
    {
        const string demo = "Host=127.0.0.1;Database=shelfsync_demo;Port=55442;Username=app;Password=test";
        DatabaseConnectionSettings.RequireDemoIsolation(DatabaseConnectionSettings.Normalize(demo), demo);
    }
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
