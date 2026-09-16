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

    /// <summary>
    /// One key per generation round of a day.
    /// <para>
    /// The round is the number of versions the day has already produced, which is what makes a day
    /// "generate once, then regenerate when it goes stale" expressible as two distinct keys. Without it the
    /// unique index would refuse the second generation of a day forever — the first job row still holds the
    /// key — and §7's catch-up path would be impossible to implement.
    /// </para>
    /// </summary>
    public static string ReflectionGeneration(ContentDate contentDate, int round = 0) =>
        $"reflection-generation:{contentDate}#{round}";

    public static string UnsourcedStatementCheck(ReflectionVersionId versionId) =>
        $"unsourced-check:{versionId}";

    /// <summary>
    /// Indexing one entry is per configuration fingerprint <em>and</em> per exact text.
    /// <para>
    /// The text is part of the key because §7 says 修改后的内容可参与未来主题检索: a revision changes what the entry
    /// should match, and a key that only named the entry would find the already-succeeded job and decline to
    /// re-embed it — leaving semantic retrieval answering with the wording the user replaced.
    /// </para>
    /// </summary>
    public static string EmbeddingIndex(InputEntryId inputId, string embeddingConfigVersion, string textHash) =>
        $"embedding-index:{inputId}:{embeddingConfigVersion}:{textHash}";

    public static string Publication(ReflectionVersionId versionId, PublishTargetId targetId, int round = 0) =>
        $"publication:{versionId}:{targetId}#{round}";

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
        DateTimeOffset scheduledAtUtc,
        string? payload)
    {
        Id = id;
        JobType = jobType;
        TargetId = targetId;
        IdempotencyKey = idempotencyKey;
        ScheduledAtUtc = scheduledAtUtc;
        Payload = payload;
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

    /// <summary>
    /// Optional request parameters, stored as JSON.
    /// <para>
    /// Exists because some work cannot be described by its target alone: regenerating a day needs to know
    /// whether the user already accepted losing a hand-edited working version (§6.4), and that decision has to
    /// survive a restart just like the job does. Encoding it into <see cref="IdempotencyKey"/> would work but
    /// would put meaning into a string that is supposed to be opaque.
    /// </para>
    /// </summary>
    public string? Payload { get; }

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
        DateTimeOffset scheduledAtUtc,
        string? payload = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ValidatePayload(payload);

        return new ProcessingJob(id, jobType, targetId, idempotencyKey, scheduledAtUtc, payload);
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
        DateTimeOffset? nextAttemptAtUtc,
        string? payload = null)
    {
        var job = new ProcessingJob(id, jobType, targetId, idempotencyKey, scheduledAtUtc, payload)
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

    /// <summary>
    /// Returns a running job to the queue because it made progress and has more to do, without recording a
    /// failure.
    /// <para>
    /// Batched work needs this: §8.3 requires the embedding rebuild to run in batches, and treating every
    /// batch boundary as a failed attempt would exhaust §14's budget part way through a large index. The
    /// attempt counter is refunded, because the attempt did not fail — it did exactly what it was asked to do.
    /// </para>
    /// </summary>
    public void Reschedule(DateTimeOffset at)
    {
        if (Status != JobStatus.Running)
        {
            throw new DomainException("job.bad_state", $"Only a running job can be rescheduled (status is {Status}).");
        }

        Status = JobStatus.Pending;
        AttemptCount = Math.Max(0, AttemptCount - 1);
        ScheduledAtUtc = at;
        NextAttemptAtUtc = at;
        StartedAtUtc = null;
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

    /// <summary>
    /// A payload is stored in the job row and travels through the database, so it is bounded: an unbounded
    /// one would let a caller turn the queue into a blob store. 4 KiB is far more than any current job needs.
    /// </summary>
    private static void ValidatePayload(string? payload)
    {
        if (payload is { Length: > 4096 })
        {
            throw new DomainException("job.payload.too_long", "A job payload may not exceed 4096 characters.");
        }
    }
}
