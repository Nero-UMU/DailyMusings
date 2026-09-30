using DailyMusings.Application.Configuration;
using DailyMusings.Application.Operations;
using DailyMusings.Domain.Reflections;
using DailyMusings.Infrastructure.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Infrastructure.Tests;

/// <summary>
/// The content retention sweep against a real database (docs/开发指导.md §15.1 and the content window added with
/// this feature). The property that matters most here is not "the text is gone" but "the row survived": a
/// historical article's source map points at these entries.
/// </summary>
[TestClass]
public class ContentRetentionTests
{
    [TestMethod]
    public async Task The_default_window_keeps_everything()
    {
        await using var context = await ReflectionTestContext.CreateAsync();

        await context.CaptureTextAsync("今天写下的一句话。");
        await context.SeedDraftAsync(context.Today, ReflectionStatus.Confirmed);

        var result = await Cleanup(context).ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(0, result.CleanedEntries, "Nothing is deleted because nobody asked for a window.");
        Assert.AreEqual(0, result.CandidateDays);
    }

    /// <summary>
    /// The whole promise of the window: the words and the recording go, and the row stays so that a source
    /// reference can still be followed.
    /// </summary>
    [TestMethod]
    public async Task A_due_day_loses_its_content_but_keeps_its_row_and_its_source_map()
    {
        await using var context = await ReflectionTestContext.CreateAsync(
            content: ContentSettings.Default with { ContentRetentionDays = 0 });

        var entry = await context.CaptureTextAsync("今天写下的一句话。");

        // The seeded draft cites the day's first entry, so this is exactly the case §6.5 protects.
        await context.SeedDraftAsync(context.Today, ReflectionStatus.Confirmed);

        Assert.AreEqual(1, await context.Database.CountAsync("source_reference"));

        var result = await Cleanup(context).ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, result.CleanedEntries);
        Assert.AreEqual(1, result.CandidateDays);

        var reloaded = await context.Inputs.FindByIdAsync(entry.Id, CancellationToken.None);

        Assert.IsNotNull(reloaded, "The row has to survive: a source reference points at it.");
        Assert.IsNull(reloaded.OriginalTranscript);
        Assert.IsNull(reloaded.RevisedTranscript);
        Assert.IsNotNull(reloaded.DeletedAtUtc);
        Assert.IsTrue(reloaded.IsDeleted);

        Assert.AreEqual(
            1,
            await context.Database.CountAsync("source_reference"),
            "The provenance of a historical article must not be collateral damage of a retention window.");
    }

    [TestMethod]
    public async Task A_day_that_is_not_confirmed_yet_is_never_touched()
    {
        await using var context = await ReflectionTestContext.CreateAsync(
            content: ContentSettings.Default with { ContentRetentionDays = 0 });

        var entry = await context.CaptureTextAsync("还没确认的一天。");
        await context.SeedDraftAsync(context.Today, ReflectionStatus.ReviewRequired);

        var result = await Cleanup(context).ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(0, result.CleanedEntries);

        var reloaded = await context.Inputs.FindByIdAsync(entry.Id, CancellationToken.None);
        Assert.IsNull(reloaded!.DeletedAtUtc);
        Assert.AreEqual("还没确认的一天。", reloaded.OriginalTranscript);
    }

    [TestMethod]
    public async Task A_recording_whose_transcription_failed_is_left_where_a_retry_can_find_it()
    {
        await using var context = await ReflectionTestContext.CreateAsync(
            content: ContentSettings.Default with { ContentRetentionDays = 0 });

        var paths = new InstancePaths(new StorageOptions
        {
            StatePath = context.Database.RootPath,
            MarkdownRootPath = Path.Combine(context.Database.RootPath, "content"),
            KeyRingPath = Path.Combine(context.Database.RootPath, "keys"),
        });

        var audio = new FileAudioStore(paths);
        await using var blob = new MemoryStream("not-really-m4a"u8.ToArray());

        var stored = await audio.SaveAsync(
            DailyMusings.Domain.Common.InputEntryId.New(),
            context.Today,
            blob,
            "audio/mp4",
            CancellationToken.None);

        var entry = DailyMusings.Domain.Inputs.InputEntry.CreateVoice(
            DailyMusings.Domain.Common.InputEntryId.New(),
            context.Clock.UtcNow,
            480,
            context.Today,
            stored.RelativePath,
            TimeSpan.FromSeconds(3));

        await context.Inputs.AddAsync(entry, CancellationToken.None);
        await context.SeedDraftAsync(context.Today, ReflectionStatus.Confirmed);

        var result = await Cleanup(context, audio).ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(0, result.CleanedEntries, "The recording is the only copy of what was said.");

        var reloaded = await context.Inputs.FindByIdAsync(entry.Id, CancellationToken.None);
        Assert.IsTrue(reloaded!.HasAudio);
        Assert.IsTrue(await audio.ExistsAsync(stored.RelativePath, CancellationToken.None));
    }

    /// <summary>
    /// A retention tombstone is invisible to the normal listing but reachable when it is asked for explicitly
    /// (§10.3: <c>GET /api/inputs?includeDeleted=true</c>). §15.1 keeps the row so a historical article's source
    /// map still resolves, which means "the user cannot see it" and "it is not there" have to be different things.
    /// </summary>
    [TestMethod]
    public async Task A_tombstone_is_hidden_from_the_normal_list_but_reachable_on_request()
    {
        await using var context = await ReflectionTestContext.CreateAsync(
            content: ContentSettings.Default with { ContentRetentionDays = 0 });

        await context.CaptureTextAsync("会过期的内容。");
        await context.SeedDraftAsync(context.Today, ReflectionStatus.Confirmed);

        await Cleanup(context).ExecuteAsync(CancellationToken.None);

        var visible = await context.Inputs.ListPageAsync(0, 50, includeDeleted: false, CancellationToken.None);
        var withTombstones = await context.Inputs.ListPageAsync(0, 50, includeDeleted: true, CancellationToken.None);

        Assert.AreEqual(0, visible.Count, "The data-management page and the phone must not see a tombstone.");
        Assert.AreEqual(1, withTombstones.Count, "Asking for tombstones explicitly has to return them.");

        Assert.AreEqual(0, await context.Inputs.CountAsync(includeDeleted: false, CancellationToken.None));
        Assert.AreEqual(
            1,
            await context.Inputs.CountAsync(includeDeleted: true, CancellationToken.None),
            "The total must agree with the page, or the admin list renders an empty last page.");
    }

    private static RunContentCleanupUseCase Cleanup(ReflectionTestContext context, FileAudioStore? audio = null)
    {
        var paths = new InstancePaths(new StorageOptions
        {
            StatePath = context.Database.RootPath,
            MarkdownRootPath = Path.Combine(context.Database.RootPath, "content"),
            KeyRingPath = Path.Combine(context.Database.RootPath, "keys"),
        });

        return new RunContentCleanupUseCase(
            context.Reflections,
            context.Inputs,
            audio ?? new FileAudioStore(paths),
            context.Content,
            context.Clock);
    }
}
