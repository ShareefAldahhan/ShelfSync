using ShelfSync.Api.Payments;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShelfSync.Api.Accounts;
using ShelfSync.Api.Endpoints;
using ShelfSync.Api.Repositories;
using ShelfSync.Api.Security;
using ShelfSync.Api.Services;

var builder=WebApplication.CreateBuilder(args);
if(args.Length==2&&args[0]=="--export-accounts-schema")
{
    using var schema=new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);
    File.WriteAllText(args[1],schema.Database.GenerateCreateScript());return;
}
var connection=builder.Configuration.GetConnectionString("ShelfSync")??throw new InvalidOperationException("Configure ConnectionStrings:ShelfSync.");
connection = ShelfSync.Api.DatabaseConnectionSettings.Normalize(connection);
builder.Configuration["ConnectionStrings:ShelfSync"]=connection;
builder.WebHost.ConfigureKestrel(options=>options.Limits.MaxRequestBodySize=64*1024);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
builder.Services.AddScoped<ProductRepository>();builder.Services.AddScoped<ProductDetailsRepository>();builder.Services.AddScoped<OrderRepository>();
builder.Services.AddScoped<ItemMaintenanceService>();builder.Services.AddScoped<ReservationService>();builder.Services.AddScoped<ReservationCancellationService>();
builder.Services.AddScoped<OrderPickingService>();builder.Services.AddScoped<OrderShortageService>();builder.Services.AddScoped<OrderShortageResolutionService>();
builder.Services.AddScoped<OrderFulfillmentService>();builder.Services.AddScoped<PosSaleService>();
builder.Services.AddSingleton<ReservationExpirationService>();builder.Services.AddHostedService<ReservationExpirationWorker>();
builder.Services.AddScoped<TransactionalOperation>();builder.Services.AddScoped<StoreOperationsService>();builder.Services.AddScoped<CartReservationService>();builder.Services.AddScoped<StoreOperationsRepository>();
builder.Services.AddHostedService<PaymentReconciliationWorker>();
builder.Services.AddSingleton<PaymentSettings>();builder.Services.AddSingleton<IStripeGateway,StripeGateway>();builder.Services.AddScoped<PaymentService>();
builder.AddHostedSecurity();builder.AddOperatorSecurity();builder.Services.AddProblemDetails();
var app=builder.Build();
if (builder.Configuration.GetValue<bool>("Hosting:Enabled"))
{
    app.UseForwardedHeaders();
    app.UseHsts();
}
app.UseExceptionHandler();app.UseOperatorSecurity();
if (!await ShelfSync.Api.DatabaseReadiness.CheckAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), CancellationToken.None))
    throw new InvalidOperationException("ShelfSync database schema is incomplete. Apply schema.sql only to a new database, then migrations 001 through 005 using a schema-owner account. See docs/setup.md. Startup never changes the schema.");
await AccountBootstrap.InitializeAsync(app.Services,app.Configuration);
app.MapBusinessEndpoints();app.MapPaymentEndpoints();
app.MapGet("/health/live",()=>Results.Ok(new{service="ShelfSync.Api",status="alive",timestamp=DateTimeOffset.UtcNow})).AllowAnonymous();
app.MapGet("/health/ready",async(NpgsqlDataSource db,CancellationToken ct)=>
{
    try{return await ShelfSync.Api.DatabaseReadiness.CheckAsync(db,ct)?Results.Ok(new{status="ready"}):Results.Json(new{status="not_ready"},statusCode:503);}
    catch(Exception e)when(e is NpgsqlException or TimeoutException){return Results.Json(new{status="not_ready"},statusCode:503);}
}).AllowAnonymous();
app.MapFallback("/api/{**path}",()=>Results.NotFound()).RequireAuthorization();
app.MapFallbackToFile("{*path:nonfile}","index.html").AllowAnonymous();
app.Run();
public partial class Program { }
