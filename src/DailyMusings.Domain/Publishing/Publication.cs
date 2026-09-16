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
        DateTimeOffset scheduledAtUtc)
    {
        Id = id;
        ReflectionId = reflectionId;
        ReflectionVersionId = reflectionVersionId;
        PublishTargetId = publishTargetId;
        Trigger = trigger;
        ScheduledAtUtc = scheduledAtUtc;
        Status = PublicationStatus.Queued;
    }

    public PublicationId Id { get; }

    public ReflectionId ReflectionId { get; }

    /// <summary>The exact version being published. A newer version never rewrites this pointer.</summary>
    public ReflectionVersionId ReflectionVersionId { get; }

    public PublishTargetId PublishTargetId { get; }

    public PublicationTrigger Trigger { get; }

    public PublicationStatus Status { get; private set; }

    /// <summary>Remote article id, kept so local edits never blindly clobber the remote (§11.1).</summary>
    public string? RemoteId { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset ScheduledAtUtc { get; private set; }

    /// <summary>Audit: who caused this attempt (administrator or device name).</summary>
    public string? TriggeredBy { get; private set; }

    public DateTimeOffset? TriggeredAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorSummary { get; private set; }

    public bool IsTerminal => Status is PublicationStatus.Published or PublicationStatus.Superseded;

    public static Publication Create(
        PublicationId id,
        ReflectionId reflectionId,
        ReflectionVersionId reflectionVersionId,
        PublishTargetId publishTargetId,
        PublicationTrigger trigger,
        DateTimeOffset scheduledAtUtc) =>
        new(id, reflectionId, reflectionVersionId, publishTargetId, trigger, scheduledAtUtc);

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
        string? errorSummary) =>
        new(id, reflectionId, reflectionVersionId, publishTargetId, trigger, scheduledAtUtc)
        {
            Status = status,
            RemoteId = remoteId,
            AttemptCount = attemptCount,
            TriggeredBy = triggeredBy,
            TriggeredAtUtc = triggeredAtUtc,
            CompletedAtUtc = completedAtUtc,
            ErrorCode = errorCode,
            ErrorSummary = errorSummary,
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
    public void CompleteAsDraft(string remoteId, DateTimeOffset at)
    {
        RemoteId = RequireRemoteId(remoteId);
        Transition(PublicationStatus.DraftUploaded, at);
        CompletedAtUtc = at;
        ErrorCode = null;
        ErrorSummary = null;
    }

    public void CompleteAsPublished(string remoteId, DateTimeOffset at)
    {
        RemoteId = RequireRemoteId(remoteId);
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

    private static string RequireRemoteId(string remoteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        return remoteId;
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
        ],

        // Failed and Expired are both recoverable, but only by an explicit human retry.
        [PublicationStatus.Failed] = [PublicationStatus.Queued],
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
