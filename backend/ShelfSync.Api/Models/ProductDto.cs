
namespace ShelfSync.Api.Models;

public sealed record ProductDto(
	Guid Id,
	string Sku,
	string Name,
	int PriceCents,
	int ShelfUnits,
	int PickedUnits,
	int SafetyBuffer,
	bool Quarantined
);
