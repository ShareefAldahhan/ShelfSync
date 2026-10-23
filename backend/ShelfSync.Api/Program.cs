using ShelfSync.Api.Security;

using Npgsql;
using ShelfSync.Api.Repositories;
using ShelfSync.Api.Models;
using ShelfSync.Api.Services;


var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("ShelfSync")
	?? throw new InvalidOperationException("Connection string 'ShelfSync' not found.");

var dataSource = NpgsqlDataSource.Create(connectionString);

builder.Services.AddSingleton(dataSource);
builder.Services.AddScoped<ProductRepository>();
builder.Services.AddScoped<ProductDetailsRepository>();
builder.Services.AddScoped<OrderRepository>();
builder.Services.AddScoped<ReservationCancellationService>();
builder.Services.AddScoped<ReservationService>();
builder.Services.AddSingleton<ReservationExpirationService>();
builder.Services.AddHostedService<ReservationExpirationWorker>();
builder.Services.AddScoped<OrderPickingService>();
builder.Services.AddScoped<OrderShortageService>();
builder.Services.AddScoped<OrderShortageResolutionService>();
builder.Services.AddScoped<OrderFulfillmentService>();
builder.Services.AddScoped<PosSaleService>();

builder.AddOperatorSecurity();
builder.Services.AddProblemDetails();
var app = builder.Build();
app.UseExceptionHandler();
app.UseOperatorSecurity();
app.UseDefaultFiles();
app.UseStaticFiles();

// Liveness: checks whether the API process can respond.

app.MapPost("/api/pos/sales", async (
	PosSaleRequest request,
	PosSaleService service,
	CancellationToken cancellationToken) =>
{
	var result = await service.RecordAsync(request, cancellationToken);

	return Results.Content(
		result.Json,
		"application/json",
		statusCode: result.StatusCode);
});


app.MapPost("/api/orders/{orderId:guid}/fulfill", async (
	Guid orderId,
	HttpRequest httpRequest,
	OrderFulfillmentService service,
	CancellationToken cancellationToken) =>
{
	if (!httpRequest.Headers.TryGetValue(
			"Idempotency-Key", out var keyValues) ||
		keyValues.Count != 1 ||
		!Guid.TryParse(keyValues[0], out var requestKey) ||
		requestKey == Guid.Empty)
	{
		return Results.BadRequest(new
		{
			error = "A valid Idempotency-Key UUID header is required."
		});
	}

	var result = await service.FulfillAsync(
		orderId,
		requestKey,
		cancellationToken);

	return Results.Content(
		result.Json,
		"application/json",
		statusCode: result.StatusCode);
});


app.MapPost("/api/orders/{orderId:guid}/resolve-shortage", async (
    Guid orderId,
    HttpRequest httpRequest,
    OrderShortageResolutionService service,
    CancellationToken cancellationToken) =>
{
    if (!httpRequest.Headers.TryGetValue(
            "Idempotency-Key", out var keyValues) ||
        keyValues.Count != 1 ||
        !Guid.TryParse(keyValues[0], out var requestKey) ||
        requestKey == Guid.Empty)
    {
        return Results.BadRequest(new
        {
            error = "A valid Idempotency-Key UUID header is required."
        });
    }

    var result = await service.ResolveAsync(
        orderId,
        requestKey,
        cancellationToken);

    return Results.Content(
        result.Json,
        "application/json",
        statusCode: result.StatusCode);
});


app.MapPost("/api/orders/{orderId:guid}/shortage", async (
	Guid orderId,
	HttpRequest httpRequest,
	OrderShortageService service,
	CancellationToken cancellationToken) =>
{
	if (!httpRequest.Headers.TryGetValue(
			"Idempotency-Key", out var keyValues) ||
		keyValues.Count != 1 ||
		!Guid.TryParse(keyValues[0], out var requestKey) ||
		requestKey == Guid.Empty)
	{
		return Results.BadRequest(new
		{
			error = "A valid Idempotency-Key UUID header is required."
		});
	}

	var result = await service.ReportAsync(
		orderId,
		requestKey,
		cancellationToken);

	return Results.Content(
		result.Json,
		"application/json",
		statusCode: result.StatusCode);
});


app.MapPost("/api/orders/{orderId:guid}/pick", async (
	Guid orderId,
	HttpRequest httpRequest,
	OrderPickingService service,
	CancellationToken cancellationToken) =>
{
	if (!httpRequest.Headers.TryGetValue(
			"Idempotency-Key", out var keyValues) ||
		keyValues.Count != 1 ||
		!Guid.TryParse(keyValues[0], out var requestKey) ||
		requestKey == Guid.Empty)
	{
		return Results.BadRequest(new
		{
			error = "A valid Idempotency-Key UUID header is required."
		});
	}

	var result = await service.PickAsync(
		orderId,
		requestKey,
		cancellationToken);

	return Results.Content(
		result.Json,
		"application/json",
		statusCode: result.StatusCode);
});

app.MapPost("/api/reservations", async (
	ReservationRequest request,
	HttpRequest httpRequest,
	ReservationService service,
	CancellationToken cancellationToken) =>
{
	if (!httpRequest.Headers.TryGetValue(
			"Idempotency-Key", out var keyValues) ||
		keyValues.Count != 1 ||
		!Guid.TryParse(keyValues[0], out var requestKey) ||
		requestKey == Guid.Empty)
	{
		return Results.BadRequest(new
		{
			error = "A valid Idempotency-Key UUID header is required."
		});
	}

	var result = await service.CreateAsync(
		request,
		requestKey,
		cancellationToken);

	return Results.Content(
		result.Json,
		"application/json",
		statusCode: result.StatusCode);
});

app.MapPost("/api/reservations/{orderId:guid}/cancel", async (
	Guid orderId,
	HttpRequest httpRequest,
	ReservationCancellationService service,
	CancellationToken cancellationToken) =>
{
	if (!httpRequest.Headers.TryGetValue(
			"Idempotency-Key", out var keyValues) ||
		keyValues.Count != 1 ||
		!Guid.TryParse(keyValues[0], out var requestKey) ||
		requestKey == Guid.Empty)
	{
		return Results.BadRequest(new
		{
			error = "A valid Idempotency-Key UUID header is required."
		});
	}

	var result = await service.CancelAsync(
		orderId,
		requestKey,
		cancellationToken);

	return Results.Content(
		result.Json,
		"application/json",
		statusCode: result.StatusCode);
});

app.MapGet("/api/products", async (
	ProductRepository repository,
	CancellationToken cancellationToken) =>
{
	var products = await repository.GetAllAsync(cancellationToken);
	return Results.Ok(products);
});

app.MapGet("/api/products/{id:guid}", async (
	Guid id,
	ProductRepository repository,
	CancellationToken cancellationToken) =>
{
	var product = await repository.GetByIdAsync(
		id, cancellationToken);

	return product is null
		? Results.NotFound()
		: Results.Ok(product);
});

app.MapGet("/health/live", () => Results.Ok(new
{
	service = "ShelfSync.Api",
	status = "alive",
	timestamp = DateTimeOffset.UtcNow
}));

// Readiness: checks whether PostgreSQL is reachable and responding.
app.MapGet("/health/ready", async (NpgsqlDataSource db) =>
{
	try
	{
		await using var connection = await db.OpenConnectionAsync();
		await using var command = new NpgsqlCommand("SELECT 1;", connection);

		var result = await command.ExecuteScalarAsync();

		if (result is int value && value == 1)
		{
			return Results.Ok(new { status = "ready" });
		}

		return Results.Json(
			new { status = "not_ready" },
			statusCode: 503
		);
	}
	catch (NpgsqlException)
	{
		return Results.Json(
			new { status = "not_ready" },
			statusCode: 503
		);
	}
	catch (TimeoutException)
	{
		return Results.Json(
			new { status = "not_ready" },
			statusCode: 503
		);
	}
});

app.MapGet("/api/orders", async (OrderRepository repository, CancellationToken ct) => Results.Ok(await repository.GetRecentAsync(ct)));

app.MapFallbackToFile("{*path:nonfile}", "index.html");
app.MapGet("/api/products/{id:guid}/details", async (Guid id, ProductDetailsRepository repository, CancellationToken ct) => { var details = await repository.GetAsync(id, ct); return details is null ? Results.NotFound() : Results.Ok(details); });

app.Run();
public partial class Program { }
