using Microsoft.Data.Sqlite;

namespace Vusos.Core.Offline;

public sealed class SqliteMaterialStore(SqliteDatabase db) : IMaterialStore
{
    private const string Columns =
        "id, course_id, module_id, file_name, remote_url, local_path, size, mime_type, remote_modified, state, error, updated_at";

    public Task<IReadOnlyList<MaterialRecord>> ListAsync(string? courseId = null, CancellationToken ct = default) =>
        db.RunAsync<IReadOnlyList<MaterialRecord>>(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM materials " +
                              (courseId is null ? "" : "WHERE course_id = $c ") +
                              "ORDER BY course_id, module_id, file_name, rowid";
            if (courseId is not null)
                cmd.Parameters.AddWithValue("$c", courseId);
            return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
        }, ct);

    public Task<MaterialRecord?> GetByUrlAsync(string remoteUrl, CancellationToken ct = default) =>
        db.RunAsync<MaterialRecord?>(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM materials WHERE remote_url = $u LIMIT 1";
            cmd.Parameters.AddWithValue("$u", remoteUrl);
            var all = await ReadAllAsync(cmd, ct).ConfigureAwait(false);
            return all.Count == 0 ? null : all[0];
        }, ct);

    public Task UpsertAsync(MaterialRecord record, CancellationToken ct = default) =>
        db.RunAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO materials({Columns})
                VALUES($id, $course, $module, $name, $url, $path, $size, $mime, $rmod, $state, $err, $upd)
                ON CONFLICT(id) DO UPDATE SET
                    course_id = excluded.course_id, module_id = excluded.module_id, file_name = excluded.file_name,
                    remote_url = excluded.remote_url, local_path = excluded.local_path, size = excluded.size,
                    mime_type = excluded.mime_type, remote_modified = excluded.remote_modified,
                    state = excluded.state, error = excluded.error, updated_at = excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$id", record.Id);
            cmd.Parameters.AddWithValue("$course", record.CourseId);
            cmd.Parameters.AddWithValue("$module", record.ModuleId);
            cmd.Parameters.AddWithValue("$name", record.FileName);
            cmd.Parameters.AddWithValue("$url", record.RemoteUrl);
            cmd.Parameters.AddWithValue("$path", (object?)record.LocalPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$size", (object?)record.Size ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$mime", (object?)record.MimeType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rmod", (object?)SqliteDatabase.Fmt(record.RemoteModified) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$state", record.State.ToString());
            cmd.Parameters.AddWithValue("$err", (object?)record.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$upd", SqliteDatabase.Fmt(record.UpdatedAt));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    public Task RemoveAsync(string id, CancellationToken ct = default) =>
        db.RunAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM materials WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    private static async Task<IReadOnlyList<MaterialRecord>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<MaterialRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new MaterialRecord(
                Id: r.GetString(0),
                CourseId: r.GetString(1),
                ModuleId: r.GetString(2),
                FileName: r.GetString(3),
                RemoteUrl: r.GetString(4),
                LocalPath: r.IsDBNull(5) ? null : r.GetString(5),
                Size: r.IsDBNull(6) ? null : r.GetInt64(6),
                MimeType: r.IsDBNull(7) ? null : r.GetString(7),
                RemoteModified: r.IsDBNull(8) ? null : SqliteDatabase.Parse(r.GetString(8)),
                State: Enum.Parse<DownloadState>(r.GetString(9)),
                Error: r.IsDBNull(10) ? null : r.GetString(10),
                UpdatedAt: SqliteDatabase.Parse(r.GetString(11))));
        }
        return list;
    }
}
