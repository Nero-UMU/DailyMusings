using DailyMusings.Domain.Common;
using DailyMusings.Domain.Time;

namespace DailyMusings.Domain.Notifications;

/// <summary>
/// The events §12 lets an operator enable one by one (docs/开发指导.md §12).
/// <para>
/// Three events, exactly as the guide lists them, and no "all mail" switch: the settings an operator sees should
/// be the decisions they actually make, and a single toggle on top of three flags would only add a fourth way to
/// be surprised by what the instance sent.
/// </para>
/// </summary>
public enum NotificationEvent
{
    /// <summary>A day's draft is ready and waiting for review.</summary>
    DraftReady = 0,

    /// <summary>A job reached its terminal failure state (§14).</summary>
    JobFailed = 1,

    /// <summary>An unattended publication succeeded or failed (§11.1).</summary>
    AutomaticPublication = 2,
}

/// <summary>
/// The idempotency keys behind the notifications the product sends.
/// <para>
/// Deriving them here, rather than at each call site, is what makes "the user was told once" a property of the
/// queue instead of a hope: a replayed notification collides with the unique index and becomes a no-op, exactly
/// like a replayed publish.
/// </para>
/// </summary>
public static class NotificationKeys
{
    /// <summary>
    /// One notification per <em>version</em>, not per day.
    /// <para>
    /// A day can produce several drafts: the nightly one, then another after new material invalidated the first.
    /// Keying on the day alone would mail about the first and stay silent about the second — and the second is
    /// exactly the one the user has to re-confirm. Keying on the version keeps each "please look at this" distinct
    /// while still making a repeated scan a no-op.
    /// </para>
    /// </summary>
    public static string ForDraftReady(ContentDate contentDate, ReflectionVersionId version) =>
        $"notification:draft-ready:{contentDate}:{version}";

    /// <summary>
    /// One reminder for the working version that is still unpublished when its configured publish time arrives.
    /// This is deliberately distinct from <see cref="ForDraftReady"/>: generation may announce that a draft is
    /// ready immediately, while the publish-time reminder answers a later and different question.
    /// </summary>
    public static string ForUnpublishedAtPublishTime(ContentDate contentDate, ReflectionVersionId version) =>
        $"notification:unpublished-at-publish-time:{contentDate}:{version}";

    /// <summary>One notification per failed job. §14 wants the user told which job failed, so the job names it.</summary>
    public static string ForFailedJob(JobId jobId) => $"notification:job-failed:{jobId}";

    /// <summary>An automatic publication reports its own outcome once.</summary>
    public static string ForPublication(PublicationId publicationId) =>
        $"notification:publication:{publicationId}";
}
