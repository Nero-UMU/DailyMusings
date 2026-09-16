using DailyMusings.Domain.Inputs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests.Inputs;

/// <summary>
/// docs/开发指导.md §6.1, §7 and §17.1: transcripts and revisions are kept apart, and deleting the audio
/// is a different operation from deleting the record.
/// </summary>
[TestClass]
public class InputEntryTests
{
    [TestMethod]
    public void A_revision_never_overwrites_the_original_transcript()
    {
        var entry = TestFactory.TranscribedVoice(transcript: "原来转写错了。");

        entry.ReviseTranscript("用户修订后的文字。");

        Assert.AreEqual("原来转写错了。", entry.OriginalTranscript);
        Assert.AreEqual("用户修订后的文字。", entry.RevisedTranscript);
        Assert.AreEqual("用户修订后的文字。", entry.TranscriptForGeneration);
    }

    [TestMethod]
    public void Generation_falls_back_to_the_original_transcript_without_a_revision()
    {
        var entry = TestFactory.TranscribedVoice(transcript: "只有原始转写。");

        Assert.IsNull(entry.RevisedTranscript);
        Assert.AreEqual("只有原始转写。", entry.TranscriptForGeneration);
    }

    [TestMethod]
    public void Clearing_a_revision_falls_back_to_the_original_again()
    {
        var entry = TestFactory.TranscribedVoice();
        entry.ReviseTranscript("修订。");

        entry.ReviseTranscript("   ");

        Assert.IsNull(entry.RevisedTranscript);
        Assert.AreEqual(entry.OriginalTranscript, entry.TranscriptForGeneration);
    }

    [TestMethod]
    public void A_transcript_can_only_be_revised_after_transcription_succeeded()
    {
        var pending = TestFactory.VoiceEntry();

        TestFactory.ThrowsDomain("input.transcript.not_ready", () => pending.ReviseTranscript("太早了。"));
    }

    [TestMethod]
    public void Text_entries_carry_their_text_as_the_transcript()
    {
        var entry = TestFactory.TextEntry(text: "  随手记一句。  ");

        Assert.AreEqual(InputSourceType.Text, entry.SourceType);
        Assert.AreEqual(TranscriptionStatus.NotApplicable, entry.TranscriptionStatus);
        Assert.AreEqual("随手记一句。", entry.TranscriptForGeneration);
    }

    /// <summary>§17.1: deleting audio and deleting the record are different operations.</summary>
    [TestMethod]
    public void Deleting_audio_keeps_the_entry_and_its_transcript()
    {
        var entry = TestFactory.TranscribedVoice();
        var id = entry.Id;

        var pathToPurge = entry.DeleteAudio(TestFactory.Noon);

        Assert.AreEqual("media/2026/03/a.m4a", pathToPurge);
        Assert.IsFalse(entry.HasAudio);
        Assert.IsNull(entry.AudioPath);
        Assert.AreEqual(TestFactory.Noon, entry.AudioDeletedAtUtc);

        // The record itself, its transcripts and its identity all survive.
        Assert.IsFalse(entry.IsDeleted);
        Assert.AreEqual(id, entry.Id);
        Assert.IsNotNull(entry.OriginalTranscript);
    }

    [TestMethod]
    public void Deleting_the_record_detaches_its_audio_and_asks_the_caller_to_purge_it()
    {
        var entry = TestFactory.TranscribedVoice();

        var pathToPurge = entry.Delete(TestFactory.Noon);

        Assert.AreEqual("media/2026/03/a.m4a", pathToPurge);
        Assert.IsTrue(entry.IsDeleted);
        Assert.IsFalse(entry.HasAudio);
    }

    [TestMethod]
    public void Deleting_audio_twice_is_harmless_so_a_retried_request_cannot_fail()
    {
        var entry = TestFactory.TranscribedVoice();

        Assert.IsNotNull(entry.DeleteAudio(TestFactory.Noon));
        Assert.IsNull(entry.DeleteAudio(TestFactory.Noon));
    }

    [TestMethod]
    public void A_deleted_entry_refuses_further_mutation()
    {
        var entry = TestFactory.TextEntry();
        entry.Delete(TestFactory.Noon);

        TestFactory.ThrowsDomain("input.deleted", () => entry.SetAllowFutureRecall(false));
        TestFactory.ThrowsDomain("input.deleted", () => entry.ReviseTranscript("太晚了。"));
    }

    [TestMethod]
    public void An_input_has_at_most_one_primary_topic_and_it_is_never_also_secondary()
    {
        var entry = TestFactory.TextEntry();
        var primary = Domain.Common.TopicId.New();
        var secondary = Domain.Common.TopicId.New();

        entry.AssignTopics(primary, [secondary, secondary]);

        Assert.AreEqual(primary, entry.PrimaryTopicId);
        Assert.AreEqual(1, entry.SecondaryTopicIds.Count);

        TestFactory.ThrowsDomain(
            "input.topics.primary_also_secondary",
            () => entry.AssignTopics(primary, [primary]));
    }

    [TestMethod]
    public void Transcription_failure_never_discards_the_entry_or_its_audio()
    {
        var entry = TestFactory.VoiceEntry();
        entry.BeginTranscription();

        entry.FailTranscription();

        Assert.AreEqual(TranscriptionStatus.Failed, entry.TranscriptionStatus);
        Assert.IsTrue(entry.HasAudio);
        Assert.IsFalse(entry.IsDeleted);

        entry.RetryTranscription();
        Assert.AreEqual(TranscriptionStatus.Pending, entry.TranscriptionStatus);
    }

    /// <summary>
    /// A stuck attempt must stay recoverable. Found by running the real pipeline: an internal error after the
    /// attempt started left the entry in progress forever, and refusing to retry it made the capture unusable.
    /// </summary>
    [TestMethod]
    public void A_stuck_in_progress_transcription_can_be_retried()
    {
        var entry = TestFactory.VoiceEntry();
        entry.BeginTranscription();

        entry.RetryTranscription();

        Assert.AreEqual(TranscriptionStatus.Pending, entry.TranscriptionStatus);
        Assert.IsNull(entry.TranscriptionErrorCode);
    }

    [TestMethod]
    public void A_transcription_that_never_started_cannot_be_retried()
    {
        var entry = TestFactory.VoiceEntry();

        TestFactory.ThrowsDomain("input.transcription.bad_state", () => entry.RetryTranscription());
    }

    [TestMethod]
    public void Transcription_state_machine_rejects_out_of_order_calls()
    {
        var entry = TestFactory.VoiceEntry();

        TestFactory.ThrowsDomain("input.transcription.bad_state", () => entry.CompleteTranscription("跳过开始。"));
        TestFactory.ThrowsDomain("input.transcription.not_voice", () => TestFactory.TextEntry().BeginTranscription());

        entry.BeginTranscription();
        entry.CompleteTranscription("完成。");

        TestFactory.ThrowsDomain("input.transcription.bad_state", () => entry.FailTranscription());
    }

    [TestMethod]
    public void An_impossible_utc_offset_is_rejected()
    {
        TestFactory.ThrowsDomain(
            "input.offset.out_of_range",
            () => InputEntry.CreateText(
                Domain.Common.InputEntryId.New(),
                TestFactory.Noon,
                createdOffsetMinutes: 900,
                TestFactory.Day(1),
                "偏移不合法。"));
    }
}
