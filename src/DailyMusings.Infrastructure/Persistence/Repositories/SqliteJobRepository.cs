using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Jobs;
using Microsoft.Data.Sqlite;

namespace DailyMusings.Infrastructure.Persistence.Repositories;

/// <summary>
/// The durable work queue (docs/开发指导.md §6.6, §14).
/// <para>
/// <see cref="TryClaimAsync"/> is the concurrency boundary. It performs the whole claim — status, attempt count,
/// start time — in one guarded statement, so two executors can never both run the same job and a crash mid-claim
/// cannot leave a half-claimed row.
/// </para>
/// </summary>
public sealed class SqliteJobRepository : IJobRepository
{
    private const string Columns = """
        id, job_type, target_id, status, attempt_count, scheduled_at_utc, started_at_utc, completed_at_utc,
        idempotency_key, error_code, error_summary, next_attempt_at_utc, payload_json
        """;

    private readonly SqliteConnectionAccessor _accessor;

    public SqliteJobRepository(SqliteConnectionAccessor accessor) => _accessor = accessor;

    public async Task AddAsync(ProcessingJob job, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            """
            INSERT INTO processing_job
                (id, job_type, target_id, status, attempt_count, scheduled_at_utc, started_at_utc, completed_at_utc,
                 idempotency_key, error_code, error_summary, next_attempt_at_utc, payload_json)
            VALUES
                ($id, $jobType, $targetId, $status, $attempts, $scheduledAt, $startedAt, $completedAt,
                 $idempotencyKey, $errorCode, $errorSummary, $nextAttemptAt, $payload);
            """,
            cancellationToken,
            ("$id", job.Id.ToString()),
            ("$jobType", (int)job.JobType),
            ("$targetId", job.TargetId),
            ("$status", (int)job.Status),
            ("$attempts", job.AttemptCount),
            ("$scheduledAt", SqliteValues.Instant(job.ScheduledAtUtc)),
            ("$startedAt", SqliteValues.InstantOrNull(job.StartedAtUtc)),
            ("$completedAt", SqliteValues.InstantOrNull(job.CompletedAtUtc)),
            ("$idempotencyKey", SqliteValues.TextOrNull(job.IdempotencyKey)),
            ("$errorCode", SqliteValues.TextOrNull(job.ErrorCode)),
            ("$errorSummary", SqliteValues.TextOrNull(job.ErrorSummary)),
            ("$nextAttemptAt", SqliteValues.InstantOrNull(job.NextAttemptAtUtc)),
            ("$payload", SqliteValues.TextOrNull(job.Payload))).ConfigureAwait(false);

    public async Task<ProcessingJob?> FindByIdAsync(JobId id, CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            $"SELECT {Columns} FROM processing_job WHERE id = $id;",
            Map,
            cancellationToken,
            ("$id", id.ToString())).ConfigureAwait(false);

    public async Task<ProcessingJob?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            $"SELECT {Columns} FROM processing_job WHERE idempotency_key = $key LIMIT 1;",
            Map,
            cancellationToken,
            ("$key", idempotencyKey)).ConfigureAwait(false);

    public async Task<ProcessingJob?> FindByTypeAndTargetAsync(
        JobType jobType,
        string targetId,
        CancellationToken cancellationToken) =>
        await _accessor.QuerySingleAsync(
            $"SELECT {Columns} FROM processing_job WHERE job_type = $jobType AND target_id = $targetId LIMIT 1;",
            Map,
            cancellationToken,
            ("$jobType", (int)jobType),
            ("$targetId", targetId)).ConfigureAwait(false);

    public async Task<IReadOnlyList<ProcessingJob>> ListDueAsync(
        DateTimeOffset nowUtc,
        int limit,
        CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            $"""
             SELECT {Columns} FROM processing_job
              WHERE status = $pending AND scheduled_at_utc <= $now
              ORDER BY scheduled_at_utc
              LIMIT $limit;
             """,
            Map,
            cancellationToken,
            ("$pending", (int)JobStatus.Pending),
            ("$now", SqliteValues.Instant(nowUtc)),
            ("$limit", limit)).ConfigureAwait(false);

    public async Task<IReadOnlyList<ProcessingJob>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
        await _accessor.QueryAsync(
            $"SELECT {Columns} FROM processing_job ORDER BY scheduled_at_utc DESC LIMIT $limit;",
            Map,
            cancellationToken,
            ("$limit", limit)).ConfigureAwait(false);

    public async Task<bool> TryClaimAsync(JobId id, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        // One statement does the whole claim: flip to running, count the attempt, stamp the start, and clear the
        // backoff. `AND status = pending` is what makes the claim exclusive.
        var claimed = await _accessor.ExecuteAsync(
            """
            UPDATE processing_job
               SET status = $running,
                   attempt_count = attempt_count + 1,
                   started_at_utc = $now,
                   next_attempt_at_utc = NULL
             WHERE id = $id
               AND status = $pending;
            """,
            cancellationToken,
            ("$running", (int)JobStatus.Running),
            ("$pending", (int)JobStatus.Pending),
            ("$now", SqliteValues.Instant(nowUtc)),
            ("$id", id.ToString())).ConfigureAwait(false);

        return claimed == 1;
    }

    public async Task UpdateAsync(ProcessingJob job, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            """
            UPDATE processing_job
               SET status = $status,
                   attempt_count = $attempts,
                   scheduled_at_utc = $scheduledAt,
                   started_at_utc = $startedAt,
                   completed_at_utc = $completedAt,
                   error_code = $errorCode,
                   error_summary = $errorSummary,
                   next_attempt_at_utc = $nextAttemptAt
             WHERE id = $id;
            """,
            cancellationToken,
            ("$id", job.Id.ToString()),
            ("$status", (int)job.Status),
            ("$attempts", job.AttemptCount),
            ("$scheduledAt", SqliteValues.Instant(job.ScheduledAtUtc)),
            ("$startedAt", SqliteValues.InstantOrNull(job.StartedAtUtc)),
            ("$completedAt", SqliteValues.InstantOrNull(job.CompletedAtUtc)),
            ("$errorCode", SqliteValues.TextOrNull(job.ErrorCode)),
            ("$errorSummary", SqliteValues.TextOrNull(job.ErrorSummary)),
            ("$nextAttemptAt", SqliteValues.InstantOrNull(job.NextAttemptAtUtc))).ConfigureAwait(false);

    /// <summary>
    /// Returns every running job to pending. Called once at startup: this process has just begun, so nothing can
    /// legitimately be running yet, and a job left running by a killed process would otherwise be stuck forever
    /// — which is exactly the recovery §14 requires after a restart.
    /// </summary>
    public async Task<int> RecoverInterruptedAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        await _accessor.ExecuteAsync(
            """
            UPDATE processing_job
               SET status = $pending,
                   started_at_utc = NULL,
                   scheduled_at_utc = $now,
                   next_attempt_at_utc = NULL
             WHERE status = $running;
            """,
            cancellationToken,
            ("$pending", (int)JobStatus.Pending),
            ("$running", (int)JobStatus.Running),
            ("$now", SqliteValues.Instant(nowUtc))).ConfigureAwait(false);

    private static ProcessingJob Map(SqliteDataReader reader) =>
        ProcessingJob.Rehydrate(
            new JobId(SqliteIds.Parse(reader.GetString(0))),
            (JobType)reader.GetInt32(1),
            reader.GetString(2),
            (JobStatus)reader.GetInt32(3),
            reader.GetInt32(4),
            SqliteValues.ReadRequiredInstant(reader, 5),
            SqliteValues.ReadInstant(reader, 6),
            SqliteValues.ReadInstant(reader, 7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            SqliteValues.ReadInstant(reader, 11),
            reader.IsDBNull(12) ? null : reader.GetString(12));
}
