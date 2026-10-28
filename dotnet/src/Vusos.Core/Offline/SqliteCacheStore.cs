namespace Vusos.Core.Offline;

public sealed class SqliteCacheStore(SqliteDatabase db) : ICacheStore
{
    public Task<CacheEntry?> GetAsync(string key, CancellationToken ct = default) =>
        db.RunAsync<CacheEntry?>(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json, fetched_at FROM cache WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
                return null;
            return new CacheEntry(r.GetString(0), SqliteDatabase.Parse(r.GetString(1)));
        }, ct);

    public Task SetAsync(string key, string json, DateTimeOffset fetchedAt, CancellationToken ct = default) =>
        db.RunAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO cache(key, json, fetched_at) VALUES($k, $j, $t)
                ON CONFLICT(key) DO UPDATE SET json = excluded.json, fetched_at = excluded.fetched_at
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$j", json);
            cmd.Parameters.AddWithValue("$t", SqliteDatabase.Fmt(fetchedAt));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    public Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default) =>
        db.RunAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            // substr zamiast LIKE: brak problemów z '%' i '_' w kluczach.
            cmd.CommandText = "DELETE FROM cache WHERE substr(key, 1, length($p)) = $p";
            cmd.Parameters.AddWithValue("$p", prefix);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    public Task ClearAsync(CancellationToken ct = default) =>
        db.RunAsync(conn => SqliteDatabase.ExecAsync(conn, null, "DELETE FROM cache", ct), ct);
}
