using Npgsql;
using ShelfSync.Api.Models;
using ShelfSync.Api.Services;

namespace ShelfSync.Tests;

public sealed class ReservationValidationTests
{
	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(101)]
	public async Task InvalidQuantity_Returns400(int quantity)
	{
		await using var db = NpgsqlDataSource.Create(
			"Host=localhost;Database=unused;Username=unused;Password=unused");

		var service = new ReservationService(db);

		var request = new ReservationRequest(
			Guid.NewGuid(),
			quantity);

		var result = await service.CreateAsync(
			request,
			Guid.NewGuid());

		Assert.Equal(400, result.StatusCode);
		Assert.Contains(
			"Invalid product or quantity.",
			result.Json);
	}

	[Fact]
	public async Task EmptyProductId_Returns400()
	{
		await using var db = NpgsqlDataSource.Create(
			"Host=localhost;Database=unused;Username=unused;Password=unused");

		var service = new ReservationService(db);

		var request = new ReservationRequest(
			Guid.Empty,
			1);

		var result = await service.CreateAsync(
			request,
			Guid.NewGuid());

		Assert.Equal(400, result.StatusCode);
	}
}