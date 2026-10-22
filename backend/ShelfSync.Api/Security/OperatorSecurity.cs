using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using System.Threading.RateLimiting;

namespace ShelfSync.Api.Security;

public sealed record LoginRequest(string? Username, string? Password);

public static class OperatorSecurity
{
    public static void AddOperatorSecurity(this WebApplicationBuilder builder)
    {
        var hash = builder.Configuration["Operator:PasswordHash"];
        if (string.IsNullOrWhiteSpace(hash))
            throw new InvalidOperationException("Configure Operator:PasswordHash using scripts/Set-Operator.ps1 before starting the API.");
        builder.Services.AddSingleton<IPasswordHasher<string>, PasswordHasher<string>>();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = "ShelfSync.Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromHours(4);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }

    public static void UseOperatorSecurity(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!context.Request.Path.StartsWithSegments("/api/auth") && context.User.Identity?.IsAuthenticated != true)
                { context.Response.StatusCode = 401; return; }
                if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
                {
                    try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                    catch (AntiforgeryValidationException)
                    { await Results.Json(new { error = "Session security token expired. Sign in again." }, statusCode: 400).ExecuteAsync(context); return; }
                }
            }
            await next(context);
        });
        app.MapGet("/api/auth/session", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Ok(new { authenticated = context.User.Identity?.IsAuthenticated == true, token = antiforgery.GetAndStoreTokens(context).RequestToken }));
        app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context, IConfiguration configuration, IPasswordHasher<string> hasher) =>
        {
            var username = configuration["Operator:Username"] ?? "operator";
            var valid = request.Password is { Length: > 0 and <= 1024 } &&
                hasher.VerifyHashedPassword(username, configuration["Operator:PasswordHash"]!, request.Password) != PasswordVerificationResult.Failed;
            if (!valid || request.Username != username) return Results.Unauthorized();
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, "Operator")], CookieAuthenticationDefaults.AuthenticationScheme)));
            return Results.Ok(new { authenticated = true });
        }).RequireRateLimiting("login");
        app.MapPost("/api/auth/logout", async (HttpContext context) =>
        { await context.SignOutAsync(); return Results.Ok(); }).RequireAuthorization();
    }
}
