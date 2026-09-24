using DailyMusings.Application.Configuration;
using DailyMusings.Application.Embeddings;
using DailyMusings.Application.Operations;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Reflections;
using DailyMusings.Infrastructure.Operations;
using DailyMusings.Infrastructure.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// The instance's counters (docs/开发指导.md §16). Two libraries matter: an empty instance has to answer without
/// pretending anything happened, and a populated one has to count the things an operator expects to see.
/// </summary>
[TestClass]
public class StatisticsTests
{
    [TestMethod]
    public async Task An_empty_instance_reports_zeros_and_its_effective_configuration()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var statistics = await ReadAsync(context);

        Assert.AreEqual(context.Today.ToString(), statistics.Today);
        Assert.AreEqual(0, statistics.TodayInputCount);
        Assert.AreEqual(0, statistics.TotalInputCount);
        Assert.AreEqual(0, statistics.TotalReflectionCount);
        Assert.AreEqual(0, statistics.TopicCount);
        Assert.AreEqual(0, statistics.QueuePending);
        Assert.AreEqual(0, statistics.MediaBytes);
        Assert.AreEqual(InstanceStatistics.NoReflection, statistics.TodayReflectionStatus);

        // The retention windows are reported even when nothing has been swept: they are what the page says the
        // instance is configured to do, not what it has done.
        Assert.AreEqual(ContentSettings.DefaultAudioRetentionDays, statistics.AudioRetentionDays);
        Assert.AreEqual(ContentRetentionPolicy.KeepForever, statistics.ContentRetentionDays);

        // The index is switched off in this context, so semantic search is honestly unavailable.
        Assert.IsFalse(statistics.SemanticSearchAvailable);

        // The context's generation settings are on and its SMTP is off; both are reported as configured.
        Assert.IsTrue(statistics.GenerationEnabled);
        Assert.IsFalse(statistics.SmtpConfigured);
    }

    [TestMethod]
    public async Task A_populated_instance_counts_what_the_operator_expects_to_see()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var first = await context.CaptureTextAsync("今天跑了一圈。");
        await context.CaptureTextAsync("又记了一句。");

        var (topic, _) = await context.CreateTopic.ExecuteAsync("晨跑", CancellationToken.None);

        // Two days, one of them confirmed, so "drafts" and "confirmed" are distinguishable.
        var (_, version) = await context.SeedDraftAsync(context.Today, ReflectionStatus.Confirmed);
        await context.Reflections.SetVersionTopicsAsync(version.Id, [topic.Id], CancellationToken.None);

        var yesterday = context.Today.AddDays(-1);
        await context.CaptureTextAsync("昨天的一点记录。", yesterday);
        await context.SeedDraftAsync(yesterday, ReflectionStatus.ReviewRequired);

        var statistics = await ReadAsync(context);

        Assert.AreEqual(2, statistics.TodayInputCount);
        Assert.AreEqual(0, statistics.TodayVoiceCount);
        Assert.AreEqual(2, statistics.TodayTextCount);
        Assert.AreEqual(3, statistics.TotalInputCount);
        Assert.AreEqual(2, statistics.TotalReflectionCount);
        Assert.AreEqual(1, statistics.ConfirmedReflectionCount);
        Assert.AreEqual(1, statistics.DraftReflectionCount);
        Assert.AreEqual(
            ReflectionStatusNamesOf(ReflectionStatus.Confirmed),
            statistics.TodayReflectionStatus);
        Assert.AreEqual(1, statistics.TopicCount);
        Assert.IsNotNull(statistics.LastGenerationAtUtc);

        // Nothing in this test queues work: the capture path files topics and indexes embeddings, and neither is
        // configured here. An empty queue must report three zeros rather than a misleading total.
        Assert.AreEqual(0, statistics.QueuePending);
        Assert.AreEqual(0, statistics.QueueRunning);
        Assert.AreEqual(0, statistics.QueueFailed);
        Assert.IsTrue(statistics.DatabaseBytes >= 256, "The reader measures the database file at the path it is given.");
        Assert.AreEqual(0, statistics.PublishedCount);
        Assert.AreEqual(0, statistics.PendingPublicationCount);
    }

    /// <summary>
    /// A retained (soft-deleted) entry is not content any more, so it must not inflate "how much have I
    /// captured" — while the row itself stays, which is what the source map needs.
    /// </summary>
    [TestMethod]
    public async Task Purged_content_stops_counting_as_captured_material()
    {
        await using var context = await ReflectionTestContext.CreateAsync(
            content: ContentSettings.Default with { ContentRetentionDays = 0 });

        var entry = await context.CaptureTextAsync("今天写下的一句话。");
        await context.SeedDraftAsync(context.Today, ReflectionStatus.Confirmed);

        var before = await ReadAsync(context);
        Assert.AreEqual(1, before.TotalInputCount);

        await new Application.Operations.RunContentCleanupUseCase(
                context.Reflections,
                context.Inputs,
                new FileAudioStore(PathsFor(context)),
                context.Content,
                context.Clock)
            .ExecuteAsync(CancellationToken.None);

        var after = await ReadAsync(context);

        Assert.AreEqual(0, after.TotalInputCount);
        Assert.AreEqual(0, after.TodayInputCount);

        // The day's draft is still there: retaining content never touches what the user wrote *as an article*.
        Assert.AreEqual(1, after.TotalReflectionCount);
        Assert.AreEqual(1, after.ConfirmedReflectionCount);
        Assert.IsNotNull(await context.Inputs.FindByIdAsync(entry.Id, CancellationToken.None));
    }

    private static string ReflectionStatusNamesOf(ReflectionStatus status) => status.ToString();

    /// <summary>
    /// The instance's directory contract, with the database file where the contract says it lives.
    /// <para>
    /// This harness keeps its throwaway database beside the root rather than in <c>data/</c>, so the file the
    /// statistics reader is told to measure is created here explicitly — the reader must measure the file at the
    /// path it is configured with, and a test that left it missing would be asserting the wrong thing.
    /// </para>
    /// </summary>
    private static InstancePaths PathsFor(ReflectionTestContext context)
    {
        var paths = new InstancePaths(new StorageOptions
        {
            RootPath = context.Database.RootPath,
            SecretsPath = Path.Combine(context.Database.RootPath, "secrets"),
            KeyRingPath = Path.Combine(context.Database.RootPath, "keys"),
        });

        paths.EnsureCreated();
        File.WriteAllText(paths.DatabasePath, new string('x', 256));

        return paths;
    }

    private static Task<InstanceStatistics> ReadAsync(ReflectionTestContext context) =>
        new SqliteStatisticsReader(
                context.Database.Accessor,
                context.Content,
                context.Generation,
                new TestSmtpSettings { Settings = Application.Abstractions.SmtpSettings.Default },
                context.SemanticState,
                PathsFor(context),
                context.Clock)
            .ReadAsync(CancellationToken.None);
}
