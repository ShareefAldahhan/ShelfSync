using Npgsql;

namespace ShelfSync.Api;

public static class DatabaseReadiness
{
    // Check the schema features used by this release, not just connectivity.
    // This deliberately performs no DDL and reveals no schema details to HTTP clients.
    public static async Task<bool> CheckAsync(NpgsqlDataSource database, CancellationToken ct)
    {
        await using var command = database.CreateCommand("""
            SELECT
              NOT EXISTS (
                SELECT 1 FROM unnest(ARRAY[
                  'products','inventory','orders','operation_requests','pos_events',
                  'inventory_events','feed_status','product_profiles','app_users',
                  'app_roles','app_user_roles','app_user_claims','app_role_claims',
                  'app_user_logins','app_user_tokens','access_audit','inventory_returns',
                  'mail_preview','payments','payment_events','payment_orders','order_payment_status'
                ]) AS required(name) WHERE to_regclass(name) IS NULL
              ) AND NOT EXISTS (
                SELECT 1 FROM (VALUES
                  ('inventory','backroom_units'),('inventory','damaged_units'),
                  ('inventory','reorder_point'),('orders','owner_user_id'),
                  ('orders','cart_id'),('payments','cart_id')
                ) AS required(table_name,column_name)
                WHERE NOT EXISTS (SELECT 1 FROM pg_attribute a
                  WHERE a.attrelid=to_regclass(required.table_name)
                    AND a.attname=required.column_name AND NOT a.attisdropped)
              );
            """);
        return await command.ExecuteScalarAsync(ct) is true;
    }
}
