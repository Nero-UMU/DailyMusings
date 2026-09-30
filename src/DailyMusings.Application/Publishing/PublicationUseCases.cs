using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Jobs;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;

namespace DailyMusings.Application.Publishing;

/// <summary>
/// The hash of an exported file's contents.
/// <para>
/// Shared by the writer and the difference check on purpose: §11.2's promise is that a file we wrote is only ever
/// replaced while it still holds exactly what we wrote, and that comparison is only meaningful if both sides
/// compute the same thing over the same bytes.
/// </para>
/// </summary>
public static class MarkdownFileHash
{
    public static string Of(string? content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty)));
}

/// <summary>The parameters a persisted publication job carries.</summary>
public sealed record PublicationPayload(bool ReplaceExistingFile)
{
    public string ToJson() => JsonSerializer.Serialize(this);

    public static PublicationPayload FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new PublicationPayload(false);
        }

        try
        {
            return JsonSerializer.Deserialize<PublicationPayload>(json) ?? new PublicationPayload(false);
        }
        catch (JsonException)
        {
            return new PublicationPayload(false);
        }
    }
}

/// <summary>
/// What a publish request actually did.
/// <para>
/// The distinction between <see cref="Created"/> and <see cref="AlreadyQueued"/> exists because the scheduler runs
/// every couple of seconds and asks the same question each time. Collapsing the two into one "queued" flag made the
/// log claim work was being queued on every tick, which is exactly the kind of noise that hides a real problem.
/// </para>
/// </summary>
public enum PublicationRequestOutcome
{
    /// <summary>This call created the publication, or put a finished one back in the queue.</summary>
    Created = 0,

    /// <summary>Work for this version and target is already waiting. Nothing new happened.</summary>
    AlreadyQueued = 1,

    /// <summary>It has already gone to the target and nobody asked to send it again.</summary>
    AlreadyFinished = 2,

    /// <summary>Refused: the day or the target is not in a state where publishing is allowed.</summary>
    Refused = 3,
}

/// <summary>What a caller asked for, and what happened.</summary>
public sealed record PublicationRequestResult(
    PublicationRequestOutcome Outcome,
    string? Code,
    string? Detail,
    Publication? Publication,
    ProcessingJob? Job)
{
    /// <summary>True when the client can expect the publication to happen.</summary>
    public bool Queued => Outcome is PublicationRequestOutcome.Created or PublicationRequestOutcome.AlreadyQueued;
}

/// <summary>
/// Queues one version to one target (docs/开发指导.md §11.1, §13).
/// <para>
/// Two rules are enforced here rather than in the controller, because both are promises the product makes. Only a
/// <em>confirmed</em> draft may be published — "默认只生成私人草稿，由你核验后才发布" is only true if the code
/// refuses anything else. And the request is idempotent per version and target, so a client that retries a
/// timed-out upload request cannot produce a second article on the blog.
/// </para>
/// </summary>
public sealed class RequestPublicationUseCase
{
    private readonly IReflectionRepository _reflections;
    private readonly IPublishTargetRepository _targets;
    private readonly IPublicationRepository _publications;
    private readonly IContentSettingsProvider _settings;
    private readonly IClock _clock;
    private readonly JobEnqueuer _jobs;

    public RequestPublicationUseCase(
        IReflectionRepository reflections,
        IPublishTargetRepository targets,
        IPublicationRepository publications,
        IContentSettingsProvider settings,
        IClock clock,
        JobEnqueuer jobs)
    {
        _reflections = reflections;
        _targets = targets;
        _publications = publications;
        _settings = settings;
        _clock = clock;
        _jobs = jobs;
    }

    /// <param name="visibility">Whether the request wants a private draft or a public article.</param>
    /// <param name="actor">
    /// Who asked. Mandatory for a manual request: §11.1 requires the audit trail, and a publication that cannot
    /// name its trigger cannot be distinguished from an automatic one after the fact.
    /// </param>
    /// <param name="replaceExistingFile">
    /// §11.2 only: replace the file this target wrote before rather than creating a versioned one. Requires the
    /// file to still be exactly what was written — the write policy refuses otherwise.
    /// </param>
    public async Task<PublicationRequestResult> ExecuteAsync(
        ContentDate contentDate,
        PublishTargetId targetId,
        PublicationVisibility visibility,
        string? actor,
        bool replaceExistingFile,
        bool manual,
        CancellationToken cancellationToken)
    {
        var reflection = await _reflections
            .FindByContentDateAsync(contentDate, cancellationToken)
            .ConfigureAwait(false);

        if (reflection is null)
        {
            return new PublicationRequestResult(PublicationRequestOutcome.Refused, "publication.reflection_unknown", "That day has no draft.", null, null);
        }

        if (reflection.Status != ReflectionStatus.Confirmed || reflection.ConfirmedVersionId is null)
        {
            return new PublicationRequestResult(
                PublicationRequestOutcome.Refused,
                "publication.reflection_not_confirmed",
                "Only a confirmed draft can be published.",
                null,
                null);
        }

        var target = await _targets.FindByIdAsync(targetId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return new PublicationRequestResult(PublicationRequestOutcome.Refused, "publish.target.unknown", "That publish target does not exist.", null, null);
        }

        var versionId = reflection.ConfirmedVersionId.Value;
        var now = _clock.UtcNow;

        var publication = await _publications
            .FindByVersionAndTargetAsync(versionId, targetId, cancellationToken)
            .ConfigureAwait(false);

        // Whether this call is the one that put the work in the queue, which is what the caller's log line and the
        // scheduler's counter both want to know.
        var created = false;

        if (publication is null)
        {
            // The slot comes from one place (ContentSettings.PublishSlotFor): 默认 23:00 生成、次日 08:00 发布，
            // 而发布时间晚于生成时间时是当天。A manual request is due immediately: the user is standing there.
            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            var scheduledAt = manual
                ? now
                : settings.PublishSlotFor(contentDate);

            if (!manual && scheduledAt > now)
            {
                // Checked here rather than only in the scheduler, so every caller gets the same answer: an
                // unattended publication exists to run in a slot, and running early would be a different product.
                return new PublicationRequestResult(
                    PublicationRequestOutcome.Refused,
                    "publication.slot_not_due",
                    "The day's publication slot has not arrived yet.",
                    null,
                    null);
            }

            publication = Publication.Create(
                PublicationId.New(),
                reflection.Id,
                versionId,
                targetId,
                manual ? PublicationTrigger.Manual : PublicationTrigger.Automatic,
                scheduledAt,
                visibility,
                requestedBy: manual ? actor : null);

            await _publications.AddAsync(publication, cancellationToken).ConfigureAwait(false);
            created = true;
        }
        else
        {
            // Already there. What happens next depends on where it got to.
            switch (publication.Status)
            {
                case PublicationStatus.DraftUploaded:
                case PublicationStatus.Failed:
                case PublicationStatus.Expired:
                    if (!manual)
                    {
                        // The unattended path never revisits a finished attempt (§11.2): a draft that is already on
                        // the target is exactly the outcome the slot wanted, and re-sending it every few seconds
                        // would be a loop with the user's blog at the end of it.
                        return new PublicationRequestResult(
                            PublicationRequestOutcome.AlreadyFinished,
                            "publication.already_finished",
                            $"This version is already at that target as {publication.Status}.",
                            publication,
                            null);
                    }

                    // A human asking again is how §11.2's "export again" and §11.1's "overwrite the remote" work.
                    publication.RequeueForReExport(visibility, actor ?? "admin", now);
                    await _publications.UpdateAsync(publication, cancellationToken).ConfigureAwait(false);
                    created = true;
                    break;

                case PublicationStatus.Published:
                    return new PublicationRequestResult(
                        PublicationRequestOutcome.AlreadyFinished,
                        "publication.already_published",
                        "This version is already published to that target.",
                        publication,
                        null);

                case PublicationStatus.Superseded:
                    return new PublicationRequestResult(
                        PublicationRequestOutcome.AlreadyFinished,
                        "publication.superseded",
                        "That attempt was replaced by a newer version of the draft.",
                        publication,
                        null);

                default:
                    // Queued or in flight: the request is already being honoured, so answering with the existing
                    // job is the idempotent answer — and reporting it as "already queued" is what keeps the
                    // scheduler's log from claiming new work on every tick.
                    break;
            }
        }
        var job = await _jobs.EnsureAsync(
            JobType.Publication,
            publication.Id.ToString(),
            IdempotencyKeys.Publication(versionId, targetId, publication.ExportRound),
            new PublicationPayload(replaceExistingFile).ToJson(),
            requeueFailed: manual,
            cancellationToken).ConfigureAwait(false);

        return new PublicationRequestResult(
            created ? PublicationRequestOutcome.Created : PublicationRequestOutcome.AlreadyQueued,
            null,
            null,
            publication,
            job);
    }
}

/// <summary>How one publication run ended.</summary>
public enum PublicationRunOutcome
{
    Uploaded = 0,
    Published = 1,
    Expired = 2,
    Skipped = 3,
}

public sealed record PublicationRunResult(PublicationRunOutcome Outcome, Publication? Publication, string? RemoteId)
{
    public bool NotifyUser => Outcome is PublicationRunOutcome.Uploaded or PublicationRunOutcome.Published or PublicationRunOutcome.Expired;
}

/// <summary>
/// Performs one publication (docs/开发指导.md §11.1, §11.2).
/// <para>
/// The decision of what to do is <see cref="PublicationPlanner"/>'s; this class only carries it out. That split is
/// deliberate: the rule that decides between "upload a draft", "publish publicly" and "stop, the window has
/// passed" is the one thing in this feature that must never be got wrong, and it is testable only if it can be
/// called without a remote, a database or a clock.
/// </para>
/// </summary>
public sealed class RunPublicationUseCase
{
    private readonly IPublicationRepository _publications;
    private readonly IPublishTargetRepository _targets;
    private readonly IReflectionRepository _reflections;
    private readonly IPublishDestinationProvider _destinations;
    private readonly IMarkdownWriter _markdown;
    private readonly IContentSettingsProvider _settings;
    private readonly IClock _clock;
    private readonly Notifications.QueueNotificationUseCase _notifications;

    public RunPublicationUseCase(
        IPublicationRepository publications,
        IPublishTargetRepository targets,
        IReflectionRepository reflections,
        IPublishDestinationProvider destinations,
        IMarkdownWriter markdown,
        IContentSettingsProvider settings,
        IClock clock,
        Notifications.QueueNotificationUseCase notifications)
    {
        _publications = publications;
        _targets = targets;
        _reflections = reflections;
        _destinations = destinations;
        _markdown = markdown;
        _settings = settings;
        _clock = clock;
        _notifications = notifications;
    }

    public async Task<PublicationRunResult> ExecuteAsync(
        PublicationId publicationId,
        PublicationPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var now = _clock.UtcNow;

        var publication = await _publications.FindByIdAsync(publicationId, cancellationToken).ConfigureAwait(false);
        if (publication is null)
        {
            return new PublicationRunResult(PublicationRunOutcome.Skipped, null, null);
        }

        var target = await _targets.FindByIdAsync(publication.PublishTargetId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            throw new PermanentExternalFailureException(
                "publication.target_missing",
                "这个发布目标已经不存在了。");
        }

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        var intent = PublicationPlanner.Decide(publication, target, settings.PublishWindow, now);

        if (intent == PublicationIntent.Skip)
        {
            return new PublicationRunResult(PublicationRunOutcome.Skipped, publication, publication.RemoteId);
        }

        if (intent == PublicationIntent.Expire)
        {
            // §11.1: the execution window elapsed. No public action follows, and only the user can put it back in
            // the queue — which is what stops a restarted container from publishing last night's draft at noon.
            publication.Expire(now);
            await _publications.UpdateAsync(publication, cancellationToken).ConfigureAwait(false);
            await NotifyAsync(publication, PublicationStatus.Expired, target.Name, "publication.window_expired", cancellationToken)
                .ConfigureAwait(false);

            return new PublicationRunResult(PublicationRunOutcome.Expired, publication, publication.RemoteId);
        }

        var reflection = await _reflections.FindByIdAsync(publication.ReflectionId, cancellationToken).ConfigureAwait(false);
        var version = await _reflections
            .FindVersionAsync(publication.ReflectionVersionId, cancellationToken)
            .ConfigureAwait(false);

        if (reflection is null || version is null)
        {
            throw new PermanentExternalFailureException(
                "publication.version_missing",
                "The version being published no longer exists.");
        }

        // A failed attempt leaves the record failed so the user can see it; the job's own retry re-enters it here.
        if (publication.Status == PublicationStatus.Failed)
        {
            publication.ResumeAfterFailedAttempt(publication.TriggeredBy ?? "system:retry", now);
        }
        else
        {
            publication.Begin(publication.TriggeredBy ?? "system:scheduler", now);
        }

        await _publications.UpdateAsync(publication, cancellationToken).ConfigureAwait(false);

        var destination = await _destinations.ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        var publishPublicly = intent == PublicationIntent.PublishPublicly;
        var previousPublications = publishPublicly
            ? (await _publications.ListByReflectionAsync(reflection.Id, cancellationToken).ConfigureAwait(false))
                .Where(item =>
                    item.Id != publication.Id &&
                    item.PublishTargetId == publication.PublishTargetId &&
                    item.Status == PublicationStatus.Published)
                .ToArray()
            : [];

        try
        {
            foreach (var previous in previousPublications)
            {
                if (previous.RemoteId is not { Length: > 0 } previousFile ||
                    previous.PublishedContentHash is not { Length: > 0 } previousHash)
                {
                    continue;
                }

                var currentHash = await _markdown
                    .ReadHashAsync(destination.MarkdownDirectory, previousFile, cancellationToken)
                    .ConfigureAwait(false);

                if (currentHash is not null && !string.Equals(currentHash, previousHash, StringComparison.Ordinal))
                {
                    throw new PermanentExternalFailureException(
                        "markdown.previous_file.modified_externally",
                        "The previously published file was changed outside the product and was not removed.");
                }
            }

            var outcome = await PublishMarkdownAsync(
                publication,
                version,
                reflection.ContentDate,
                destination,
                payload,
                publishPublicly,
                settings.HexoFrontMatterTemplate,
                cancellationToken).ConfigureAwait(false);

            foreach (var previous in previousPublications)
            {
                if (previous.RemoteId is { Length: > 0 } previousFile &&
                    previous.PublishedContentHash is { Length: > 0 } previousHash &&
                    !string.Equals(previousFile, publication.RemoteId, StringComparison.Ordinal))
                {
                    var removed = await _markdown
                        .DeleteIfUnchangedAsync(destination.MarkdownDirectory, previousFile, previousHash, cancellationToken)
                        .ConfigureAwait(false);

                    if (!removed)
                    {
                        throw new PermanentExternalFailureException(
                            "markdown.previous_file.modified_externally",
                            "The previously published file was changed outside the product and was not removed.");
                    }
                }

                previous.Supersede(_clock.UtcNow);
                await _publications.UpdateAsync(previous, cancellationToken).ConfigureAwait(false);
            }

            await _publications.UpdateAsync(publication, cancellationToken).ConfigureAwait(false);
            await NotifyAsync(publication, publication.Status, target.Name, publication.ErrorCode, cancellationToken)
                .ConfigureAwait(false);

            return outcome;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The record follows reality on every failed attempt (see ResumeAfterFailedAttempt), so the user can
            // see the failure while the job is still retrying rather than a status that says "in progress" for
            // as long as the retries last.
            publication.Fail(
                exception switch
                {
                    TransientExternalFailureException transient => transient.Code,
                    PermanentExternalFailureException permanent => permanent.Code,
                    DomainException domain => domain.Code,
                    _ => "publication.unexpected",
                },
                exception.GetType().Name,
                now,
                RetryPolicy.Default);

            await _publications.UpdateAsync(publication, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<PublicationRunResult> PublishMarkdownAsync(
        Publication publication,
        ReflectionVersion version,
        ContentDate contentDate,
        PublishDestination destination,
        PublicationPayload payload,
        bool publishPublicly,
        string hexoFrontMatterTemplate,
        CancellationToken cancellationToken)
    {
        // Visibility means one thing for a file: whether Hexo is told this post is still a draft. That is the
        // front matter's `draft` field, and it is the whole of what "公开" can be for the only target kind left.
        var document = MarkdownDocument.From(version, contentDate, isDraft: !publishPublicly);
        var content = new MarkdownTemplate(hexoFrontMatterTemplate).Render(document);

        var baseName = MarkdownFileName.BaseName(contentDate, document.Slug);

        // The file this target wrote last time. §11.2's whole safety story is here: we only ever replace a file
        // that is still exactly what we wrote, and never one we did not write at all.
        var previousFileName = publication.RemoteId;
        var previousHash = publication.PublishedContentHash;

        var write = await _markdown
            .WriteAsync(
                new MarkdownWriteRequest(
                    destination.MarkdownDirectory ?? string.Empty,
                    baseName,
                    content,
                    previousFileName,
                    previousHash,
                    payload.ReplaceExistingFile),
                cancellationToken)
            .ConfigureAwait(false);

        if (write.Plan is MarkdownWritePlan.RefuseUnowned or MarkdownWritePlan.RefuseExternallyModified)
        {
            // Not retryable: the same request would be refused again, and the answer is for the user to look at
            // the file rather than for the queue to try harder.
            throw new PermanentExternalFailureException(
                write.Plan == MarkdownWritePlan.RefuseUnowned
                    ? "markdown.file.not_ours"
                    : "markdown.file.modified_externally",
                write.Plan == MarkdownWritePlan.RefuseUnowned
                    ? "A file of that name already exists and was not written by this instance."
                    : "The exported file has been changed outside the product.");
        }

        // Written as a draft first in both cases, then promoted: that is the state machine's only path to
        // Published, and it keeps "the file was written" and "the post is public" as two separate facts.
        publication.CompleteAsDraft(write.FileName, write.ContentHash, _clock.UtcNow);

        if (publishPublicly)
        {
            publication.PromoteDraftToPublished(_clock.UtcNow);
        }

        return new PublicationRunResult(
            publishPublicly ? PublicationRunOutcome.Published : PublicationRunOutcome.Uploaded,
            publication,
            write.FileName);
    }

    /// <summary>
    /// Queues the §12 notification for an automatic outcome. Only automatic ones: a manual request was made by
    /// someone who is already looking at the screen, and mailing them about their own click is noise.
    /// </summary>
    private async Task NotifyAsync(
        Publication publication,
        PublicationStatus status,
        string targetName,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        if (publication.Trigger != PublicationTrigger.Automatic)
        {
            return;
        }

        await _notifications
            .QueuePublicationAsync(publication.Id, status, targetName, publication.RemoteId, errorCode, cancellationToken)
            .ConfigureAwait(false);
    }
}
