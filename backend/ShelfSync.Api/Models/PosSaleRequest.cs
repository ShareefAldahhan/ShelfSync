namespace ShelfSync.Api.Models;

public sealed record PosSaleRequest(
	Guid EventId,
	Guid ProductId,
	int Quantity);