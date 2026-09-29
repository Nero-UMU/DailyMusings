using DailyMusings.Domain.Inputs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Inputs;

/// <summary>
/// docs/开发指导.md §15.1 and decision A.1: when a recording may be physically deleted, and the two cases where it
/// may not be. Both of those cases are about not destroying the only copy of something, which is why they are
/// asserted before the arithmetic.
/// </summary>
[TestClass]
public class AudioRetentionTests
{
    [TestMethod]
    public void The_default_window_is_thirty_days_from_the_confirmation()
    {
        var policy = AudioRetentionPolicy.Default;
        var confirmed = TestFactory.Noon;

        Assert.AreEqual(30, policy.Days);
        Assert.IsFalse(policy.IsDue(confirmed, confirmed.AddDays(29)));
        Assert.IsTrue(policy.IsDue(confirmed, confirmed.AddDays(30)));
    }

    [TestMethod]
    public void Zero_deletes_as_soon_as_the_day_is_confirmed()
    {
        var policy = new AudioRetentionPolicy(0);
        var confirmed = TestFactory.Noon;

        Assert.IsTrue(policy.DeletesImmediately);
        Assert.IsTrue(policy.IsDue(confirmed, confirmed), "There is no window at all.");
    }

    [TestMethod]
    public void Forever_never_expires()
    {
        var policy = new AudioRetentionPolicy(AudioRetentionPolicy.KeepForever);

        Assert.IsTrue(policy.KeepsForever);
        Assert.IsFalse(policy.IsDue(TestFactory.Noon, TestFactory.Noon.AddYears(50)));
    }

    [TestMethod]
    public void An_impossible_window_is_refused()
    {
        TestFactory.ThrowsDomain("retention.days.out_of_range", () => new AudioRetentionPolicy(-2).Validate());
    }

    [TestMethod]
    public void A_recording_becomes_cleanable_only_after_a_human_confirms_the_day()
    {
        var entry = TestFactory.TranscribedVoice();
        var policy = new AudioRetentionPolicy(30);
        var now = TestFactory.Noon.AddDays(60);

        Assert.IsFalse(
            AudioCleanupPolicy.IsCleanable(entry, confirmedAtUtc: null, policy, now),
            "Until somebody signs the day off, the recordings are still the only account of what was said.");

        Assert.IsTrue(AudioCleanupPolicy.IsCleanable(entry, TestFactory.Noon, policy, now));
    }

    [TestMethod]
    public void A_recording_whose_transcription_failed_is_never_cleanable()
    {
        // The transcript does not exist, so the recording is the input the retry needs. Deleting it would turn a
        // failed transcription into a lost capture.
        var entry = TestFactory.VoiceEntry();
        entry.BeginTranscription();
        entry.FailTranscription("transcription.timeout");

        Assert.IsFalse(
            AudioCleanupPolicy.IsCleanable(entry, TestFactory.Noon, new AudioRetentionPolicy(0), TestFactory.Noon.AddYears(1)));
    }

    [TestMethod]
    public void An_entry_that_has_not_been_transcribed_yet_keeps_its_audio()
    {
        var pending = TestFactory.VoiceEntry();

        Assert.IsFalse(
            AudioCleanupPolicy.IsCleanable(pending, TestFactory.Noon, new AudioRetentionPolicy(0), TestFactory.Noon.AddYears(1)));
    }

    [TestMethod]
    public void A_deleted_entry_or_one_without_audio_is_not_a_candidate()
    {
        var policy = new AudioRetentionPolicy(0);
        var confirmed = TestFactory.Noon;

        // The retention sweep is what reaches the deleted state in production, and it takes the audio with it,
        // so both cases below land on the same guard. The assertions pin the outcome, not two separate branches.
        var purged = TestFactory.TranscribedVoice();
        purged.PurgeContent(TestFactory.Noon);
        Assert.IsFalse(AudioCleanupPolicy.IsCleanable(purged, confirmed, policy, confirmed));

        var silent = TestFactory.TranscribedVoice();
        silent.DeleteAudio(TestFactory.Noon);
        Assert.IsFalse(AudioCleanupPolicy.IsCleanable(silent, confirmed, policy, confirmed));
    }

    [TestMethod]
    public void The_policy_describes_itself_for_the_operator_and_the_manifest()
    {
        StringAssert.Contains(new AudioRetentionPolicy(30).Describe(), "30");
        StringAssert.Contains(new AudioRetentionPolicy(0).Describe(), "立即");
        StringAssert.Contains(new AudioRetentionPolicy(AudioRetentionPolicy.KeepForever).Describe(), "永久");
    }
}
