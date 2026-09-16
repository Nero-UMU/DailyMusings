using DailyMusings.Application.Reflections;
using DailyMusings.Domain.Retrieval;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// History retrieval across both of its paths (docs/开发指导.md §8.3, decision A.8).
/// <para>
/// The degraded path is not a fallback nobody exercises: §3.1 makes embeddings optional, so an instance with them
/// switched off has to retrieve usefully from topics and full text alone. Both paths are asserted here, together
/// with the one boundary that must hold in each — nothing from after the article's day.
/// </para>
/// </summary>
[TestClass]
public class RetrievalTests
{
    [TestMethod]
    public async Task Topics_carry_retrieval_when_embeddings_are_switched_off()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var (topic, _) = await context.CreateTopic.ExecuteAsync("录音", CancellationToken.None);

        var today = await context.CaptureTextAsync("今天试了一下录音。", idempotencyKey: "today");
        await context.AssignTopics.ExecuteAsync(today.Id, topic.Id, [], CancellationToken.None);

        var related = await context.CaptureTextAsync(
            "以前也录过一次。",
            contentDate: context.Today.AddDays(-3),
            idempotencyKey: "related");
        await context.AssignTopics.ExecuteAsync(related.Id, topic.Id, [], CancellationToken.None);

        var unrelated = await context.CaptureTextAsync(
            "周末去爬山看日出。",
            contentDate: context.Today.AddDays(-2),
            idempotencyKey: "unrelated");

        var outcome = await context.ThisRetrieval.ExecuteAsync(
            context.Today,
            await context.Inputs.ListByContentDateAsync(context.Today, CancellationToken.None),
            CancellationToken.None);

        Assert.AreEqual(RetrievalMode.Degraded, outcome.Mode);
        Assert.IsFalse(outcome.SemanticSearch.Available);

        var ids = outcome.Materials.Select(material => material.Entry.Id).ToArray();
        CollectionAssert.Contains(ids, related.Id);
        CollectionAssert.DoesNotContain(ids, unrelated.Id);

        var relatedMaterial = outcome.Materials.Single(material => material.Entry.Id == related.Id);
        Assert.AreEqual(1.0, relatedMaterial.Relevance, 0.0001);
        Assert.IsTrue(relatedMaterial.IsHistorical, "Material from an earlier day is flagged for §8.3's phrasing rule.");
        Assert.IsTrue(relatedMaterial.Reason.Contains("录音", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Full_text_carries_retrieval_when_no_topics_are_shared()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        await context.CaptureTextAsync("今天试了一下录音和上传。", idempotencyKey: "today");

        var related = await context.CaptureTextAsync(
            "上次录音上传失败了。",
            contentDate: context.Today.AddDays(-4),
            idempotencyKey: "related");

        await context.CaptureTextAsync(
            "周末去爬山看日出。",
            contentDate: context.Today.AddDays(-4),
            idempotencyKey: "unrelated");

        var outcome = await context.ThisRetrieval.ExecuteAsync(
            context.Today,
            await context.Inputs.ListByContentDateAsync(context.Today, CancellationToken.None),
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { related.Id },
            outcome.Materials.Select(material => material.Entry.Id).ToArray());
        Assert.IsTrue(outcome.Materials[0].Relevance < 0.5, "Text overlap ranks below an explicit topic match.");
    }

    /// <summary>
    /// §8.3's boundary, and the one thing retrieval must never get wrong: a reflection may not cite something from
    /// after its own day.
    /// </summary>
    [TestMethod]
    public async Task Nothing_from_after_the_article_day_is_ever_retrieved()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var (topic, _) = await context.CreateTopic.ExecuteAsync("录音", CancellationToken.None);

        var today = await context.CaptureTextAsync("今天试了一下录音。", idempotencyKey: "today");
        await context.AssignTopics.ExecuteAsync(today.Id, topic.Id, [], CancellationToken.None);

        var tomorrow = await context.CaptureTextAsync(
            "明天要做的事。",
            contentDate: context.Today.AddDays(1),
            idempotencyKey: "future");
        await context.AssignTopics.ExecuteAsync(tomorrow.Id, topic.Id, [], CancellationToken.None);

        var outcome = await context.ThisRetrieval.ExecuteAsync(
            context.Today,
            await context.Inputs.ListByContentDateAsync(context.Today, CancellationToken.None),
            CancellationToken.None);

        CollectionAssert.DoesNotContain(
            outcome.Materials.Select(material => material.Entry.Id).ToArray(),
            tomorrow.Id);
    }

    [TestMethod]
    public async Task Material_the_user_excluded_from_future_recall_is_never_retrieved()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        var (topic, _) = await context.CreateTopic.ExecuteAsync("录音", CancellationToken.None);

        var today = await context.CaptureTextAsync("今天试了一下录音。", idempotencyKey: "today");
        await context.AssignTopics.ExecuteAsync(today.Id, topic.Id, [], CancellationToken.None);

        var excluded = await context.CaptureTextAsync(
            "这件事不要再提。",
            contentDate: context.Today.AddDays(-1),
            idempotencyKey: "excluded");
        await context.AssignTopics.ExecuteAsync(excluded.Id, topic.Id, [], CancellationToken.None);

        excluded.SetAllowFutureRecall(false);
        await context.Inputs.UpdateAsync(excluded, CancellationToken.None);

        var outcome = await context.ThisRetrieval.ExecuteAsync(
            context.Today,
            await context.Inputs.ListByContentDateAsync(context.Today, CancellationToken.None),
            CancellationToken.None);

        CollectionAssert.DoesNotContain(
            outcome.Materials.Select(material => material.Entry.Id).ToArray(),
            excluded.Id);
    }

    [TestMethod]
    public async Task Semantic_retrieval_is_used_once_the_index_matches_the_configured_model()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        context.Embedding.Settings = context.Embedding.Settings with { Enabled = true };
        context.EmbeddingClient.VectorFor = text => text.Contains("录音", StringComparison.Ordinal)
            ? [1f, 0f, 0f]
            : [0f, 1f, 0f];

        var today = await context.CaptureTextAsync("今天试了一下录音。", idempotencyKey: "today");
        var similar = await context.CaptureTextAsync(
            "还是那件关于录音的事。",
            contentDate: context.Today.AddDays(-5),
            idempotencyKey: "similar");
        var different = await context.CaptureTextAsync(
            "周末去爬山看日出。",
            contentDate: context.Today.AddDays(-5),
            idempotencyKey: "different");

        await context.EmbedInput.ExecuteAsync(today.Id, CancellationToken.None);
        await context.EmbedInput.ExecuteAsync(similar.Id, CancellationToken.None);
        await context.EmbedInput.ExecuteAsync(different.Id, CancellationToken.None);

        // A8: the index is only usable once it has been completed for the configured fingerprint.
        var before = await context.SemanticState.ExecuteAsync(CancellationToken.None);
        Assert.IsFalse(before.Available);
        Assert.IsTrue(before.Rebuilding);

        await context.IndexState.MarkIndexedAsync(
            context.Embedding.Settings.ResolveConfigVersion().Fingerprint,
            CancellationToken.None);

        var after = await context.SemanticState.ExecuteAsync(CancellationToken.None);
        Assert.IsTrue(after.Available);
        Assert.IsFalse(after.Rebuilding);

        var outcome = await context.ThisRetrieval.ExecuteAsync(
            context.Today,
            await context.Inputs.ListByContentDateAsync(context.Today, CancellationToken.None),
            CancellationToken.None);

        Assert.AreEqual(RetrievalMode.Semantic, outcome.Mode);

        var ids = outcome.Materials.Select(material => material.Entry.Id).ToArray();
        CollectionAssert.Contains(ids, similar.Id);
        CollectionAssert.DoesNotContain(ids, different.Id, "An unrelated vector must not be returned.");

        var match = outcome.Materials.Single(material => material.Entry.Id == similar.Id);
        Assert.AreEqual(1.0, match.Relevance, 0.0001, "Identical directions score a perfect cosine.");
        Assert.AreEqual("语义相似", match.Reason);
    }

    [TestMethod]
    public async Task Changing_the_embedding_model_makes_the_index_unusable_until_it_is_rebuilt()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        context.Embedding.Settings = context.Embedding.Settings with { Enabled = true };

        await context.CaptureTextAsync("今天试着记录了一点东西。", idempotencyKey: "today");

        var firstFingerprint = context.Embedding.Settings.ResolveConfigVersion().Fingerprint;
        await context.IndexState.MarkIndexedAsync(firstFingerprint, CancellationToken.None);
        Assert.IsTrue((await context.SemanticState.ExecuteAsync(CancellationToken.None)).Available);

        // §8.3: vectors from two models are not comparable, so a change invalidates the whole index at once.
        context.Embedding.Settings = context.Embedding.Settings with { Model = "another-embedding-model" };

        var afterChange = await context.SemanticState.ExecuteAsync(CancellationToken.None);
        Assert.IsFalse(afterChange.Available);
        Assert.IsTrue(afterChange.Rebuilding);
        Assert.AreNotEqual(firstFingerprint, afterChange.ConfiguredVersion);
    }

    [TestMethod]
    public async Task A_rebuild_runs_in_batches_and_switches_the_index_only_when_it_is_complete()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        context.Embedding.Settings = context.Embedding.Settings with { Enabled = true, BatchSize = 1 };

        await context.CaptureTextAsync("第一条记录。", idempotencyKey: "a");
        await context.CaptureTextAsync("第二条记录。", idempotencyKey: "b");

        var fingerprint = context.Embedding.Settings.ResolveConfigVersion().Fingerprint;

        var first = await context.RebuildIndex.ExecuteAsync(fingerprint, CancellationToken.None);
        Assert.IsFalse(first.Complete, "A batch boundary is not a completed rebuild.");
        Assert.AreEqual(1, first.Indexed);
        Assert.AreEqual(1, first.Remaining);

        // The switch must not have happened yet: retrieval would otherwise read a half-built index.
        Assert.IsFalse((await context.SemanticState.ExecuteAsync(CancellationToken.None)).Available);

        var second = await context.RebuildIndex.ExecuteAsync(fingerprint, CancellationToken.None);
        Assert.IsTrue(second.Complete);
        Assert.AreEqual(2, await context.EmbeddingIndex.CountAsync(fingerprint, CancellationToken.None));
        Assert.IsTrue((await context.SemanticState.ExecuteAsync(CancellationToken.None)).Available);
    }

    [TestMethod]
    public async Task Superseded_vectors_are_dropped_once_the_new_index_is_complete()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        context.Embedding.Settings = context.Embedding.Settings with { Enabled = true };

        await context.CaptureTextAsync("今天试着记录了一点东西。", idempotencyKey: "today");

        var first = context.Embedding.Settings.ResolveConfigVersion().Fingerprint;
        await context.RebuildIndex.ExecuteAsync(first, CancellationToken.None);
        Assert.AreEqual(1, await context.EmbeddingIndex.CountAsync(first, CancellationToken.None));

        context.Embedding.Settings = context.Embedding.Settings with { Model = "another-embedding-model" };
        var second = context.Embedding.Settings.ResolveConfigVersion().Fingerprint;

        var result = await context.RebuildIndex.ExecuteAsync(second, CancellationToken.None);

        // A.8 keeps one generation of the index. The old rows have to survive the rebuild and disappear after it.
        Assert.IsTrue(result.Complete);
        Assert.AreEqual(1, await context.EmbeddingIndex.CountAsync(second, CancellationToken.None));
        Assert.AreEqual(0, await context.EmbeddingIndex.CountAsync(first, CancellationToken.None));
        Assert.AreEqual(second, await context.IndexState.GetIndexedVersionAsync(CancellationToken.None));
    }

    /// <summary>
    /// §7: 修改后的内容可参与未来主题检索. A revised transcript changes what the entry should match, so the index
    /// has to be rebuilt for it — a vector for the replaced wording would keep answering the old question.
    /// </summary>
    [TestMethod]
    public async Task An_edited_transcript_is_queued_for_re_embedding()
    {
        await using var context = await ReflectionTestContext.CreateAsync();
        context.Embedding.Settings = context.Embedding.Settings with { Enabled = true };

        var entry = Domain.Inputs.InputEntry.CreateVoice(
            Domain.Common.InputEntryId.New(),
            context.Clock.UtcNow,
            480,
            context.Today,
            "media/2026/03/a.m4a",
            TimeSpan.FromSeconds(9));

        entry.BeginTranscription();
        entry.CompleteTranscription("最初的转写内容。");
        await context.Inputs.AddAsync(entry, CancellationToken.None);

        Assert.IsTrue(await context.EnsureIndexed.ExecuteAsync(
            entry.Id, entry.TranscriptForGeneration, CancellationToken.None));

        var first = (await context.Jobs.ListRecentAsync(10, CancellationToken.None))
            .Single(job => job.JobType == Domain.Jobs.JobType.EmbeddingIndex);

        await context.EmbedInput.ExecuteAsync(entry.Id, CancellationToken.None);
        Assert.AreEqual(
            1,
            await context.EmbeddingIndex.CountAsync(
                context.Embedding.Settings.ResolveConfigVersion().Fingerprint,
                CancellationToken.None));

        var revised = new Application.Inputs.ReviseTranscriptUseCase(context.Inputs, context.EnsureIndexed);

        await revised.ExecuteAsync(entry.Id, "改成了完全不同的内容。", CancellationToken.None);
        await revised.ExecuteAsync(entry.Id, "改成了完全不同的内容。", CancellationToken.None);

        var jobs = await context.Jobs.ListRecentAsync(10, CancellationToken.None);
        var embeddingJobs = jobs.Where(job => job.JobType == Domain.Jobs.JobType.EmbeddingIndex).ToArray();

        Assert.AreEqual(2, embeddingJobs.Length, "The revision queues one re-embed, and repeating it queues nothing.");
        Assert.AreNotEqual(first.IdempotencyKey, embeddingJobs.Select(job => job.IdempotencyKey).First(key => key != first.IdempotencyKey));
    }
}
