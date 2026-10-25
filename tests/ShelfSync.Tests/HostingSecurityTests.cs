using ShelfSync.Api.Security;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ShelfSync.Tests;

public sealed class HostingSecurityTests
{
    [Theory]
    [InlineData("http://demo.example.com")]
    [InlineData("https://*.example.com")]
    [InlineData("https://user:password@demo.example.com")]
    [InlineData("https://demo.example.com/path")]
    [InlineData("https://demo.example.com?redirect=elsewhere")]
    [InlineData("https://demo.example.com#fragment")]
    public void RejectsUnsafePublicOrigins(string origin) =>
        Assert.Throws<InvalidOperationException>(() => HostingSecurity.ValidateOrigin(origin));

    [Fact]
    public void AllowsFixedHttpsOrigin() =>
        Assert.Equal("demo.example.com", HostingSecurity.ValidateOrigin("https://demo.example.com").Host);

    [Theory]
    [InlineData("*")]
    [InlineData("0.0.0.0/0")]
    [InlineData("proxy.example.com")]
    public void RejectsWildcardNetworkAndHostnameTrust(string proxies) =>
        Assert.Throws<InvalidOperationException>(() => HostingSecurity.ParseProxies(proxies));

    [Fact]
    public void TrustIsEmptyUnlessExplicitlyConfigured()
    {
        Assert.Empty(HostingSecurity.ParseProxies(null));
        Assert.Equal(2, HostingSecurity.ParseProxies("127.0.0.1, ::1").Length);
    }

    [Theory]
    [InlineData("192.0.2.10", "https")]
    [InlineData("192.0.2.11", "http")]
    public async Task OnlyConfiguredProxyCanSupplyHttpsScheme(string remoteAddress, string expectedScheme)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration["Hosting:Enabled"] = "true";
        builder.Configuration["Hosting:PublicOrigin"] = "https://demo.example.com";
        builder.Configuration["Hosting:DataProtectionPath"] = Path.Combine(Path.GetTempPath(), "shelfsync-hosted-keys");
        builder.Configuration["Hosting:KnownProxies"] = "192.0.2.10";
        builder.AddHostedSecurity();
        await using var app = builder.Build();
        var options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>();
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, options);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.5";
        await middleware.Invoke(context);
        Assert.Equal(expectedScheme, context.Request.Scheme);
    }
}
