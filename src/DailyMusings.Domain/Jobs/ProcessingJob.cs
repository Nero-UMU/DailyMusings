using DailyMusings.Domain.Common;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Time;

namespace DailyMusings.Domain.Jobs;

/// <summary>
/// Every unit of background work in the product (docs/开发指导.md §6.6). Transcription, indexing,
/// generation, checks, notification, publication, backup and audio cleanup are all jobs — none of them
/// may live only in process memory, because §14 requires them to survive a restart.
/// </summary>
public enum JobType
{
    Transcription = 0,
    EmbeddingIndex = 1,
    EmbeddingRebuild = 2,
    ReflectionGeneration = 3,
    UnsourcedStatementCheck = 4,
    Notification = 5,
    Publication = 6,
    Backup = 7,

    /// <summary>Retention sweep for audio blobs (decision A.1).</summary>
    AudioCleanup = 8,
}

public enum JobStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,

    /// <summary>
    /// Terminal failure: retries are exhausted. This is the <em>task</em> state, deliberately distinct
    /// from <see cref="ReflectionStatus.Failed"/>, which is the <em>draft</em> state (A.3).
    /// </summary>
    Failed = 3,
}

/// <summary>
/// Exponential backoff with a capped attempt count (§14: 临时错误采用指数退避，默认最多三次).
/// </summary>
public sealed record RetryPolicy(int MaxAttempts, TimeSpan BaseDelay)
{
    public static RetryPolicy Default { get; } = new(3, TimeSpan.FromMinutes(1));

    /// <summary>Delay to observe after the attempt numbered <paramref name="failedAttemptNumber"/> fails.</summary>
    public TimeSpan DelayFor(int failedAttemptNumber)
    {
        if (failedAttemptNumber < 1)
        {
            throw new DomainException("retry.attempt.out_of_range", "Attempt numbers start at 1.");
        }

        var exponent = Math.Min(failedAttemptNumber - 1, 10);
        return BaseDelay * Math.Pow(2, exponent);
    }

    public void Validate()
    {
        if (MaxAttempts < 1)
        {
            throw new DomainException("retry.max_attempts.out_of_range", "MaxAttempts must be at least 1.");
        }

        if (BaseDelay <= TimeSpan.Zero)
        {
            throw new DomainException("retry.base_delay.out_of_range", "BaseDelay must be positive.");
        }
    }
}

/// <summary>
/// Idempotency keys for the three operations §14 singles out. Centralizing them here means the "same
/// input transcribed twice cannot produce two rows" guarantee is expressed once, in the domain, rather
/// than re-derived at each call site.
/// </summary>
public static class IdempotencyKeys
{
    public static string Transcription(InputEntryId inputId) => $"transcription:{inputId}";

    public static string ReflectionGeneration(ContentDate contentDate) => $"reflection-generation:{contentDate}";

    public static string Publication(ReflectionVersionId versionId, PublishTargetId targetId) =>
        $"publication:{versionId}:{targetId}";

    public static string Notification(string eventKey, string targetId) => $"notification:{eventKey}:{targetId}";

    public static string IndexRebuild(string embeddingConfigVersion) => $"embedding-rebuild:{embeddingConfigVersion}";
}

/// <summary>A persisted unit of background work.</summary>
public sealed class ProcessingJob
{
    private ProcessingJob(
        JobId id,
        JobType jobType,
        string targetId,
        string? idempotencyKey,
        DateTimeOffset scheduledAtUtc)
    {
        Id = id;
        JobType = jobType;
        TargetId = targetId;
        IdempotencyKey = idempotencyKey;
        ScheduledAtUtc = scheduledAtUtc;
        Status = JobStatus.Pending;
    }

    public JobId Id { get; }

    public JobType JobType { get; }

    /// <summary>Identifier of the thing the job operates on — input id, content day, publication id…</summary>
    public string TargetId { get; }

    public JobStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset ScheduledAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>Unique across the job table, so a replayed request cannot enqueue a second copy (§14).</summary>
    public string? IdempotencyKey { get; }

    public string? ErrorCode { get; private set; }

    /// <summary>Redacted, log-safe summary. Never contains private content (§16).</summary>
    public string? ErrorSummary { get; private set; }

    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public bool IsTerminal => Status is JobStatus.Succeeded or JobStatus.Failed;

    public static ProcessingJob Create(
        JobId id,
        JobType jobType,
        string targetId,
        string? idempotencyKey,
        DateTimeOffset scheduledAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        return new ProcessingJob(id, jobType, targetId, idempotencyKey, scheduledAtUtc);
    }

    public static ProcessingJob Rehydrate(
        JobId id,
        JobType jobType,
        string targetId,
        JobStatus status,
        int attemptCount,
        DateTimeOffset scheduledAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? idempotencyKey,
        string? errorCode,
        string? errorSummary,
        DateTimeOffset? nextAttemptAtUtc)
    {
        var job = new ProcessingJob(id, jobType, targetId, idempotencyKey, scheduledAtUtc)
        {
            Status = status,
            AttemptCount = attemptCount,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            NextAttemptAtUtc = nextAttemptAtUtc,
        };

        return job;
    }

    public void Start(DateTimeOffset at)
    {
        if (IsTerminal)
        {
            throw new DomainException("job.already_terminal", $"A {Status} job cannot be started again.");
        }

        if (Status != JobStatus.Pending)
        {
            // Starting an already-running job would silently burn an attempt and lose the original
            // Start time, which is exactly the kind of double-execution §14 forbids.
            throw new DomainException("job.bad_state", $"Only a pending job can start (status is {Status}).");
        }

        Status = JobStatus.Running;
        AttemptCount++;
        StartedAtUtc = at;
        NextAttemptAtUtc = null;
    }

    public void Succeed(DateTimeOffset at)
    {
        if (Status != JobStatus.Running)
        {
            throw new DomainException("job.bad_state", $"Only a running job can succeed (status is {Status}).");
        }

        Status = JobStatus.Succeeded;
        CompletedAtUtc = at;
        ErrorCode = null;
        ErrorSummary = null;
        NextAttemptAtUtc = null;
    }

    /// <summary>
    /// Records a failed attempt. While attempts remain the job returns to <see cref="JobStatus.Pending"/>
    /// with an exponential backoff; once they are exhausted it becomes terminally failed, which is what
    /// the notification job reports to the user (§12, §14).
    /// </summary>
    public void Fail(string errorCode, string? errorSummary, DateTimeOffset at, RetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        if (Status != JobStatus.Running)
        {
            throw new DomainException("job.bad_state", $"Only a running job can fail (status is {Status}).");
        }

        ErrorCode = errorCode;
        ErrorSummary = errorSummary;

        if (AttemptCount >= policy.MaxAttempts)
        {
            Status = JobStatus.Failed;
            CompletedAtUtc = at;
            NextAttemptAtUtc = null;
            return;
        }

        Status = JobStatus.Pending;

        // AttemptCount is the attempt that just failed; each subsequent delay doubles.
        ScheduledAtUtc = at + policy.DelayFor(AttemptCount);
        NextAttemptAtUtc = ScheduledAtUtc;
    }

    /// <summary>
    /// Puts a terminally failed job back in line after the user fixed the cause (§14). The attempt
    /// counter is reset because this is a fresh decision by a human, not another automatic retry.
    /// </summary>
    public void Requeue(DateTimeOffset at)
    {
        // Only a failed job may be requeued: re-running a succeeded job would duplicate its effects.
        if (Status != JobStatus.Failed)
        {
            throw new DomainException(
                "job.not_failed",
                $"Only a terminally failed job can be requeued (status is {Status}).");
        }

        Status = JobStatus.Pending;
        AttemptCount = 0;
        ScheduledAtUtc = at;
        NextAttemptAtUtc = null;
        CompletedAtUtc = null;
        ErrorCode = null;
        ErrorSummary = null;
    }

    /// <summary>Claims a job whose scheduled time has arrived. Returns false when it is not due.</summary>
    public bool TryClaim(DateTimeOffset nowUtc)
    {
        if (Status != JobStatus.Pending || ScheduledAtUtc > nowUtc)
        {
            return false;
        }

        Start(nowUtc);
        return true;
    }
}
