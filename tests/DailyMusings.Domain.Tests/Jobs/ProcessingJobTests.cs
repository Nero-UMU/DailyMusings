using DailyMusings.Domain.Jobs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Jobs;

/// <summary>
/// docs/开发指导.md §14: work is persisted, transient errors back off exponentially, retries are bounded,
/// and a job that exhausted them can be put back in line by a human once the cause is fixed.
/// </summary>
[TestClass]
public class ProcessingJobTests
{
    private static readonly RetryPolicy Policy = new(MaxAttempts: 3, BaseDelay: TimeSpan.FromMinutes(1));

    [TestMethod]
    public void A_new_job_waits_for_its_scheduled_time()
    {
        var job = TestFactory.NewJob();

        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.AreEqual(0, job.AttemptCount);
        Assert.IsFalse(job.TryClaim(TestFactory.Noon.AddSeconds(-1)));
        Assert.IsTrue(job.TryClaim(TestFactory.Noon));
        Assert.AreEqual(JobStatus.Running, job.Status);
        Assert.AreEqual(1, job.AttemptCount);
    }

    [TestMethod]
    public void Transient_failures_back_off_exponentially_until_the_attempts_run_out()
    {
        var job = TestFactory.NewJob();

        var firstAttempt = TestFactory.Noon;
        job.Start(firstAttempt);
        job.Fail("model.timeout", "timed out", firstAttempt, Policy);

        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.AreEqual(firstAttempt + TimeSpan.FromMinutes(1), job.NextAttemptAtUtc);
        Assert.IsFalse(job.IsTerminal);

        var secondAttempt = job.ScheduledAtUtc;
        job.Start(secondAttempt);
        job.Fail("model.timeout", "timed out", secondAttempt, Policy);

        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.AreEqual(secondAttempt + TimeSpan.FromMinutes(2), job.NextAttemptAtUtc);

        var thirdAttempt = job.ScheduledAtUtc;
        job.Start(thirdAttempt);
        job.Fail("model.timeout", "timed out", thirdAttempt, Policy);

        // Third failure with MaxAttempts = 3 is terminal, which is what triggers the failure email (§12).
        Assert.AreEqual(JobStatus.Failed, job.Status);
        Assert.IsTrue(job.IsTerminal);
        Assert.AreEqual("model.timeout", job.ErrorCode);
        Assert.IsNull(job.NextAttemptAtUtc);
        Assert.AreEqual(thirdAttempt, job.CompletedAtUtc);
    }

    [TestMethod]
    public void A_successful_job_clears_its_previous_error()
    {
        var job = TestFactory.NewJob();
        job.Start(TestFactory.Noon);
        job.Fail("model.timeout", "timed out", TestFactory.Noon, Policy);

        job.Start(job.ScheduledAtUtc);
        job.Succeed(job.ScheduledAtUtc);

        Assert.AreEqual(JobStatus.Succeeded, job.Status);
        Assert.IsNull(job.ErrorCode);
        Assert.IsNull(job.ErrorSummary);
        Assert.IsNotNull(job.CompletedAtUtc);
    }

    [TestMethod]
    public void A_terminally_failed_job_can_be_requeued_by_a_human()
    {
        var job = TestFactory.NewJob();
        job.Start(TestFactory.Noon);
        job.Fail("e", "s", TestFactory.Noon, new RetryPolicy(1, TimeSpan.FromMinutes(1)));
        Assert.AreEqual(JobStatus.Failed, job.Status);

        job.Requeue(TestFactory.Noon.AddHours(1));

        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.AreEqual(0, job.AttemptCount);
        Assert.IsNull(job.CompletedAtUtc);
        Assert.IsNull(job.ErrorCode);
        Assert.AreEqual(TestFactory.Noon.AddHours(1), job.ScheduledAtUtc);
    }

    [TestMethod]
    public void State_machine_rejects_out_of_order_calls()
    {
        var job = TestFactory.NewJob();

        TestFactory.ThrowsDomain("job.bad_state", () => job.Succeed(TestFactory.Noon));

        job.Start(TestFactory.Noon);
        TestFactory.ThrowsDomain("job.bad_state", () => job.Start(TestFactory.Noon));

        job.Succeed(TestFactory.Noon);
        TestFactory.ThrowsDomain("job.already_terminal", () => job.Start(TestFactory.Noon));

        // Requeueing a job that already succeeded would duplicate its effects, so it is refused.
        TestFactory.ThrowsDomain("job.not_failed", () => job.Requeue(TestFactory.Noon));
    }

    [TestMethod]
    public void Retry_policy_rejects_impossible_configuration()
    {
        TestFactory.ThrowsDomain("retry.attempt.out_of_range", () => new RetryPolicy(3, TimeSpan.FromSeconds(1)).DelayFor(0));
        TestFactory.ThrowsDomain("retry.max_attempts.out_of_range", () => new RetryPolicy(0, TimeSpan.FromSeconds(1)).Validate());
        TestFactory.ThrowsDomain("retry.base_delay.out_of_range", () => new RetryPolicy(3, TimeSpan.Zero).Validate());
    }

    [TestMethod]
    public void Backoff_growth_is_capped_so_the_delay_cannot_overflow()
    {
        var policy = new RetryPolicy(64, TimeSpan.FromMinutes(1));

        Assert.AreEqual(TimeSpan.FromMinutes(1), policy.DelayFor(1));
        Assert.AreEqual(TimeSpan.FromMinutes(2), policy.DelayFor(2));
        Assert.AreEqual(TimeSpan.FromMinutes(4), policy.DelayFor(3));
        Assert.AreEqual(TimeSpan.FromMinutes(1024), policy.DelayFor(11));
        Assert.AreEqual(TimeSpan.FromMinutes(1024), policy.DelayFor(40));
    }

    /// <summary>
    /// §14 names exactly three operations that must be idempotent; their keys must stay stable. The generation
    /// key also carries a round, because §7 needs a day to be generatable again once it goes stale — without it
    /// the unique index would refuse the second generation of that day forever.
    /// </summary>
    [TestMethod]
    public void Idempotency_keys_are_stable_and_distinct_per_operation()
    {
        var inputId = Domain.Common.InputEntryId.New();
        var versionId = Domain.Common.ReflectionVersionId.New();
        var targetId = Domain.Common.PublishTargetId.New();
        var day = TestFactory.Day(3);

        Assert.AreEqual($"transcription:{inputId}", IdempotencyKeys.Transcription(inputId));
        Assert.AreEqual("reflection-generation:2026-03-03#0", IdempotencyKeys.ReflectionGeneration(day));
        Assert.AreEqual($"publication:{versionId}:{targetId}", IdempotencyKeys.Publication(versionId, targetId));

        // A first draft and the draft after new material arrived are different rounds of the same day.
        Assert.AreEqual("reflection-generation:2026-03-03#1", IdempotencyKeys.ReflectionGeneration(day, round: 1));
        Assert.AreNotEqual(
            IdempotencyKeys.ReflectionGeneration(day),
            IdempotencyKeys.ReflectionGeneration(day, round: 1));

        // Same day and round → same key, so a replayed request collides instead of duplicating.
        Assert.AreEqual(
            IdempotencyKeys.ReflectionGeneration(day),
            IdempotencyKeys.ReflectionGeneration(TestFactory.Day(3)));

        // Different day → different key.
        Assert.AreNotEqual(
            IdempotencyKeys.ReflectionGeneration(day),
            IdempotencyKeys.ReflectionGeneration(TestFactory.Day(4)));
    }

    [TestMethod]
    public void Job_types_cover_every_persisted_operation_the_product_needs()
    {
        var types = Enum.GetValues<JobType>();

        CollectionAssert.Contains(types, JobType.Transcription);
        CollectionAssert.Contains(types, JobType.ReflectionGeneration);
        CollectionAssert.Contains(types, JobType.Notification);
        CollectionAssert.Contains(types, JobType.Publication);
        CollectionAssert.Contains(types, JobType.EmbeddingRebuild);
        CollectionAssert.Contains(types, JobType.AudioCleanup);
    }
}
