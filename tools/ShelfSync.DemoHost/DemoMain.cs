using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ShelfSync.Api.Accounts;
using ShelfSync.Api.Payments;

namespace ShelfSync.DemoHost;

public static class DemoMain
{
    public const string Password = "ShelfSync-Demo-Only-2026!";
    public static async Task Main()
    {
        var root = Directory.GetCurrentDirectory();
        var connection = Environment.GetEnvironmentVariable("SHELFSYNC_DEMO_CONNECTION") ?? throw new InvalidOperationException("Use node scripts/Demo.mjs.");
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (settings.Database is not ("shelfsync_demo" or "shelfsync_test") || settings.Host is not ("localhost" or "127.0.0.1"))
            throw new InvalidOperationException("Demo requires an isolated loopback demo/test database.");
        var port = int.Parse(Environment.GetEnvironmentVariable("SHELFSYNC_DEMO_PORT") ?? "5092");
        var origin = "http://127.0.0.1:" + port;
        var webhookSecret = "whsec_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var gateway = new DemoGateway();
        // Set the connection before the application entry point reads configuration.
        // Factory callbacks run too late for configuration consumed in top-level startup.
        Environment.SetEnvironmentVariable("ConnectionStrings__ShelfSync", connection);
        Environment.SetEnvironmentVariable("Operator__Username", "demo-admin");
        Environment.SetEnvironmentVariable("Operator__PasswordHash", new PasswordHasher<string>().HashPassword("demo-admin", Password));
        Environment.SetEnvironmentVariable("Stripe__Enabled", "true");
        Environment.SetEnvironmentVariable("Stripe__SecretKey", "sk_test_demo");
        Environment.SetEnvironmentVariable("Stripe__WebhookSecret", webhookSecret);
        Environment.SetEnvironmentVariable("Stripe__BaseUrl", origin);
        Environment.SetEnvironmentVariable("Hosting__Enabled", "false");
        await using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development").UseContentRoot(Path.Combine(root, "backend/ShelfSync.Api"))
                .UseWebRoot(Path.Combine(root, "frontend/dist"));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ShelfSync"] = connection,
                ["Operator:Username"] = "demo-admin",
                ["Operator:PasswordHash"] = new PasswordHasher<string>().HashPassword("demo-admin", Password),
                ["Stripe:Enabled"] = "true", ["Stripe:SecretKey"] = "sk_test_demo", ["Stripe:WebhookSecret"] = webhookSecret,
                ["Stripe:BaseUrl"] = origin, ["Hosting:Enabled"] = "false", ["Logging:LogLevel:Default"] = "Warning"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IStripeGateway>();
                services.AddSingleton<IStripeGateway>(gateway);
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.AddSingleton<IStartupFilter>(new DemoRoutes(gateway, origin, webhookSecret));
            });
        });
        factory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, port));
        factory.StartServer();
        await using (var identity = factory.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT current_database(),current_user,inet_server_port()"))
        await using (var reader = await identity.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            if (reader.GetString(0) != settings.Database || reader.GetString(1) != settings.Username || reader.GetInt32(2) != settings.Port)
                throw new InvalidOperationException("Connected demo database identity differs from the requested isolated database.");
        }
        if (factory.Services.GetRequiredService<IStripeGateway>() is not DemoGateway)
            throw new InvalidOperationException("Demo payment gateway was not installed; refusing to seed accounts or run.");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            foreach (var (name, role) in new[] { ("demo-customer", AppRoles.Customer), ("demo-associate", AppRoles.Associate), ("demo-manager", AppRoles.Manager) })
            {
                if (await users.FindByNameAsync(name) is not null) continue;
                var user = new AppUser { UserName = name };
                AccountBootstrap.Ensure(await users.CreateAsync(user, Password));
                AccountBootstrap.Ensure(await users.AddToRoleAsync(user, role));
            }
        }
        Console.WriteLine($"DEMO_READY {origin}");
        using var stop = new CancellationTokenSource();
        using var shutdown = factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(stop.Cancel);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
    }
}

internal sealed class DemoRoutes(DemoGateway gateway, string origin, string secret) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, proceed) =>
        {
            if (context.Request.Path == "/__demo/info")
            {
                await context.Response.WriteAsJsonAsync(new { simulatedPayments = true });
                return;
            }
            if (context.Request.Path.StartsWithSegments("/__demo/checkout", out var remaining))
            {
                var session = remaining.Value?.Trim('/');
                if (session is null || !gateway.Sessions.TryGetValue(session, out var existing)) { context.Response.StatusCode = 404; return; }
                var auth = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                if (!auth.Succeeded) { context.Response.StatusCode = 401; return; }
                context.User = auth.Principal!;
                var db = context.RequestServices.GetRequiredService<NpgsqlDataSource>();
                await using var owner = db.CreateCommand("SELECT owner_user_id FROM orders WHERE id=$1");
                owner.Parameters.AddWithValue(existing.Order);
                if (await owner.ExecuteScalarAsync() is not Guid user || user != AccountEndpoints.UserId(context.User)) { context.Response.StatusCode = 403; return; }
                var csrf = context.RequestServices.GetRequiredService<IAntiforgery>();
                if (HttpMethods.IsPost(context.Request.Method))
                {
                    try { await csrf.ValidateRequestAsync(context); } catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; return; }
                    var confirmed = gateway.Confirm(session);
                    var raw = System.Text.Json.JsonSerializer.Serialize(new { id = "evt_demo_paid_" + session, type = "checkout.session.completed", livemode = false, data = new { @object = new { id = session, @object = "checkout.session" } } });
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(now + "." + raw))).ToLowerInvariant();
                    using var http = new HttpClient();
                    using var request = new HttpRequestMessage(HttpMethod.Post, origin + "/api/payments/stripe-webhook") { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
                    request.Headers.Add("Stripe-Signature", $"t={now},v1={signature}");
                    using var response = await http.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                    context.Response.Redirect(origin + "/?payment=returned#payment/" + confirmed.Order);
                    return;
                }
                var tokens = csrf.GetAndStoreTokens(context);
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync($$"""
                    <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
                    <title>ShelfSync simulated checkout</title><body style="font:18px system-ui;max-width:600px;margin:60px auto;padding:20px;background:#faf9f5;color:#193a30">
                    <h1>Simulated checkout</h1><p>This isolated demo does not contact Stripe or collect money. Do not enter card details.</p>
                    <p>Demo total: USD {{existing.Snapshot.Amount / 100m:F2}}</p>
                    <form method="post"><input type="hidden" name="{{tokens.FormFieldName}}" value="{{tokens.RequestToken}}">
                    <button type="submit" style="padding:16px;background:#204d3e;color:white;border:0;border-radius:6px">Confirm simulated payment</button></form>
                    <p>The demo sends a signed event through the app's actual webhook endpoint, then shows the receipt.</p></body></html>
                    """);
                return;
            }
            // Rewrite only this host's simulated checkout URLs after the real API validates them.
            if (HttpMethods.IsPost(context.Request.Method) && (context.Request.Path == "/api/cart/checkout" || context.Request.Path.Value?.EndsWith("/checkout", StringComparison.Ordinal) == true))
            {
                var original = context.Response.Body;
                await using var buffer = new MemoryStream();
                context.Response.Body = buffer;
                try
                {
                    await proceed();
                    buffer.Position = 0;
                    var json = await new StreamReader(buffer).ReadToEndAsync();
                    json = json.Replace("https://checkout.stripe.com/c/pay/cs_test_demo_", origin + "/__demo/checkout/cs_test_demo_", StringComparison.Ordinal);
                    context.Response.ContentLength = null;
                    await original.WriteAsync(Encoding.UTF8.GetBytes(json));
                }
                finally { context.Response.Body = original; }
                return;
            }
            await proceed();
        });
        next(app);
    };
}
