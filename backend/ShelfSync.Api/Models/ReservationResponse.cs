
namespace ShelfSync.Api.Models;

public sealed record ReservationResponse(
	Guid OrderId,
	Guid ProductId,
	int Quantity,
	string State,
	DateTime ExpiresAt
);
