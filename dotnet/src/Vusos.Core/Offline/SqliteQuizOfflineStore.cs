using Microsoft.Data.Sqlite;
using Vusos.Core.Domain;
using Vusos.Core.Services.Json;

namespace Vusos.Core.Offline;

public sealed class SqliteQuizOfflineStore(SqliteDatabase db) : IQuizOfflineStore
{
    public Task SaveAttemptDataAsync(QuizAttemptData data, CancellationToken ct = default) =>
        db.RunAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO quiz_attempts(attempt_id, quiz_id, json) VALUES($a, $q, $j)
                ON CONFLICT(attempt_id) DO UPDATE SET quiz_id = excluded.quiz_id, json = excluded.json
                """;
            cmd.Parameters.AddWithValue("$a", data.Attempt.Id);
            cmd.Parameters.AddWithValue("$q", data.Attempt.QuizId);
            cmd.Parameters.AddWithValue("$j", VusosJson.Serialize(data));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    public Task<QuizAttemptData?> GetAttemptDataAsync(string attemptId, CancellationToken ct = default) =>
        db.RunAsync<QuizAttemptData?>(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM quiz_attempts WHERE attempt_id = $a";
            cmd.Parameters.AddWithValue("$a", attemptId);
            var json = (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return json is null ? null : VusosJson.Deserialize<QuizAttemptData>(json);
        }, ct);

    public Task SaveLocalAnswersAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, CancellationToken ct = default) =>
        db.InTransactionAsync<bool>(async (conn, tx) =>
        {
            foreach (var a in answers)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO quiz_local_answers(attempt_id, name, value) VALUES($a, $n, $v)
                    ON CONFLICT(attempt_id, name) DO UPDATE SET value = excluded.value
                    """;
                cmd.Parameters.AddWithValue("$a", attemptId);
                cmd.Parameters.AddWithValue("$n", a.Name);
                cmd.Parameters.AddWithValue("$v", a.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return true;
        }, ct);

    public Task<IReadOnlyList<QuizAnswer>> GetLocalAnswersAsync(string attemptId, CancellationToken ct = default) =>
        db.RunAsync<IReadOnlyList<QuizAnswer>>(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name, value FROM quiz_local_answers WHERE attempt_id = $a ORDER BY name";
            cmd.Parameters.AddWithValue("$a", attemptId);
            var list = new List<QuizAnswer>();
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                list.Add(new QuizAnswer(r.GetString(0), r.GetString(1)));
            return list;
        }, ct);

    public Task EnqueueAsync(PendingQuizAction action, CancellationToken ct = default) =>
        db.RunAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO quiz_pending(id, attempt_id, quiz_id, answers_json, finish, created_at, retry_count, last_error)
                VALUES($id, $a, $q, $j, $f, $c, $r, $e)
                ON CONFLICT(id) DO UPDATE SET
                    attempt_id = excluded.attempt_id, quiz_id = excluded.quiz_id, answers_json = excluded.answers_json,
                    finish = excluded.finish, created_at = excluded.created_at,
                    retry_count = excluded.retry_count, last_error = excluded.last_error
                """;
            cmd.Parameters.AddWithValue("$id", action.Id);
            cmd.Parameters.AddWithValue("$a", action.AttemptId);
            cmd.Parameters.AddWithValue("$q", action.QuizId);
            cmd.Parameters.AddWithValue("$j", VusosJson.Serialize(action.Answers));
            cmd.Parameters.AddWithValue("$f", action.Finish ? 1 : 0);
            cmd.Parameters.AddWithValue("$c", SqliteDatabase.Fmt(action.CreatedAt));
            cmd.Parameters.AddWithValue("$r", action.RetryCount);
            cmd.Parameters.AddWithValue("$e", (object?)action.LastError ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    public Task<IReadOnlyList<PendingQuizAction>> ListPendingAsync(CancellationToken ct = default) =>
        db.RunAsync<IReadOnlyList<PendingQuizAction>>(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, attempt_id, quiz_id, answers_json, finish, created_at, retry_count, last_error
                FROM quiz_pending ORDER BY created_at, rowid
                """;
            var list = new List<PendingQuizAction>();
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var answers = VusosJson.Deserialize<List<QuizAnswer>>(r.GetString(3)) ?? [];
                list.Add(new PendingQuizAction(
                    Id: r.GetString(0),
                    AttemptId: r.GetString(1),
                    QuizId: r.GetString(2),
                    Answers: answers,
                    Finish: r.GetInt32(4) != 0,
                    CreatedAt: SqliteDatabase.Parse(r.GetString(5)),
                    RetryCount: r.GetInt32(6),
                    LastError: r.IsDBNull(7) ? null : r.GetString(7)));
            }
            return list;
        }, ct);

    public Task CompleteAsync(string actionId, CancellationToken ct = default) =>
        db.RunAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM quiz_pending WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", actionId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    public Task MarkFailedAsync(string actionId, string errorMessage, CancellationToken ct = default) =>
        db.RunAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE quiz_pending SET retry_count = retry_count + 1, last_error = $e WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", actionId);
            cmd.Parameters.AddWithValue("$e", errorMessage);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);
}
