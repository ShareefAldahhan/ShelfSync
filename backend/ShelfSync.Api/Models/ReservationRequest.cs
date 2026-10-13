
namespace ShelfSync.Api.Models;

public sealed record ReservationRequest(
	Guid ProductId,
	int Quantity
);
