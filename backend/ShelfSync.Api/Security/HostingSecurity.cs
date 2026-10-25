using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

namespace ShelfSync.Api.Security;

public static class HostingSecurity
{
    public static void AddHostedSecurity(this WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("Hosting:Enabled")) return;
        if (!builder.Environment.IsProduction())
            throw new InvalidOperationException("Hosted deployments must run in Production.");
        foreach (var key in new[] { "ASPNETCORE_FORWARDEDHEADERS_ENABLED", "DOTNET_FORWARDEDHEADERS_ENABLED", "FORWARDEDHEADERS_ENABLED" })
            if (builder.Configuration.GetValue<bool>(key))
                throw new InvalidOperationException("Unrestricted forwarded headers are not supported. Configure Hosting:KnownProxies explicitly.");

        var origin = ValidateOrigin(builder.Configuration["Hosting:PublicOrigin"]);
        var keyPath = builder.Configuration["Hosting:DataProtectionPath"];
        if (string.IsNullOrWhiteSpace(keyPath) || !Path.IsPathFullyQualified(keyPath))
            throw new InvalidOperationException("Configure an absolute persistent Hosting:DataProtectionPath outside the application directory.");
        var fullPath = Path.GetFullPath(keyPath);
        var contentRoot = Path.GetFullPath(builder.Environment.ContentRootPath).TrimEnd(Path.DirectorySeparatorChar);
        if (fullPath.Equals(contentRoot, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(contentRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Data-protection keys must be stored outside the application directory.");

        var proxies = ParseProxies(builder.Configuration["Hosting:KnownProxies"]);
        builder.Configuration["AllowedHosts"] = origin.Host;
        builder.Services.AddDataProtection().SetApplicationName("ShelfSync")
            .PersistKeysToFileSystem(new DirectoryInfo(fullPath));
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in proxies) options.KnownProxies.Add(proxy);
            // Never clear both trust lists without replacing them: that trusts everyone.
            // A direct-TLS deployment needs no forwarded headers at all.
            if (proxies.Length == 0) options.ForwardedHeaders = ForwardedHeaders.None;
        });
        builder.Services.AddHsts(options => { options.MaxAge = TimeSpan.FromDays(30); options.IncludeSubDomains = false; });
    }

    public static Uri ValidateOrigin(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.HostNameType == UriHostNameType.Unknown || origin.Host.Contains('*') ||
            !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment) || origin.AbsolutePath != "/")
            throw new InvalidOperationException("Hosting:PublicOrigin must be a fixed HTTPS origin without a path or credentials.");
        return origin;
    }

    public static IPAddress[] ParseProxies(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(address => IPAddress.TryParse(address, out var parsed) ? parsed :
                throw new InvalidOperationException("Hosting:KnownProxies accepts only comma-separated explicit IP addresses.")).ToArray();
}
