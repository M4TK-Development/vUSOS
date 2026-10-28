using System.Globalization;
using Microsoft.Data.Sqlite;
using Vusos.Core.Platform;

namespace Vusos.Core.Offline;

/// <summary>
/// Lokalna baza SQLite (<c>{DataDirectory}/vusos.db</c>, WAL, klucze obce) z idempotentnymi migracjami.
/// Wątkowo bezpieczna: każda operacja otwiera własne połączenie, a dostęp jest serializowany semaforem.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    public const string FileName = "vusos.db";

    /// <summary>Kolejne migracje; indeks + 1 = numer wersji schematu. Każda jest idempotentna (IF NOT EXISTS).</summary>
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE IF NOT EXISTS cache (
            key        TEXT NOT NULL PRIMARY KEY,
            json       TEXT NOT NULL,
            fetched_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS materials (
            id              TEXT NOT NULL PRIMARY KEY,
            course_id       TEXT NOT NULL,
            module_id       TEXT NOT NULL,
            file_name       TEXT NOT NULL,
            remote_url      TEXT NOT NULL,
            local_path      TEXT NULL,
            size            INTEGER NULL,
            mime_type       TEXT NULL,
            remote_modified TEXT NULL,
            state           TEXT NOT NULL,
            error           TEXT NULL,
            updated_at      TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_materials_course ON materials(course_id);
        CREATE INDEX IF NOT EXISTS ix_materials_url ON materials(remote_url);
        CREATE TABLE IF NOT EXISTS quiz_attempts (
            attempt_id TEXT NOT NULL PRIMARY KEY,
            quiz_id    TEXT NOT NULL,
            json       TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS quiz_local_answers (
            attempt_id TEXT NOT NULL,
            name       TEXT NOT NULL,
            value      TEXT NOT NULL,
            PRIMARY KEY (attempt_id, name)
        );
        CREATE TABLE IF NOT EXISTS quiz_pending (
            id           TEXT NOT NULL PRIMARY KEY,
            attempt_id   TEXT NOT NULL,
            quiz_id      TEXT NOT NULL,
            answers_json TEXT NOT NULL,
            finish       INTEGER NOT NULL,
            created_at   TEXT NOT NULL,
            retry_count  INTEGER NOT NULL DEFAULT 0,
            last_error   TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS kv (
            key   TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL
        );
        """,
    ];

    private static readonly string[] DataTables = ["cache", "materials", "quiz_attempts", "quiz_local_answers", "quiz_pending", "kv"];

    private readonly string _path;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public SqliteDatabase(IAppPaths paths) : this(Path.Combine(paths.DataDirectory, FileName))
    {
    }

    private SqliteDatabase(string dbPath)
    {
        _path = dbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Baza w dowolnym pliku (testy, narzędzia).</summary>
    public static SqliteDatabase FromPath(string dbPath) => new(dbPath);

    public string DatabasePath => _path;

    /// <summary>Wykonuje operację na świeżo otwartym połączeniu (serializowane).</summary>
    public async Task<T> RunAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            return await action(conn).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RunAsync(Func<SqliteConnection, Task> action, CancellationToken ct = default) =>
        RunAsync<bool>(async c => { await action(c).ConfigureAwait(false); return true; }, ct);

    /// <summary>Jak <see cref="RunAsync{T}"/>, ale w transakcji (commit przy sukcesie).</summary>
    public Task<T> InTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken ct = default) =>
        RunAsync(async conn =>
        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            var result = await action(conn, tx).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }, ct);

    /// <summary>Czyści wszystkie dane użytkownika (wylogowanie). Schemat zostaje.</summary>
    public Task ClearAllAsync(CancellationToken ct = default) =>
        InTransactionAsync<bool>(async (conn, tx) =>
        {
            foreach (var table in DataTables)
                await ExecAsync(conn, tx, $"DELETE FROM {table}", ct).ConfigureAwait(false);
            return true;
        }, ct);

    public async Task<string?> GetKvAsync(string key, CancellationToken ct = default) =>
        await RunAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM kv WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    public Task SetKvAsync(string key, string value, CancellationToken ct = default) =>
        RunAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO kv(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SqliteConnection.ClearAllPools();
        _gate.Dispose();
    }

    // ---------- helpers współdzielone przez magazyny ----------

    internal static string Fmt(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static string? Fmt(DateTimeOffset? value) => value is null ? null : Fmt(value.Value);

    internal static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    internal static DateTimeOffset? ParseNullable(object? value) =>
        value is string s ? Parse(s) : null;

    internal static async Task ExecAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        if (!_initialized)
            await InitializeAsync(ct).ConfigureAwait(false);
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await ExecAsync(conn, null, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;", ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Wywoływane pod <see cref="_gate"/>.</summary>
    private async Task InitializeAsync(CancellationToken ct)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await ExecAsync(conn, null, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;", ct).ConfigureAwait(false);
        await ExecAsync(conn, null, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)", ct).ConfigureAwait(false);

        int current;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
            current = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        for (var v = current; v < Migrations.Length; v++)
        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await ExecAsync(conn, tx, Migrations[v], ct).ConfigureAwait(false);
            await ExecAsync(conn, tx, "DELETE FROM schema_version", ct).ConfigureAwait(false);
            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO schema_version(version) VALUES($v)";
                ins.Parameters.AddWithValue("$v", v + 1);
                await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        _initialized = true;
    }
}
