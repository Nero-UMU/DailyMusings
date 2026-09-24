using DailyMusings.Domain.Inputs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Inputs;

/// <summary>
/// How long captured content is kept after a day is confirmed, and which entries a sweep may strip
/// (docs/开发指导.md §15.1 and the content window added with this feature).
/// </summary>
[TestClass]
public class ContentRetentionTests
{
    private static readonly DateTimeOffset ConfirmedAt = TestFactory.Utc(2026, 3, 1, 12, 0);

    [TestMethod]
    public void Keeping_forever_is_the_default_and_nothing_is_ever_due()
    {
        var policy = ContentRetentionPolicy.Default;

        Assert.IsTrue(policy.KeepsForever);
        Assert.AreEqual(ContentRetentionPolicy.KeepForever, policy.Days);
        Assert.IsFalse(policy.IsDue(ConfirmedAt, ConfirmedAt.AddYears(10)));
    }

    [TestMethod]
    public void Zero_days_means_due_as_soon_as_the_day_is_confirmed()
    {
        var policy = new ContentRetentionPolicy(0);

        Assert.IsTrue(policy.DeletesImmediately);
        Assert.IsTrue(policy.IsDue(ConfirmedAt, ConfirmedAt));
    }

    [TestMethod]
    public void A_window_becomes_due_exactly_when_it_elapses()
    {
        var policy = new ContentRetentionPolicy(7);

        Assert.IsFalse(policy.IsDue(ConfirmedAt, ConfirmedAt.AddDays(7).AddSeconds(-1)));
        Assert.IsTrue(policy.IsDue(ConfirmedAt, ConfirmedAt.AddDays(7)));
    }

    [TestMethod]
    public void A_window_shorter_than_keep_forever_is_refused()
    {
        var failure = TestFactory.ThrowsDomain("retention.days.out_of_range", () => new ContentRetentionPolicy(-2).Validate());

        Assert.AreEqual("retention.days.out_of_range", failure.Code);
    }

    [TestMethod]
    public void Nothing_is_cleanable_before_the_day_is_confirmed()
    {
        var entry = TestFactory.TextEntry();
        var policy = new ContentRetentionPolicy(0);

        Assert.IsFalse(
            ContentCleanupPolicy.IsCleanable(entry, confirmedAtUtc: null, policy, TestFactory.Noon.AddDays(30)),
            "Until a human signs the day off, nothing is disposable.");
    }

    /// <summary>
    /// A recording whose transcription never succeeded is the only copy of what was said. Deleting it would throw
    /// away material a retry still needs — and because a sweep soft-deletes the row, it would also make the blob
    /// unreachable. The same reasoning the audio policy already applies (§15.1).
    /// </summary>
    [TestMethod]
    public void A_recording_that_was_never_transcribed_is_left_alone()
    {
        var entry = TestFactory.VoiceEntry();
        var policy = new ContentRetentionPolicy(0);

        Assert.IsFalse(ContentCleanupPolicy.IsCleanable(entry, ConfirmedAt, policy, ConfirmedAt.AddDays(1)));

        entry.FailTranscription("transcription.timeout");
        Assert.IsFalse(ContentCleanupPolicy.IsCleanable(entry, ConfirmedAt, policy, ConfirmedAt.AddDays(1)));

        entry.RetryTranscription();
        entry.BeginTranscription();
        entry.CompleteTranscription("今天很累。");

        Assert.IsTrue(ContentCleanupPolicy.IsCleanable(entry, ConfirmedAt, policy, ConfirmedAt.AddDays(1)));
    }

    [TestMethod]
    public void A_text_entry_is_cleanable_once_its_day_is_confirmed_and_due()
    {
        var entry = TestFactory.TextEntry();
        var policy = new ContentRetentionPolicy(7);

        Assert.IsFalse(ContentCleanupPolicy.IsCleanable(entry, ConfirmedAt, policy, ConfirmedAt.AddDays(6)));
        Assert.IsTrue(ContentCleanupPolicy.IsCleanable(entry, ConfirmedAt, policy, ConfirmedAt.AddDays(7)));
    }

    [TestMethod]
    public void An_entry_with_nothing_left_is_not_a_candidate_again()
    {
        var entry = TestFactory.TextEntry();
        var policy = new ContentRetentionPolicy(0);

        Assert.IsTrue(ContentCleanupPolicy.HoldsContent(entry));

        entry.PurgeContent(ConfirmedAt);

        Assert.IsFalse(ContentCleanupPolicy.HoldsContent(entry));
        Assert.IsFalse(ContentCleanupPolicy.IsCleanable(entry, ConfirmedAt, policy, ConfirmedAt.AddDays(1)));
    }

    /// <summary>
    /// §6.5: the entry keeps its identity, or a historical article's source map would dangle. What goes is the
    /// words and the recording.
    /// </summary>
    [TestMethod]
    public void Purging_clears_the_text_and_the_recording_but_keeps_the_row()
    {
        var entry = TestFactory.TranscribedVoice();
        entry.ReviseTranscript("我自己改过的版本。");

        var path = entry.PurgeContent(ConfirmedAt);

        Assert.IsNotNull(path, "The caller has to be told which blob to delete.");
        Assert.IsNull(entry.OriginalTranscript);
        Assert.IsNull(entry.RevisedTranscript);
        Assert.IsFalse(entry.HasAudio);
        Assert.AreEqual(ConfirmedAt, entry.DeletedAtUtc);
        Assert.AreEqual(ConfirmedAt, entry.AudioDeletedAtUtc);
        Assert.IsTrue(entry.IsDeleted);

        // The content day and the capture instant survive: they are what a source map refers to.
        Assert.AreEqual(TestFactory.Day(1), entry.ContentDate);
        Assert.AreEqual(TestFactory.Noon, entry.CreatedAtUtc);
    }

    [TestMethod]
    public void Purging_twice_changes_nothing()
    {
        var entry = TestFactory.TextEntry();

        entry.PurgeContent(ConfirmedAt);
        var second = entry.PurgeContent(ConfirmedAt.AddDays(1));

        Assert.IsNull(second);
        Assert.AreEqual(ConfirmedAt, entry.DeletedAtUtc, "A second sweep must not move the tombstone's stamp.");
    }
}
