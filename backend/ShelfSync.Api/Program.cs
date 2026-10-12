
using Npgsql;
using ShelfSync.Api.Repositories;



var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("ShelfSync")
	?? throw new InvalidOperationException("Connection string 'ShelfSync' not found.");

var dataSource = NpgsqlDataSource.Create(connectionString);

builder.Services.AddSingleton(dataSource);
builder.Services.AddScoped<ProductRepository>();

var app = builder.Build();

// Liveness: checks whether the API process can respond.

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

app.Run();
