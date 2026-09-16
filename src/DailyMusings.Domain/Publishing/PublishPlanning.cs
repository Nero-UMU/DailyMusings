using DailyMusings.Domain.Common;

namespace DailyMusings.Domain.Publishing;

/// <summary>What a publication was asked to achieve (docs/开发指导.md §11.1).</summary>
public enum PublicationVisibility
{
    /// <summary>An unpublished remote draft. The product's default outcome for anything unattended.</summary>
    Draft = 0,

    /// <summary>Visible to the world. Requires either a human or a per-target opt-in.</summary>
    Public = 1,
}

/// <summary>What a run should actually do, once the rules have had their say.</summary>
public enum PublicationIntent
{
    /// <summary>Nothing to do: the publication already finished, or is not in a state to run.</summary>
    Skip = 0,

    /// <summary>
    /// The execution window elapsed. §11.1: the record turns Expired and no automatic action follows, because
    /// publishing hours late without anyone noticing is exactly what the window exists to prevent.
    /// </summary>
    Expire = 1,

    /// <summary>Upload to the remote as a private draft.</summary>
    UploadDraft = 2,

    /// <summary>Make it publicly visible.</summary>
    PublishPublicly = 3,
}

/// <summary>
/// Turns a publication, its target and the clock into an intent (docs/开发指导.md §11.1, decision A.2).
/// <para>
/// This is the rule behind the product's most safety-critical promise — that nothing reaches the public
/// internet without a human or an explicit per-target opt-in — so it is a pure function here in the domain
/// rather than a chain of conditions inside a job handler. §17.1 asks for the publish state transitions and
/// the execution window to be tested directly, and that is only possible if the decision can be called with
/// nothing but its inputs.
/// </para>
/// </summary>
public static class PublicationPlanner
{
    public static PublicationIntent Decide(
        Publication publication,
        PublishTarget target,
        TimeSpan window,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(target);

        // Queued is the normal case. Failed is included on purpose: it is the state a previous attempt left behind,
        // and the only thing that re-enters it is the job's own bounded retry (§14) or a human putting it back.
        // Everything else has either finished, been replaced, or belongs to a run that is still on the network —
        // and running any of those again is exactly the double-publish §14 forbids.
        if (publication.Status is not (PublicationStatus.Queued or PublicationStatus.Failed))
        {
            return PublicationIntent.Skip;
        }

        // The window is checked for both kinds of request, because what it protects against is the *delay*, not
        // the visibility: a manual request starts queued at the moment it was made, so it is never expired.
        if (PublishWindow.IsExpired(publication.ScheduledAtUtc, window, nowUtc))
        {
            return PublicationIntent.Expire;
        }

        if (publication.Trigger == PublicationTrigger.Manual)
        {
            return publication.RequestedVisibility == PublicationVisibility.Public
                ? PublicationIntent.PublishPublicly
                : PublicationIntent.UploadDraft;
        }

        // Unattended. The default behaviour is a private draft (§11.1); going public needs the opt-in, which
        // only an administrator can set and which records who set it.
        return target.AutomaticPublishEnabled && publication.RequestedVisibility == PublicationVisibility.Public
            ? PublicationIntent.PublishPublicly
            : PublicationIntent.UploadDraft;
    }
}

/// <summary>
/// Which publications a new version invalidates (docs/开发指导.md §11.1: 夜间新增素材使待发布版本失效).
/// <para>
/// The state list is deliberately short. A publication that has not run yet, or that has only reached the remote
/// as a draft, still describes a version the user has moved on from — leaving it queued would publish text they
/// have since replaced. One that is mid-flight is left alone: the request is already in the network, and marking
/// it superseded would only make the local record disagree with the remote.
/// </para>
/// </summary>
public static class PublicationSupersession
{
    public static bool ShouldSupersede(PublicationStatus status) =>
        status is PublicationStatus.Queued or PublicationStatus.DraftUploaded;
}

/// <summary>
/// How the local draft and the remote copy have diverged since the last successful publish (§11.1:
/// 用户可主动检查远程差异，并选择拉取、覆盖或保留两边).
/// </summary>
public sealed record RemoteComparison(bool RemoteChecked, bool LocalChanged, bool RemoteChanged)
{
    /// <summary>Nothing to reconcile: the remote still holds what we sent and the draft has not moved.</summary>
    public bool InSync => RemoteChecked && !LocalChanged && !RemoteChanged;

    /// <summary>Only the local side moved, so re-publishing is a normal, safe action.</summary>
    public bool OnlyLocalChanged => RemoteChecked && LocalChanged && !RemoteChanged;

    /// <summary>Only the remote side moved — the user edited the article on the site itself.</summary>
    public bool OnlyRemoteChanged => RemoteChecked && !LocalChanged && RemoteChanged;

    /// <summary>Both moved. This is the case the product must never resolve on its own.</summary>
    public bool BothChanged => RemoteChecked && LocalChanged && RemoteChanged;

    public static RemoteComparison Unknown { get; } = new(false, false, false);
}
