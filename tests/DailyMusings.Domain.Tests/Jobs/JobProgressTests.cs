using DailyMusings.Domain.Common;
using DailyMusings.Domain.Jobs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Jobs;

/// <summary>
/// docs/开发指导.md §6.6 and §14. Two job behaviours added for phase three: a payload so a job can carry a
/// user decision across a restart, and a way to yield the queue without recording a failure, which is what
/// makes §8.3's batched index rebuild possible inside a bounded retry budget.
/// </summary>
[TestClass]
public class JobProgressTests
{
    [TestMethod]
    public void A_rescheduled_job_goes_back_to_pending_without_spending_an_attempt()
    {
        var job = TestFactory.NewJob(JobType.EmbeddingRebuild);
        var start = TestFactory.Noon;
        var later = start.AddMinutes(1);

        Assert.IsTrue(job.TryClaim(start));
        Assert.AreEqual(1, job.AttemptCount);

        job.Reschedule(later);

        Assert.AreEqual(JobStatus.Pending, job.Status);
        Assert.AreEqual(0, job.AttemptCount, "A batch that made progress did not fail.");
        Assert.AreEqual(later, job.ScheduledAtUtc);
        Assert.IsNull(job.StartedAtUtc, "The job is no longer running, so its start time must not linger.");
        Assert.IsFalse(job.IsTerminal);
    }

    [TestMethod]
    public void A_rescheduled_job_can_be_claimed_again()
    {
        var job = TestFactory.NewJob(JobType.EmbeddingRebuild);

        Assert.IsTrue(job.TryClaim(TestFactory.Noon));
        job.Reschedule(TestFactory.Noon.AddMinutes(1));

        Assert.IsFalse(
            job.TryClaim(TestFactory.Noon.AddSeconds(30)),
            "The job is not due until its rescheduled time.");
        Assert.IsTrue(job.TryClaim(TestFactory.Noon.AddMinutes(2)));
    }

    [TestMethod]
    public void Only_a_running_job_can_be_rescheduled()
    {
        var pending = TestFactory.NewJob();
        TestFactory.ThrowsDomain("job.bad_state", () => pending.Reschedule(TestFactory.Noon));

        var succeeded = TestFactory.NewJob();
        succeeded.TryClaim(TestFactory.Noon);
        succeeded.Succeed(TestFactory.Noon);
        TestFactory.ThrowsDomain("job.bad_state", () => succeeded.Reschedule(TestFactory.Noon));
    }

    [TestMethod]
    public void A_bounded_payload_travels_with_the_job()
    {
        var job = ProcessingJob.Create(
            JobId.New(),
            JobType.ReflectionGeneration,
            "2026-03-10",
            IdempotencyKeys.ReflectionGeneration(TestFactory.Day(10)),
            TestFactory.Noon,
            """{"allowOverwriteOfManualEdits":true}""");

        Assert.AreEqual("""{"allowOverwriteOfManualEdits":true}""", job.Payload);

        var rehydrated = ProcessingJob.Rehydrate(
            job.Id,
            job.JobType,
            job.TargetId,
            JobStatus.Pending,
            0,
            job.ScheduledAtUtc,
            null,
            null,
            job.IdempotencyKey,
            null,
            null,
            null,
            job.Payload);

        Assert.AreEqual(job.Payload, rehydrated.Payload);
    }

    [TestMethod]
    public void An_oversized_payload_is_refused()
    {
        TestFactory.ThrowsDomain(
            "job.payload.too_long",
            () => ProcessingJob.Create(
                JobId.New(),
                JobType.ReflectionGeneration,
                "2026-03-10",
                null,
                TestFactory.Noon,
                new string('x', 4097)));
    }

    [TestMethod]
    public void A_rescheduled_job_is_not_reported_as_a_failure()
    {
        // §12 notifies on a terminal failure; a batch boundary must never look like one.
        var job = TestFactory.NewJob(JobType.EmbeddingRebuild);
        job.TryClaim(TestFactory.Noon);
        job.Reschedule(TestFactory.Noon.AddMinutes(1));

        Assert.IsNull(job.ErrorCode);
        Assert.IsNull(job.ErrorSummary);
        Assert.IsNull(job.CompletedAtUtc);
    }
}
