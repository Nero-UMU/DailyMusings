using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Reflections.Sources;
using DailyMusings.Domain.Time;
using DailyMusings.Domain.Topics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DailyMusings.Domain.Tests;

/// <summary>
/// Shared builders so each test states only what it is actually about. Every instant used here is
/// explicit — the domain never reads an ambient clock, which is what makes §7 testable at all.
/// </summary>
internal static class TestFactory
{
    /// <summary>2026-03-01T12:00:00Z.</summary>
    public static readonly DateTimeOffset Noon = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    public const int ShanghaiOffsetMinutes = 480;

    public static ContentCalendar Shanghai() => new(ContentTimeZone.FromId(ContentTimeZone.DefaultId));

    public static ContentDate Day(int dayOfMarch) => ContentDate.From(new DateOnly(2026, 3, dayOfMarch));

    public static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    public static InputEntry TextEntry(ContentDate? contentDate = null, string text = "今天路过一条小巷。")
    {
        var day = contentDate ?? Day(1);
        return InputEntry.CreateText(InputEntryId.New(), Noon, ShanghaiOffsetMinutes, day, text);
    }

    /// <summary>A text entry captured at a specific instant, for ordering and recall-boundary tests.</summary>
    public static InputEntry TextEntryAt(DateTimeOffset createdAtUtc, ContentDate contentDate, string text)
    {
        return InputEntry.CreateText(InputEntryId.New(), createdAtUtc, ShanghaiOffsetMinutes, contentDate, text);
    }

    /// <summary>A text entry already filed under topics, the shape automatic recognition produces.</summary>
    public static InputEntry FiledText(
        ContentDate? contentDate,
        string text,
        TopicId? primary = null,
        params TopicId[] secondary)
    {
        var entry = TextEntry(contentDate, text);
        entry.AssignTopics(primary, secondary);
        return entry;
    }

    public static Topic NewTopic(string name = "录音") => Topic.Create(TopicId.New(), name, Noon);

    public static InputEntry VoiceEntry(ContentDate? contentDate = null, string audioPath = "media/2026/03/a.m4a")
    {
        var day = contentDate ?? Day(1);
        return InputEntry.CreateVoice(
            InputEntryId.New(),
            Noon,
            ShanghaiOffsetMinutes,
            day,
            audioPath,
            TimeSpan.FromSeconds(12));
    }

    public static InputEntry TranscribedVoice(ContentDate? contentDate = null, string transcript = "今天很累。")
    {
        var entry = VoiceEntry(contentDate);
        entry.BeginTranscription();
        entry.CompleteTranscription(transcript);
        return entry;
    }

    public static Reflection NewReflection(ContentDate? contentDate = null)
    {
        var day = contentDate ?? Day(1);
        return Reflection.Create(ReflectionId.New(), day, GenerationReason.Scheduled, Noon);
    }

    public static ReflectionVersion NewVersion(Reflection reflection, string body = "第一段。\n\n第二段。")
    {
        var version = ReflectionVersion.CreateGenerated(
            ReflectionVersionId.New(),
            reflection.Id,
            "标题",
            "摘要",
            body,
            WritingSettings.Default,
            new ModelInfo("test-model"),
            "prompt-v1",
            Noon);

        return version;
    }

    public static SourceReference NewSource(
        ReflectionVersion version,
        int blockIndex = 0,
        int charStart = 0,
        int charEnd = 3,
        string quotedText = "第一段",
        InputEntryId? inputId = null,
        bool isHistorical = false) =>
        SourceReference.Create(
            SourceReferenceId.New(),
            version.Id,
            blockIndex,
            charStart,
            charEnd,
            quotedText,
            inputId ?? InputEntryId.New(),
            0.9,
            "与当天记录一致",
            isHistorical);

    /// <summary>Asserts a specific domain rule fired, by its stable code rather than its message.</summary>
    public static DomainException ThrowsDomain(string expectedCode, Action action)
    {
        var exception = Assert.ThrowsException<DomainException>(action);
        Assert.AreEqual(expectedCode, exception.Code, $"Unexpected domain rule: {exception.Message}");
        return exception;
    }

    public static ProcessingJob NewJob(JobType type = JobType.Transcription) =>
        ProcessingJob.Create(JobId.New(), type, "target-1", IdempotencyKeys.Transcription(InputEntryId.New()), Noon);

    public static PublishTarget NewTarget(bool automatic = false, string? enabledBy = null)
    {
        var target = PublishTarget.Create(PublishTargetId.New(), "blog", PublishTargetType.WordPress, "site-a");

        if (automatic)
        {
            target.EnableAutomaticPublish(enabledBy ?? "admin", Noon);
        }

        return target;
    }

    public static Publication NewPublication(
        PublishTarget target,
        PublicationTrigger trigger = PublicationTrigger.Automatic,
        DateTimeOffset? scheduledAtUtc = null) =>
        Publication.Create(
            PublicationId.New(),
            ReflectionId.New(),
            ReflectionVersionId.New(),
            target.Id,
            trigger,
            scheduledAtUtc ?? Noon);
}
