using DailyMusings.Domain.Common;
using DailyMusings.Domain.Jobs;

namespace DailyMusings.Domain.Publishing;

public enum PublicationStatus
{
    Queued = 0,
    InProgress = 1,

    /// <summary>Uploaded to the remote as a private draft. The product's default outcome (§11.1).</summary>
    DraftUploaded = 2,

    Published = 3,
    Failed = 4,

    /// <summary>
    /// The scheduled slot passed without running. Terminal on purpose: §14 forbids publishing silently
    /// long after the fact, so recovery must be a human decision.
    /// </summary>
    Expired = 5,

    /// <summary>A newer version replaced this one before it went out (§11.1 夜间新增素材使待发布版本失效).</summary>
    Superseded = 6,
}

public enum PublicationTrigger
{
    Automatic = 0,
    Manual = 1,
}

/// <summary>Why an automatic run must not proceed (decision A.2, §11.1).</summary>
public enum AutomaticPublishBlock
{
    None = 0,

    /// <summary>The publication was requested by a human; the automatic gate does not apply.</summary>
    NotAutomaticRequest = 1,

    /// <summary>Unattended publishing is per-target opt-in and this target never opted in.</summary>
    TargetNotOptedIn = 2,

    /// <summary>The execution window elapsed. Never silently publish late.</summary>
    WindowExpired = 3,

    NotQueued = 4,
}

public readonly record struct AutomaticPublishGate(bool Allowed, AutomaticPublishBlock Reason)
{
    public static AutomaticPublishGate Permit { get; } = new(true, AutomaticPublishBlock.None);
}

/// <summary>
/// The execution window for a scheduled publication. Its only job is to make "never publish silently
/// hours later" a computable fact rather than a good intention (decision A.2).
/// </summary>
public static class PublishWindow
{
    public static void Validate(TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new DomainException("publish.window.invalid", "The publish window must be positive.");
        }
    }

    public static DateTimeOffset Deadline(DateTimeOffset scheduledAtUtc, TimeSpan window)
    {
        Validate(window);
        return scheduledAtUtc + window;
    }

    /// <summary>True once the slot is more than <paramref name="window"/> in the past.</summary>
    public static bool IsExpired(DateTimeOffset scheduledAtUtc, TimeSpan window, DateTimeOffset nowUtc) =>
        nowUtc > Deadline(scheduledAtUtc, window);
}

/// <summary>
/// One version of one reflection heading to one target (docs/开发指导.md §6.7). A reflection may go to
/// several targets, each tracked independently.
/// </summary>
public sealed class Publication
{
    private Publication(
        PublicationId id,
        ReflectionId reflectionId,
        ReflectionVersionId reflectionVersionId,
        PublishTargetId publishTargetId,
        PublicationTrigger trigger,
        PublicationVisibility requestedVisibility,
        DateTimeOffset scheduledAtUtc)
    {
        Id = id;
        ReflectionId = reflectionId;
        ReflectionVersionId = reflectionVersionId;
        PublishTargetId = publishTargetId;
        Trigger = trigger;
        RequestedVisibility = requestedVisibility;
        ScheduledAtUtc = scheduledAtUtc;
        Status = PublicationStatus.Queued;
    }

    public PublicationId Id { get; }

    public ReflectionId ReflectionId { get; }

    /// <summary>The exact version being published. A newer version never rewrites this pointer.</summary>
    public ReflectionVersionId ReflectionVersionId { get; }

    public PublishTargetId PublishTargetId { get; }

    /// <summary>
    /// How this publication was asked for. Settable rather than get-only because a record the scheduler created can
    /// be re-requested by a person (see <see cref="RequeueForReExport"/>), and at that moment the opt-in gate stops
    /// applying: it exists to constrain <em>unattended</em> runs, not to overrule someone who is standing there.
    /// </summary>
    public PublicationTrigger Trigger { get; private set; }

    /// <summary>
    /// What was asked for when this record was created. For an automatic request it is recorded rather than
    /// implied, so "the slot ran and uploaded a draft because the target never opted in" and "the slot ran and
    /// published publicly" stay distinguishable after the fact.
    /// </summary>
    public PublicationVisibility RequestedVisibility { get; private set; }

    public PublicationStatus Status { get; private set; }

    /// <summary>Remote article id, kept so local edits never blindly clobber the remote (§11.1).</summary>
    public string? RemoteId { get; private set; }

    /// <summary>
    /// Content hash of what was actually sent to the remote.
    /// <para>
    /// This is what makes a later difference check answerable. Without it, "the remote changed" and "our draft
    /// changed" cannot be told apart from "nothing happened", and §11.1's pull / overwrite / keep-both choice
    /// would be presented to the user with no evidence behind it.
    /// </para>
    /// </summary>
    public string? PublishedContentHash { get; private set; }

    /// <summary>Content hash the last remote check observed, or <c>null</c> when the remote has never been read.</summary>
    public string? RemoteContentHash { get; private set; }

    public DateTimeOffset? RemoteCheckedAtUtc { get; private set; }

    /// <summary>
    /// When the user last chose to keep both sides as they are (§11.1). Recorded so that a difference they have
    /// already accepted is not rediscovered on every check and presented as if it were news.
    /// </summary>
    public DateTimeOffset? RemoteDivergenceAcknowledgedAtUtc { get; private set; }

    public int AttemptCount { get; private set; }

    /// <summary>
    /// How many times this version has been sent to this target. Part of the job's idempotency key, so that a
    /// re-export is a new job instead of colliding with the one that already succeeded (§11.2, §14).
    /// </summary>
    public int ExportRound { get; private set; }

    public DateTimeOffset ScheduledAtUtc { get; private set; }

    /// <summary>Audit: who caused this attempt (administrator or device name).</summary>
    public string? TriggeredBy { get; private set; }
    public DateTimeOffset? TriggeredAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorSummary { get; private set; }

    public bool IsTerminal => Status is PublicationStatus.Published or PublicationStatus.Superseded;

    /// <param name="requestedBy">
    /// Who asked for this publication, recorded at request time rather than when the attempt runs. §11.1 requires
    /// the trail, and an automatic slot has no human to name — so it stays <c>null</c> and the attempt attributes
    /// itself to the scheduler.
    /// </param>
    public static Publication Create(
        PublicationId id,
        ReflectionId reflectionId,
        ReflectionVersionId reflectionVersionId,
        PublishTargetId publishTargetId,
        PublicationTrigger trigger,
        DateTimeOffset scheduledAtUtc,
        PublicationVisibility requestedVisibility = PublicationVisibility.Draft,
        string? requestedBy = null)
    {
        var publication = new Publication(
            id,
            reflectionId,
            reflectionVersionId,
            publishTargetId,
            trigger,
            requestedVisibility,
            scheduledAtUtc);

        if (!string.IsNullOrWhiteSpace(requestedBy))
        {
            publication.TriggeredBy = requestedBy.Trim();
        }

        return publication;
    }

    public static Publication Rehydrate(
        PublicationId id,
        ReflectionId reflectionId,
        ReflectionVersionId reflectionVersionId,
        PublishTargetId publishTargetId,
        PublicationTrigger trigger,
        PublicationStatus status,
        string? remoteId,
        int attemptCount,
        DateTimeOffset scheduledAtUtc,
        string? triggeredBy,
        DateTimeOffset? triggeredAtUtc,
        DateTimeOffset? completedAtUtc,
        string? errorCode,
        string? errorSummary,
        PublicationVisibility requestedVisibility = PublicationVisibility.Draft,
        string? publishedContentHash = null,
        string? remoteContentHash = null,
        DateTimeOffset? remoteCheckedAtUtc = null,
        DateTimeOffset? remoteDivergenceAcknowledgedAtUtc = null,
        int exportRound = 0) =>
        new(id, reflectionId, reflectionVersionId, publishTargetId, trigger, requestedVisibility, scheduledAtUtc)
        {
            Status = status,
            RemoteId = remoteId,
            AttemptCount = attemptCount,
            TriggeredBy = triggeredBy,
            TriggeredAtUtc = triggeredAtUtc,
            CompletedAtUtc = completedAtUtc,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
            PublishedContentHash = publishedContentHash,
            RemoteContentHash = remoteContentHash,
            RemoteCheckedAtUtc = remoteCheckedAtUtc,
            RemoteDivergenceAcknowledgedAtUtc = remoteDivergenceAcknowledgedAtUtc,
            ExportRound = exportRound,
        };

    /// <summary>
    /// Decides whether an unattended run may go ahead. Automatic publication requires three things at
    /// once: the target opted in, the execution window has not elapsed, and the publication is still
    /// queued. Any of them failing means a human must act — which is the entire point of the rule.
    /// </summary>
    public AutomaticPublishGate CanRunAutomatically(PublishTarget target, TimeSpan window, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (Trigger != PublicationTrigger.Automatic)
        {
            return new AutomaticPublishGate(false, AutomaticPublishBlock.NotAutomaticRequest);
        }

        if (Status != PublicationStatus.Queued)
        {
            return new AutomaticPublishGate(false, AutomaticPublishBlock.NotQueued);
        }

        if (!target.AutomaticPublishEnabled)
        {
            return new AutomaticPublishGate(false, AutomaticPublishBlock.TargetNotOptedIn);
        }

        if (PublishWindow.IsExpired(ScheduledAtUtc, window, nowUtc))
        {
            return new AutomaticPublishGate(false, AutomaticPublishBlock.WindowExpired);
        }

        return AutomaticPublishGate.Permit;
    }

    /// <summary>Starts an attempt. Manual runs must name the human who triggered them.</summary>
    public void Begin(string actor, DateTimeOffset at)
    {
        // Validate before mutating: a rejected call must leave the publication exactly as it was found.
        if (Trigger == PublicationTrigger.Manual && string.IsNullOrWhiteSpace(actor))
        {
            throw new DomainException(
                "publication.manual.actor_required",
                "A manual publication must record who triggered it.");
        }

        Transition(PublicationStatus.InProgress, at);
        AttemptCount++;
        TriggeredBy = string.IsNullOrWhiteSpace(actor) ? "system:scheduler" : actor.Trim();
        TriggeredAtUtc = at;
    }

    /// <summary>Uploaded as a remote draft — the default, human-confirmable outcome.</summary>
    public void CompleteAsDraft(string remoteId, string publishedContentHash, DateTimeOffset at)
    {
        RemoteId = RequireRemoteId(remoteId);
        RecordPublishedContent(publishedContentHash, at);
        Transition(PublicationStatus.DraftUploaded, at);
        CompletedAtUtc = at;
        ErrorCode = null;
        ErrorSummary = null;
    }

    public void CompleteAsPublished(string remoteId, string publishedContentHash, DateTimeOffset at)
    {
        RemoteId = RequireRemoteId(remoteId);
        RecordPublishedContent(publishedContentHash, at);
        Transition(PublicationStatus.Published, at);
        CompletedAtUtc = at;
        ErrorCode = null;
        ErrorSummary = null;
    }

    /// <summary>Moves an uploaded draft to publicly visible.</summary>
    public void PromoteDraftToPublished(DateTimeOffset at)
    {
        Transition(PublicationStatus.Published, at);
        CompletedAtUtc = at;
    }

    /// <summary>
    /// Records what the remote holds, as observed by a remote check. Also refreshes the baseline when the remote
    /// turns out to match what we sent, so a later check compares against the freshest known state.
    /// </summary>
    public void RecordRemoteObservation(string remoteContentHash, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteContentHash);

        RemoteContentHash = remoteContentHash;
        RemoteCheckedAtUtc = at;
    }

    /// <summary>
    /// How the local draft and the remote have diverged since the last publish (§11.1).
    /// </summary>
    /// <param name="currentLocalContentHash">
    /// Hash of the draft as it stands now. The caller passes it because only it knows which version is being
    /// compared — and the comparison must be against what was published, not against the working slot's pointer.
    /// </param>
    public RemoteComparison CompareWithRemote(string? currentLocalContentHash)
    {
        if (PublishedContentHash is null || RemoteContentHash is null)
        {
            return RemoteComparison.Unknown;
        }

        var localChanged = currentLocalContentHash is not null &&
                           !string.Equals(PublishedContentHash, currentLocalContentHash, StringComparison.Ordinal);

        var remoteChanged = !string.Equals(PublishedContentHash, RemoteContentHash, StringComparison.Ordinal);

        // Acknowledging the difference clears it, so the user stops being asked about a decision they made.
        if (RemoteDivergenceAcknowledgedAtUtc is not null && RemoteCheckedAtUtc <= RemoteDivergenceAcknowledgedAtUtc)
        {
            localChanged = false;
            remoteChanged = false;
        }

        return new RemoteComparison(RemoteChecked: true, localChanged, remoteChanged);
    }

    /// <summary>Records the user's decision to keep both sides as they are (§11.1).</summary>
    public void AcknowledgeRemoteDivergence(DateTimeOffset at) => RemoteDivergenceAcknowledgedAtUtc = at;

    public void Fail(string errorCode, string? errorSummary, DateTimeOffset at, RetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        Transition(PublicationStatus.Failed, at);
        ErrorCode = errorCode;
        ErrorSummary = errorSummary;
        CompletedAtUtc = at;
    }

    /// <summary>Marks the slot missed. Callers must not then publish automatically (§14).</summary>
    public void Expire(DateTimeOffset at)
    {
        Transition(PublicationStatus.Expired, at);
        CompletedAtUtc = at;
    }

    /// <summary>Marks this attempt obsolete because the reflection moved on (§11.1).</summary>
    public void Supersede(DateTimeOffset at)
    {
        Transition(PublicationStatus.Superseded, at);
        CompletedAtUtc = at;
    }

    /// <summary>
    /// Puts a failed or expired publication back in the queue. Always a human action, so the actor is
    /// mandatory and the attempt counter restarts.
    /// </summary>
    public void RequeueForManualRetry(string actor, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        Transition(PublicationStatus.Queued, at);
        AttemptCount = 0;
        ScheduledAtUtc = at;
        CompletedAtUtc = null;
        ErrorCode = null;
        ErrorSummary = null;
        TriggeredBy = actor.Trim();
        TriggeredAtUtc = at;
    }

    /// <summary>
    /// Queues an export of a version that is already on the remote as a draft (§11.2: 再次导出默认创建带版本号的新文件).
    /// <para>
    /// Only reachable from <see cref="PublicationStatus.DraftUploaded"/>, and <see cref="RemoteId"/> is kept on
    /// purpose: a Markdown re-export needs to know which file it wrote in order to tell "ours, untouched" from
    /// "somebody edited this".
    /// </para>
    /// </summary>
    public void RequeueForReExport(PublicationVisibility visibility, string actor, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        // A person asked for this, even when the record started life as an unattended run. Without this the planner
        // kept treating it as unattended, so a manual "publish publicly" against an already-uploaded draft was
        // silently downgraded to a draft upload whenever the target had not opted in: the user asked for public and
        // the API answered 200 "queued".
        Trigger = PublicationTrigger.Manual;

        Transition(PublicationStatus.Queued, at);
        AttemptCount = 0;
        ExportRound++;
        ScheduledAtUtc = at;
        CompletedAtUtc = null;
        ErrorCode = null;
        ErrorSummary = null;
        TriggeredBy = actor.Trim();
        TriggeredAtUtc = at;
        RequestedVisibility = visibility;
    }

    /// <summary>
    /// Re-enters a publication that a failed attempt left marked failed, because the job still has attempts left.
    /// <para>
    /// The alternative — leaving it InProgress between attempts — would tell the user nothing while the retries
    /// ran, and the alternative of a terminal Failed would be a lie while the system was still trying. So the
    /// status follows reality at every moment, and a manual retry is still the only way back once the job has
    /// genuinely given up (§14).
    /// </para>
    /// </summary>
    public void ResumeAfterFailedAttempt(string actor, DateTimeOffset at)
    {
        Transition(PublicationStatus.InProgress, at);
        AttemptCount++;
        TriggeredBy = string.IsNullOrWhiteSpace(actor) ? "system:retry" : actor.Trim();
        TriggeredAtUtc = at;
        ErrorCode = null;
        ErrorSummary = null;
        CompletedAtUtc = null;
    }

    private static string RequireRemoteId(string remoteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        return remoteId;
    }

    /// <summary>
    /// Remembers the content we put on the remote. Called from both completion paths so no path can publish
    /// without leaving behind the evidence a later difference check depends on.
    /// </summary>
    private void RecordPublishedContent(string contentHash, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);

        PublishedContentHash = contentHash;

        // The remote now holds exactly this, so it is also the freshest observation of the remote.
        RemoteContentHash = contentHash;
        RemoteCheckedAtUtc = at;
    }

    private void Transition(PublicationStatus to, DateTimeOffset at)
    {
        PublicationStatusTransitions.EnsureAllowed(Status, to);
        Status = to;
    }
}

/// <summary>The publication lifecycle (docs/开发指导.md §11.1, decision A.2).</summary>
public static class PublicationStatusTransitions
{
    private static readonly Dictionary<PublicationStatus, PublicationStatus[]> Allowed = new()
    {
        [PublicationStatus.Queued] =
        [
            PublicationStatus.InProgress,
            PublicationStatus.Expired,
            PublicationStatus.Superseded,
        ],
        [PublicationStatus.InProgress] =
        [
            PublicationStatus.DraftUploaded,
            PublicationStatus.Published,
            PublicationStatus.Failed,
        ],
        [PublicationStatus.DraftUploaded] =
        [
            PublicationStatus.Published,
            PublicationStatus.Superseded,

            // A re-export of a version that is already on the remote as a draft. Explicitly a human action: the
            // automatic path never revisits a completed publication (11.2).
            PublicationStatus.Queued,
        ],

        // Failed and Expired are both recoverable, but only by an explicit human retry — apart from Failed being
        // re-entered by the job's own bounded retry, which is not a recovery but the same attempt continuing.
        [PublicationStatus.Failed] = [PublicationStatus.Queued, PublicationStatus.InProgress],
        [PublicationStatus.Expired] = [PublicationStatus.Queued],
        [PublicationStatus.Published] = [],
        [PublicationStatus.Superseded] = [],
    };

    public static bool IsAllowed(PublicationStatus from, PublicationStatus to) =>
        from == to || (Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0);

    public static IReadOnlyList<PublicationStatus> From(PublicationStatus status) =>
        Allowed.TryGetValue(status, out var targets) ? targets : [];

    public static void EnsureAllowed(PublicationStatus from, PublicationStatus to)
    {
        if (!IsAllowed(from, to))
        {
            throw new DomainException(
                "publication.status.illegal_transition",
                $"Transition {from} -> {to} is not part of the publication lifecycle.");
        }
    }
}
