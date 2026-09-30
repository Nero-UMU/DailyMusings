using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Application.Jobs;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Notifications;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Reflections;
using DailyMusings.Domain.Time;

namespace DailyMusings.Application.Publishing;

public sealed record PublicationScheduleResult(int Queued, int Expired, int Superseded);

/// <summary>
/// The publish half of the clock (docs/开发指导.md §7, §11.1): the nightly slot, the execution window, and the
/// invalidation of a pending version when the draft moves on.
/// <para>
/// Like the generation scheduler it only ever enqueues or records state — every rule it applies lives in the
/// domain, so the scheduled path and the manual path cannot drift apart. It is also where three separately
/// documented behaviours meet, which is worth naming: the 08:00 slot starts an upload, the 120-minute window
/// stops one that never ran, and new material invalidates one that has not gone out yet.
/// </para>
/// </summary>
public sealed class SchedulePublicationsUseCase
{
    /// <summary>How far back to look for confirmed days whose slot has arrived but which never ran.</summary>
    private const int LookBackDays = 30;

    private readonly IReflectionRepository _reflections;
    private readonly IPublishTargetRepository _targets;
    private readonly IPublicationRepository _publications;
    private readonly IContentSettingsProvider _settings;
    private readonly IClock _clock;
    private readonly RequestPublicationUseCase _request;
    private readonly Notifications.QueueNotificationUseCase _notifications;

    public SchedulePublicationsUseCase(
        IReflectionRepository reflections,
        IPublishTargetRepository targets,
        IPublicationRepository publications,
        IContentSettingsProvider settings,
        IClock clock,
        RequestPublicationUseCase request,
        Notifications.QueueNotificationUseCase notifications)
    {
        _reflections = reflections;
        _targets = targets;
        _publications = publications;
        _settings = settings;
        _clock = clock;
        _request = request;
        _notifications = notifications;
    }

    public async Task<PublicationScheduleResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var calendar = settings.CreateCalendar();
        var now = _clock.UtcNow;
        var today = calendar.ContentDateOf(now);

        await QueueUnpublishedReplacementRemindersAsync(settings, today, cancellationToken).ConfigureAwait(false);
        var queued = await QueueDuePublicationsAsync(settings, today, cancellationToken).ConfigureAwait(false);
        var expired = await ExpireOverdueAsync(settings, cancellationToken).ConfigureAwait(false);
        var superseded = await SupersedeStaleAsync(cancellationToken).ConfigureAwait(false);

        return new PublicationScheduleResult(queued, expired, superseded);
    }

    /// <summary>
    /// Reminds the owner when a regenerated working version reaches the publish slot while an older version is
    /// still public. Regeneration and confirmation are both editorial actions; neither authorizes replacing a
    /// public file. The reminder is therefore the scheduler's only action until the user explicitly publishes.
    /// </summary>
    private async Task QueueUnpublishedReplacementRemindersAsync(
        Configuration.ContentSettings settings,
        ContentDate today,
        CancellationToken cancellationToken)
    {
        var reflections = await _reflections
            .ListByDateRangeAsync(today.AddDays(-LookBackDays), today, cancellationToken)
            .ConfigureAwait(false);
        var now = _clock.UtcNow;

        foreach (var reflection in reflections.OrderBy(candidate => candidate.ContentDate))
        {
            if (reflection.WorkingVersionId is not { } workingVersionId ||
                settings.PublishSlotFor(reflection.ContentDate) > now)
            {
                continue;
            }

            var publications = await _publications
                .ListByReflectionAsync(reflection.Id, cancellationToken)
                .ConfigureAwait(false);

            if (!publications.Any(publication =>
                    publication.Status == PublicationStatus.Published &&
                    publication.ReflectionVersionId != workingVersionId))
            {
                continue;
            }

            var workingVersion = await _reflections
                .FindVersionAsync(workingVersionId, cancellationToken)
                .ConfigureAwait(false);

            await _notifications
                .QueueUnpublishedAtPublishTimeAsync(
                    reflection.ContentDate,
                    workingVersionId,
                    workingVersion?.Title,
                    workingVersion?.Body,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts the upload for every day whose 08:00 slot has arrived. The targets are all of them: §11.1's
    /// per-target opt-in decides whether the run may go <em>public</em>, not whether it happens at all — the
    /// documented default is a private draft on every configured target.
    /// </summary>
    private async Task<int> QueueDuePublicationsAsync(
        Configuration.ContentSettings settings,
        ContentDate today,
        CancellationToken cancellationToken)
    {
        var targets = await _targets.ListAsync(cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            return 0;
        }

        var reflections = await _reflections
            .ListByDateRangeAsync(today.AddDays(-LookBackDays), today, cancellationToken)
            .ConfigureAwait(false);

        var calendar = settings.CreateCalendar();
        var now = _clock.UtcNow;
        var queued = 0;

        foreach (var reflection in reflections
            .Where(candidate => candidate.WorkingVersionId is not null)
            .OrderBy(candidate => candidate.ContentDate))
        {
            // §11.1: 默认 23:00 生成、次日 08:00 发布；发布时间晚于生成时间则是当天（见 PublishSlotFor）。
            if (settings.PublishSlotFor(reflection.ContentDate) > now)
            {
                continue;
            }

            var activePublications = await _publications
                .ListByReflectionAsync(reflection.Id, cancellationToken)
                .ConfigureAwait(false);

            foreach (var target in targets)
            {
                // 附录 A.33（2026-09-30 用户要求）：勾选了「允许自动公开发布」的目标发**当天的工作稿**——勾选本身就是
                // 那个授权，不再要求先有人确认；没勾选的目标保持 §11.1 原文，只发已确认的稿。
                var versionToPublish = target.AutomaticPublishEnabled
                    ? reflection.WorkingVersionId
                    : reflection.Status == ReflectionStatus.Confirmed ? reflection.ConfirmedVersionId : null;

                if (versionToPublish is null)
                {
                    continue;
                }

                if (activePublications.Any(publication =>
                        publication.Status == PublicationStatus.Published &&
                        publication.ReflectionVersionId != versionToPublish))
                {
                    // 已经公开的是另一版：自动动作不替换已公开的文件（那要人来做），只由上面的提醒告诉用户。
                    continue;
                }

                var result = await _request
                    .ExecuteAsync(
                        reflection.ContentDate,
                        target.Id,

                        // §11.1: the per-target opt-in decides whether the unattended run may go public. This used
                        // to pass Draft unconditionally, which made the switch unreachable rather than merely
                        // conservative: the planner publishes publicly only when the record *wants* public and the
                        // target opted in, so "自动公开" was stored, audited and shown on the settings page while
                        // nothing could ever go public through it.
                        target.AutomaticPublishEnabled ? PublicationVisibility.Public : PublicationVisibility.Draft,

                        actor: "system:scheduler",
                        replaceExistingFile: false,
                        manual: false,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (result.Outcome == PublicationRequestOutcome.Created)
                {
                    queued++;
                }
            }
        }

        return queued;
    }

    /// <summary>
    /// Turns queued publications whose window has elapsed into <see cref="PublicationStatus.Expired"/>
    /// (decision A.2). Nothing else happens to them: §14 says a missed slot is a decision for the user, and a
    /// restart is explicitly not a reason to extend it.
    /// </summary>
    private async Task<int> ExpireOverdueAsync(
        Configuration.ContentSettings settings,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var outstanding = await _publications.ListOutstandingAsync(200, cancellationToken).ConfigureAwait(false);
        var expired = 0;

        foreach (var publication in outstanding)
        {
            // Only queued ones: an in-flight attempt belongs to its job, and marking it expired underneath the job
            // would leave the record contradicting what is actually happening on the network.
            if (publication.Status != PublicationStatus.Queued)
            {
                continue;
            }

            if (!PublishWindow.IsExpired(publication.ScheduledAtUtc, settings.PublishWindow, now))
            {
                continue;
            }

            publication.Expire(now);
            await _publications.UpdateAsync(publication, cancellationToken).ConfigureAwait(false);
            expired++;

            // §12's "自动发布成功或失败": an expiry is the failure mode that matters most, because the whole point
            // of the window is that a human has to find out rather than the instance deciding later.
            var target = await _targets
                .FindByIdAsync(publication.PublishTargetId, cancellationToken)
                .ConfigureAwait(false);

            await _notifications
                .QueuePublicationAsync(
                    publication.Id,
                    PublicationStatus.Expired,
                    target?.Name ?? publication.PublishTargetId.ToString(),
                    publication.RemoteId,
                    errorCode: "publication.window_expired",
                    // 超时这封信不附正文（附录 A.34）：它要说的就是「什么都没发生，原因在此」。
                    content: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return expired;
    }

    /// <summary>
    /// Invalidates pending publications for a version the draft has moved past (§11.1: 夜间新增素材使待发布版本
    /// 失效；补生成后才重新进入发布队列).
    /// <para>
    /// Expressed as a reconciliation rather than as a step inside ingestion or generation, because both of those
    /// are places this rule would have to be remembered. The condition is simply "the day is no longer sitting on
    /// this confirmed version", which covers new material arriving, a regeneration finishing, and a user
    /// confirming a different version.
    /// </para>
    /// </summary>
    private async Task<int> SupersedeStaleAsync(CancellationToken cancellationToken)
    {
        var outstanding = await _publications.ListOutstandingAsync(200, cancellationToken).ConfigureAwait(false);
        var superseded = 0;
        var now = _clock.UtcNow;

        foreach (var publication in outstanding)
        {
            if (!PublicationSupersession.ShouldSupersede(publication.Status))
            {
                continue;
            }

            var reflection = await _reflections
                .FindByIdAsync(publication.ReflectionId, cancellationToken)
                .ConfigureAwait(false);

            // 「还作数」= 这一天仍然停在这一版上，而且这一版对它仍然成立：
            //   * 已确认的那一版：只有状态仍是「已确认」时才算数——新增素材把它标成过期时，待发布的那一版随之失效
            //     （§11.1 原文，New_material_invalidates_a_pending_publication 钉住的就是这条）。
            //   * 未确认的工作稿：附录 A.33 的自动发布针对的正是它，所以只要这一天还停在这一版上就算数；
            //     这里若要求「已确认」，刚排上的自动发布会在这个 tick 里被自己作废。
            var stillCurrent = reflection is not null &&
                               (reflection.ConfirmedVersionId == publication.ReflectionVersionId
                                   ? reflection.Status == ReflectionStatus.Confirmed
                                   : reflection.WorkingVersionId == publication.ReflectionVersionId);

            if (stillCurrent)
            {
                continue;
            }

            publication.Supersede(now);
            await _publications.UpdateAsync(publication, cancellationToken).ConfigureAwait(false);
            superseded++;
        }

        return superseded;
    }
}
