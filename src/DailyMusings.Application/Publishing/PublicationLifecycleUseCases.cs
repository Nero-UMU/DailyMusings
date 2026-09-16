using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Jobs;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Inputs;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;

namespace DailyMusings.Application.Publishing;

/// <summary>
/// Puts a failed or expired publication back in the queue (§13, §14).
/// <para>
/// Always a human action with a name attached: §14 makes recovery a decision, not a timer, which is what stops
/// "the container restarted" from becoming a way to publish something nobody re-approved.
/// </para>
/// </summary>
public sealed class RetryPublicationUseCase
{
    private readonly IPublicationRepository _publications;
    private readonly IClock _clock;
    private readonly JobEnqueuer _jobs;

    public RetryPublicationUseCase(IPublicationRepository publications, IClock clock, JobEnqueuer jobs)
    {
        _publications = publications;
        _clock = clock;
        _jobs = jobs;
    }

    public async Task<(Publication Publication, ProcessingJob Job)> ExecuteAsync(
        PublicationId publicationId,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var publication = await _publications.FindByIdAsync(publicationId, cancellationToken).ConfigureAwait(false)
            ?? throw new UseCaseException("publication.unknown", $"No publication with id {publicationId}.");

        publication.RequeueForManualRetry(actor, _clock.UtcNow);
        await _publications.UpdateAsync(publication, cancellationToken).ConfigureAwait(false);

        var job = await _jobs.EnsureAsync(
            JobType.Publication,
            publication.Id.ToString(),
            IdempotencyKeys.Publication(publication.ReflectionVersionId, publication.PublishTargetId, publication.ExportRound),
            new PublicationPayload(false).ToJson(),
            requeueFailed: true,
            cancellationToken).ConfigureAwait(false);

        return (publication, job);
    }
}

/// <summary>The three answers §11.1 allows when the remote and the draft have drifted apart.</summary>
public enum RemoteDivergenceAction
{
    /// <summary>Take the remote's text as the new local draft.</summary>
    Pull = 0,

    /// <summary>Push the local draft over the remote.</summary>
    Overwrite = 1,

    /// <summary>Leave both as they are, and stop reporting the difference.</summary>
    KeepBoth = 2,
}

public sealed record RemoteCheckResult(
    Publication Publication,
    PublishTarget Target,
    RemoteComparison Comparison,
    RemoteArticle? Remote,
    string? RemoteContentHash,
    string? LocalContentHash);

/// <summary>A publication together with the target it names, which is the shape every reader wants.</summary>
public sealed record PublicationView(Publication Publication, string TargetName, PublishTargetType TargetType);

/// <summary>
/// Reads publication records (docs/开发指导.md §9.3, §13).
/// <para>
/// The target's name travels with each record rather than being resolved by the caller: a client showing "已上传到
/// blog" needs it, and a repository read per row in a controller is how an endpoint ends up owning a join.
/// </para>
/// </summary>
public sealed class ListPublicationsUseCase
{
    private readonly IPublicationRepository _publications;
    private readonly IPublishTargetRepository _targets;

    public ListPublicationsUseCase(IPublicationRepository publications, IPublishTargetRepository targets)
    {
        _publications = publications;
        _targets = targets;
    }

    public async Task<IReadOnlyList<PublicationView>> ExecuteAsync(int limit, CancellationToken cancellationToken)
    {
        var items = await _publications.ListRecentAsync(limit, cancellationToken).ConfigureAwait(false);
        return await ToViewsAsync(items, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PublicationView>> ForReflectionAsync(
        ReflectionId reflectionId,
        CancellationToken cancellationToken)
    {
        var items = await _publications
            .ListByReflectionAsync(reflectionId, cancellationToken)
            .ConfigureAwait(false);

        return await ToViewsAsync(items, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PublicationView?> GetAsync(PublicationId id, CancellationToken cancellationToken)
    {
        var publication = await _publications.FindByIdAsync(id, cancellationToken).ConfigureAwait(false);

        if (publication is null)
        {
            return null;
        }

        var target = await _targets
            .FindByIdAsync(publication.PublishTargetId, cancellationToken)
            .ConfigureAwait(false);

        return new PublicationView(publication, target?.Name ?? publication.PublishTargetId.ToString(), target?.Type ?? PublishTargetType.WordPress);
    }

    private async Task<IReadOnlyList<PublicationView>> ToViewsAsync(
        IReadOnlyList<Publication> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var targets = (await _targets.ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(target => target.Id);

        return items
            .Select(publication =>
            {
                var target = targets.GetValueOrDefault(publication.PublishTargetId);

                return new PublicationView(
                    publication,
                    target?.Name ?? publication.PublishTargetId.ToString(),
                    target?.Type ?? PublishTargetType.WordPress);
            })
            .ToArray();
    }
}

/// <summary>
/// Reads the remote back and reports how it differs from what was sent (docs/开发指导.md §11.1:
/// 用户可主动检查远程差异，并选择拉取、覆盖或保留两边).
/// <para>
/// Deliberately a user-triggered operation rather than a background one. The product's contract is that local
/// changes never automatically update remote content, and a poller that refreshed this every few minutes would
/// make the two look continuously reconciled.
/// </para>
/// </summary>
public sealed class CheckRemoteUseCase
{
    private readonly IPublicationRepository _publications;
    private readonly IPublishTargetRepository _targets;
    private readonly IReflectionRepository _reflections;
    private readonly IPublishDestinationProvider _destinations;
    private readonly IRemotePublisher _wordPress;
    private readonly IMarkdownWriter _markdown;
    private readonly IClock _clock;

    public CheckRemoteUseCase(
        IPublicationRepository publications,
        IPublishTargetRepository targets,
        IReflectionRepository reflections,
        IPublishDestinationProvider destinations,
        IRemotePublisher wordPress,
        IMarkdownWriter markdown,
        IClock clock)
    {
        _publications = publications;
        _targets = targets;
        _reflections = reflections;
        _destinations = destinations;
        _wordPress = wordPress;
        _markdown = markdown;
        _clock = clock;
    }

    public async Task<RemoteCheckResult> ExecuteAsync(PublicationId publicationId, CancellationToken cancellationToken)
    {
        var publication = await _publications.FindByIdAsync(publicationId, cancellationToken).ConfigureAwait(false)
            ?? throw new UseCaseException("publication.unknown", $"No publication with id {publicationId}.");

        var target = await _targets.FindByIdAsync(publication.PublishTargetId, cancellationToken).ConfigureAwait(false)
            ?? throw new UseCaseException("publish.target.unknown", "The publish target no longer exists.");

        var destination = await _destinations.ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        var version = await _reflections
            .FindVersionAsync(publication.ReflectionVersionId, cancellationToken)
            .ConfigureAwait(false);

        var localHash = await LocalContentHashAsync(publication, version, destination, cancellationToken)
            .ConfigureAwait(false);

        RemoteArticle? article = null;
        string? remoteHash = null;

        if (destination.Type == PublishTargetType.Markdown)
        {
            // For a file, "the remote" is the file itself: its hash is the comparison.
            if (publication.RemoteId is { } fileName && !string.IsNullOrEmpty(destination.MarkdownDirectory))
            {
                remoteHash = await _markdown
                    .ReadHashAsync(destination.MarkdownDirectory, fileName, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else if (publication.RemoteId is { } remoteId)
        {
            var site = destination.RequireWordPress();
            article = await _wordPress.GetAsync(site, remoteId, cancellationToken).ConfigureAwait(false);

            if (article is not null)
            {
                remoteHash = RemoteContentFingerprint.Of(article.Title, article.Content);
            }
        }

        if (remoteHash is null)
        {
            // The article is gone from the remote. Reported as unknown rather than as a difference: there is
            // nothing to compare against, and pretending otherwise would offer the user a choice about nothing.
            publication.RecordRemoteObservation(string.Empty, _clock.UtcNow);
            await _publications.UpdateAsync(publication, cancellationToken).ConfigureAwait(false);

            return new RemoteCheckResult(publication, target, RemoteComparison.Unknown, article, null, localHash);
        }

        publication.RecordRemoteObservation(remoteHash, _clock.UtcNow);
        await _publications.UpdateAsync(publication, cancellationToken).ConfigureAwait(false);

        return new RemoteCheckResult(
            publication,
            target,
            publication.CompareWithRemote(localHash),
            article,
            remoteHash,
            localHash);
    }

    /// <summary>
    /// What the draft would send right now. Compared against the recorded publish to answer "has the draft moved
    /// since we sent it", which is the local half of the difference.
    /// </summary>
    public async Task<string?> LocalContentHashAsync(
        Publication publication,
        ReflectionVersion? version,
        PublishDestination destination,
        CancellationToken cancellationToken)
    {
        if (version is null)
        {
            return null;
        }

        if (destination.Type == PublishTargetType.Markdown)
        {
            // The reflection lookup is what supplies the content day; a Markdown file's identity depends on it.
            var reflection = await _reflections
                .FindByIdAsync(publication.ReflectionId, cancellationToken)
                .ConfigureAwait(false);

            return reflection is null
                ? null
                : MarkdownFileHash.Of(
                    MarkdownTemplate.DefaultTemplate.Render(
                        MarkdownDocument.From(version, reflection.ContentDate)));
        }

        return RemoteContentFingerprint.Of(version.Title, WordPressBody.Render(version));
    }
}

/// <summary>
/// Carries out the user's answer to a difference (§11.1).
/// </summary>
public sealed class ResolveRemoteDivergenceUseCase
{
    private readonly IPublicationRepository _publications;
    private readonly IPublishTargetRepository _targets;
    private readonly IReflectionRepository _reflections;
    private readonly IPublishDestinationProvider _destinations;
    private readonly IRemotePublisher _wordPress;
    private readonly IMarkdownWriter _markdown;
    private readonly CheckRemoteUseCase _check;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly JobEnqueuer _jobs;

    public ResolveRemoteDivergenceUseCase(
        IPublicationRepository publications,
        IPublishTargetRepository targets,
        IReflectionRepository reflections,
        IPublishDestinationProvider destinations,
        IRemotePublisher wordPress,
        IMarkdownWriter markdown,
        CheckRemoteUseCase check,
        IUnitOfWork unitOfWork,
        IClock clock,
        JobEnqueuer jobs)
    {
        _publications = publications;
        _targets = targets;
        _reflections = reflections;
        _destinations = destinations;
        _wordPress = wordPress;
        _markdown = markdown;
        _check = check;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _jobs = jobs;
    }

    /// <param name="allowOverwriteOfManualEdits">
    /// §6.4 again: pulling the remote's text installs a new working version, and if the one it displaces carries
    /// hand edits the user has to have accepted that. Their having asked for a pull is not by itself consent to
    /// lose a different edit, so the client warns first and passes this flag only if they agreed.
    /// </param>
    public async Task<RemoteCheckResult> ExecuteAsync(
        PublicationId publicationId,
        RemoteDivergenceAction action,
        string actor,
        bool allowOverwriteOfManualEdits,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var publication = await _publications.FindByIdAsync(publicationId, cancellationToken).ConfigureAwait(false)
            ?? throw new UseCaseException("publication.unknown", $"No publication with id {publicationId}.");

        var target = await _targets.FindByIdAsync(publication.PublishTargetId, cancellationToken).ConfigureAwait(false)
            ?? throw new UseCaseException("publish.target.unknown", "The publish target no longer exists.");

        var destination = await _destinations.ResolveAsync(target, cancellationToken).ConfigureAwait(false);

        switch (action)
        {
            case RemoteDivergenceAction.KeepBoth:
                // Nothing moves. The only thing that changes is that the user has been asked and has answered.
                publication.AcknowledgeRemoteDivergence(_clock.UtcNow);
                await _publications.UpdateAsync(publication, cancellationToken).ConfigureAwait(false);
                break;

            case RemoteDivergenceAction.Overwrite:
                // Pushing the local version over the remote only makes sense while the attempt has not finished.
                // Once the article is public, §11.1's own state machine makes that record terminal: a change to a
                // published article goes out as a new version the user confirms, which produces its own
                // publication. Saying so is better than letting the domain's refusal surface as a server error.
                if (publication.Status is PublicationStatus.Published or PublicationStatus.Superseded)
                {
                    throw new UseCaseException(
                        "publication.overwrite.not_applicable",
                        publication.Status == PublicationStatus.Published
                            ? "That article is already public. Confirm a new version of the draft to send a change."
                            : "That attempt was replaced by a newer version of the draft.");
                }

                // A new round, so the job is a new job rather than a collision with the one that already succeeded.
                publication.RequeueForReExport(publication.RequestedVisibility, actor, _clock.UtcNow);
                await _publications.UpdateAsync(publication, cancellationToken).ConfigureAwait(false);

                await _jobs.EnsureAsync(
                    JobType.Publication,
                    publication.Id.ToString(),
                    IdempotencyKeys.Publication(
                        publication.ReflectionVersionId,
                        publication.PublishTargetId,
                        publication.ExportRound),
                    new PublicationPayload(ReplaceExistingFile: true).ToJson(),
                    requeueFailed: false,
                    cancellationToken).ConfigureAwait(false);
                break;

            case RemoteDivergenceAction.Pull:
                await PullAsync(publication, destination, allowOverwriteOfManualEdits, cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                throw new UseCaseException("publication.divergence.unknown_action", $"Unknown action {action}.");
        }

        return await _check.ExecuteAsync(publicationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Brings the remote's text into the draft as a new version.
    /// <para>
    /// The new version is marked as hand-edited, because it is: the text came from a person typing in WordPress,
    /// and §6.4's protection against silent regeneration exists precisely to keep that from being rotated away by
    /// the next generation run.
    /// </para>
    /// </summary>
    private async Task PullAsync(
        Publication publication,
        PublishDestination destination,
        bool allowOverwriteOfManualEdits,
        CancellationToken cancellationToken)
    {
        if (publication.RemoteId is not { } remoteId)
        {
            throw new UseCaseException("publication.remote_id_missing", "This publication has no remote article.");
        }

        if (destination.Type == PublishTargetType.Markdown)
        {
            // A file the user edits is theirs to edit in their own editor. Copying it back into the draft would
            // make the export directory a second source of truth, which is exactly what §11.2 avoids.
            throw new UseCaseException(
                "publication.pull.not_supported",
                "A Markdown export cannot be pulled back; read the file instead.");
        }

        var article = await _wordPress
            .GetAsync(destination.RequireWordPress(), remoteId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UseCaseException("publication.remote_missing", "The remote article no longer exists.");

        var title = string.IsNullOrWhiteSpace(article.Title) ? "来自远端的修改" : article.Title;
        var body = WordPressBody.ToPlainText(article.Content);

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new UseCaseException("publication.pull.empty", "The remote article has no text to pull.");
        }

        var reflection = await _reflections
            .FindByIdAsync(publication.ReflectionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UseCaseException("reflection.unknown", "The draft no longer exists.");

        var now = _clock.UtcNow;

        var version = ReflectionVersion.CreateGenerated(
            ReflectionVersionId.New(),
            reflection.Id,
            title,
            string.Empty,
            body,
            WritingSettings.Default,
            modelInfo: null,
            promptVersion: null,
            createdAtUtc: now);

        // A pull is a human edit by another route, so it carries the same protection.
        version.Edit(title, string.Empty, body, now);

        if (reflection.Status == ReflectionStatus.PendingInputs)
        {
            reflection.MarkReady(now);
        }

        // §6.3 gives Confirmed exactly one outgoing edge, and this is the one it means. Pulling text a person
        // typed on the site means the draft is no longer what was agreed, so the day goes stale first and the pull
        // lands as a new version awaiting re-confirmation — which is what §7 says happens to a confirmed draft
        // whose basis moved.
        if (reflection.Status == ReflectionStatus.Confirmed)
        {
            reflection.MarkStaleByLateInput(now);
        }

        var working = reflection.WorkingVersionId is { } workingId
            ? await _reflections.FindVersionAsync(workingId, cancellationToken).ConfigureAwait(false)
            : null;

        reflection.BeginGeneration(GenerationReason.Manual, now);
        reflection.ApplyGeneratedVersion(
            version.Id,
            working?.HasManualEdits ?? false,
            allowOverwriteOfManualEdits,
            now);

        await using (var transaction = await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            await _reflections.UpdateAsync(reflection, cancellationToken).ConfigureAwait(false);
            await _reflections.AddVersionAsync(version, cancellationToken).ConfigureAwait(false);
            await _reflections.ReplaceSourcesAsync(version.Id, [], cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
