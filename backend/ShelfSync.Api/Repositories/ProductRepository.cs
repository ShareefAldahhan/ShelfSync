
using Npgsql;
using ShelfSync.Api.Models;

namespace ShelfSync.Api.Repositories;

public sealed class ProductRepository(NpgsqlDataSource db)
{
	public async Task<List<ProductDto>> GetAllAsync(
		CancellationToken cancellationToken = default)
	{
		const string sql = """
            SELECT p.id, p.sku, p.name, p.price_cents,
                   i.shelf_units, i.picked_units,
                   i.safety_buffer, i.quarantined
            FROM products AS p
            JOIN inventory AS i ON i.product_id = p.id
            ORDER BY p.sku;
            """;

		var products = new List<ProductDto>();

		await using var command = db.CreateCommand(sql);
		await using var reader =
			await command.ExecuteReaderAsync(cancellationToken);

		while (await reader.ReadAsync(cancellationToken))
		{
			products.Add(ReadProduct(reader));
		}

		return products;
	}

	public async Task<ProductDto?> GetByIdAsync(
		Guid id,
		CancellationToken cancellationToken = default)
	{
		const string sql = """
            SELECT p.id, p.sku, p.name, p.price_cents,
                   i.shelf_units, i.picked_units,
                   i.safety_buffer, i.quarantined
            FROM products AS p
            JOIN inventory AS i ON i.product_id = p.id
            WHERE p.id = $1;
            """;

		await using var command = db.CreateCommand(sql);
		command.Parameters.AddWithValue(id);

		await using var reader =
			await command.ExecuteReaderAsync(cancellationToken);

		if (!await reader.ReadAsync(cancellationToken))
			return null;

		return ReadProduct(reader);
	}

	private static ProductDto ReadProduct(NpgsqlDataReader reader)
	{
		return new ProductDto(
			reader.GetGuid(0),
			reader.GetString(1),
			reader.GetString(2),
			reader.GetInt32(3),
			reader.GetInt32(4),
			reader.GetInt32(5),
			reader.GetInt32(6),
			reader.GetBoolean(7)
		);
	}
}
