using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShelfSync.Api.Accounts;
using System.Threading.RateLimiting;

namespace ShelfSync.Api.Security;
public sealed record LoginRequest(string? Username,string? Password);
public sealed record RegisterRequest(string? Username,string? Password);

public static class OperatorSecurity
{
    public static void AddOperatorSecurity(this WebApplicationBuilder builder)
    {
        var hash=builder.Configuration["Operator:PasswordHash"];
        if(string.IsNullOrWhiteSpace(hash))throw new InvalidOperationException("Configure recovery administrator credentials with Set-Operator.ps1 before starting the API.");
        try{_=new PasswordHasher<string>().VerifyHashedPassword("operator",hash,"configuration-validation");}
        catch(Exception ex)when(ex is FormatException or ArgumentException or IndexOutOfRangeException){throw new InvalidOperationException("Operator:PasswordHash must be a valid ASP.NET Identity password hash.");}
        // Identity and inventory share one bounded pool; separate pools can
        // together exhaust PostgreSQL connections during concurrent requests.
        builder.Services.AddDbContext<AccountsDbContext>((services,options)=>
            options.UseNpgsql(services.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddIdentityCore<AppUser>(options=>
        {
            options.Password.RequiredLength=12;options.Password.RequireDigit=false;options.Password.RequireLowercase=false;options.Password.RequireUppercase=false;options.Password.RequireNonAlphanumeric=false;
            options.Lockout.MaxFailedAccessAttempts=5;options.Lockout.DefaultLockoutTimeSpan=TimeSpan.FromMinutes(15);options.Lockout.AllowedForNewUsers=true;
        }).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AccountsDbContext>().AddDefaultTokenProviders();
        builder.Services.Configure<DataProtectionTokenProviderOptions>(options=>options.TokenLifespan=TimeSpan.FromMinutes(30));
        builder.Services.AddScoped<IPasswordHasher<string>,PasswordHasher<string>>();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options=>
        {
            options.Cookie.Name="ShelfSync.Session";options.Cookie.HttpOnly=true;options.Cookie.SameSite=SameSiteMode.Strict;
            options.Cookie.SecurePolicy=builder.Environment.IsDevelopment()?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;
            options.ExpireTimeSpan=TimeSpan.FromHours(4);options.SlidingExpiration=false;
            options.Events.OnRedirectToLogin=c=>{c.Response.StatusCode=401;return Task.CompletedTask;};
            options.Events.OnRedirectToAccessDenied=c=>{c.Response.StatusCode=403;return Task.CompletedTask;};
            options.Events.OnValidatePrincipal=async c=>
            {
                var manager=c.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
                var id=c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                var user=id is null?null:await manager.FindByIdAsync(id);
                if(user is null||!user.Enabled||await manager.IsLockedOutAsync(user)||user.SecurityStamp!=c.Principal?.FindFirstValue("security_stamp"))
                {c.RejectPrincipal();await c.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);}
            };
        });
        builder.Services.AddAuthorization(options=>
        {
            options.AddPolicy("ApplicationUser",p=>p.RequireAuthenticatedUser().RequireRole(AppRoles.All));
            options.AddPolicy("ReserveInventory",p=>p.RequireAuthenticatedUser().RequireRole(AppRoles.Customer,AppRoles.Manager,AppRoles.Admin));
            options.AddPolicy("Staff",p=>p.RequireAuthenticatedUser().RequireRole(AppRoles.Staff));
            options.AddPolicy("Management",p=>p.RequireAuthenticatedUser().RequireRole(AppRoles.Management));
            options.AddPolicy("Admin",p=>p.RequireAuthenticatedUser().RequireRole(AppRoles.Admin));
        });
        builder.Services.AddAntiforgery(options=>{options.HeaderName="X-CSRF-TOKEN";options.Cookie.SameSite=SameSiteMode.Strict;options.Cookie.SecurePolicy=builder.Environment.IsDevelopment()?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;});
        builder.Services.AddRateLimiter(options=>
        {
            options.RejectionStatusCode=429;
            options.AddPolicy("payments",context=>RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirstValue(ClaimTypes.NameIdentifier)??context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=10,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
            options.AddPolicy("login",context=>RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=5,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
        });
    }
    public static void UseOperatorSecurity(this WebApplication app)
    {
        app.Use(async(context,next)=>
        {
            context.Response.Headers.XContentTypeOptions="nosniff";context.Response.Headers["Referrer-Policy"]="same-origin";
            context.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            if(context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.Headers.CacheControl="no-store";
                if(!app.Environment.IsDevelopment()&&!context.Request.IsHttps){await Results.Json(new{error="HTTPS is required."},statusCode:400).ExecuteAsync(context);return;}
            }
            await next(context);
        });
        app.UseDefaultFiles();app.UseStaticFiles();
        app.UseAuthentication();app.UseAuthorization();app.UseRateLimiter();
        app.Use(async(context,next)=>
        {
            if(context.Request.Path.StartsWithSegments("/api")&&context.Request.Path!="/api/payments/stripe-webhook"&&!HttpMethods.IsGet(context.Request.Method)&&!HttpMethods.IsHead(context.Request.Method))
            {
                try{await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);}
                catch(AntiforgeryValidationException){await Results.Json(new{error="Session security token expired. Refresh and sign in again."},statusCode:400).ExecuteAsync(context);return;}
            }
            await next(context);
        });
        app.MapGet("/api/auth/session",(HttpContext c,IAntiforgery csrf)=>Results.Ok(new{authenticated=c.User.Identity?.IsAuthenticated==true,username=c.User.Identity?.Name,userId=c.User.FindFirstValue(ClaimTypes.NameIdentifier),roles=c.User.FindAll(ClaimTypes.Role).Select(r=>r.Value),token=csrf.GetAndStoreTokens(c).RequestToken})).AllowAnonymous();
        app.MapPost("/api/auth/login",async(LoginRequest request,HttpContext c,UserManager<AppUser> users)=>
        {
            if(request.Username is null||request.Username.Length>64||request.Password is null||request.Password.Length>1024)return Results.Unauthorized();
            var user=await users.FindByNameAsync(request.Username.Trim());
            if(user is null||!user.Enabled||await users.IsLockedOutAsync(user))return Results.Unauthorized();
            if(!await users.CheckPasswordAsync(user,request.Password)){AccountBootstrap.Ensure(await users.AccessFailedAsync(user));return Results.Unauthorized();}
            AccountBootstrap.Ensure(await users.ResetAccessFailedCountAsync(user));
            await SignInAsync(c,user,users);return Results.Ok(new{authenticated=true});
        }).AllowAnonymous().RequireRateLimiting("login");
        app.MapPost("/api/auth/register",async(RegisterRequest request,HttpContext c,AccountsDbContext db,UserManager<AppUser> users,CancellationToken ct)=>
        {
            var input=new NewAccountRequest(request.Username??"",request.Password??"",AppRoles.Customer);
            if(!AccountEndpoints.ValidAccount(input))return Results.BadRequest(new{error="Choose a username (3–64 characters) and password (12–1024 characters)."});
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            var user=new AppUser{Id=Guid.NewGuid(),UserName=input.Username.Trim()};
            var created=await users.CreateAsync(user,input.Password);if(!created.Succeeded)return AccountEndpoints.Failure(created);
            var assigned=await users.AddToRoleAsync(user,AppRoles.Customer);if(!assigned.Succeeded)return AccountEndpoints.Failure(assigned);
            db.AccessEvents.Add(new(){ActorUserId=user.Id,TargetUserId=user.Id,Action="REGISTER_CUSTOMER",Detail="Customer self-registration"});
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);await SignInAsync(c,user,users);return Results.Ok(new{authenticated=true});
        }).AllowAnonymous().RequireRateLimiting("login");
        app.MapPost("/api/auth/logout",async(HttpContext c)=>{await c.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);return Results.Ok();}).RequireAuthorization();
        app.MapAccountEndpoints();app.MapMailPreviewEndpoints();
    }
    private static async Task SignInAsync(HttpContext context,AppUser user,UserManager<AppUser> users)
    {
        var claims=new List<Claim>{new(ClaimTypes.NameIdentifier,user.Id.ToString()),new(ClaimTypes.Name,user.UserName!),new("security_stamp",user.SecurityStamp!)};
        claims.AddRange((await users.GetRolesAsync(user)).Select(role=>new Claim(ClaimTypes.Role,role)));
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,new ClaimsPrincipal(new ClaimsIdentity(claims,CookieAuthenticationDefaults.AuthenticationScheme)));
    }
}
