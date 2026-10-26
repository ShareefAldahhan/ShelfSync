
using Npgsql;
using ShelfSync.Api.Models;

namespace ShelfSync.Api.Repositories;

public sealed class ProductRepository(NpgsqlDataSource db)
{
    public sealed record CatalogPage(List<ProductDto> Items, long Total, int Page, int PageSize);

    public async Task<CatalogPage> SearchAsync(string search, string department, string sort, int page, int pageSize, CancellationToken ct = default)
    {
        const string sql = """
            WITH filtered AS (
              SELECT p.id,p.sku,p.name,p.price_cents,i.shelf_units,i.picked_units,i.safety_buffer,i.quarantined
              FROM products p JOIN inventory i ON i.product_id=p.id
              WHERE ($1='' OR strpos(lower(p.name || ' ' || p.sku),lower($1))>0)
                AND ($2='All products' OR $2=CASE
                  WHEN p.name ~* 'chips|beans|cereal' THEN 'Pantry'
                  WHEN p.name ~* 'milk|eggs' THEN 'Dairy & eggs'
                  WHEN p.name ~* 'bread' THEN 'Bakery'
                  WHEN p.name ~* 'apples' THEN 'Produce'
                  ELSE 'Home & care' END)
            )
            SELECT items.*,totals.total FROM (SELECT count(*) AS total FROM filtered) totals
            LEFT JOIN LATERAL (
              SELECT * FROM filtered ORDER BY
                CASE WHEN $3='price-low' THEN price_cents END ASC,
                CASE WHEN $3='price-high' THEN price_cents END DESC,
                sku,id
              LIMIT $4 OFFSET $5
            ) items ON true;
            """;
        await using var command = db.CreateCommand(sql);
        command.Parameters.AddWithValue(search);
        command.Parameters.AddWithValue(department);
        command.Parameters.AddWithValue(sort);
        command.Parameters.AddWithValue(pageSize);
        command.Parameters.AddWithValue((page - 1) * pageSize);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<ProductDto>();
        long total = 0;
        while (await reader.ReadAsync(ct))
        {
            total = reader.GetInt64(8);
            if (!reader.IsDBNull(0)) items.Add(ReadProduct(reader));
        }
        return new(items, total, page, pageSize);
    }
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
