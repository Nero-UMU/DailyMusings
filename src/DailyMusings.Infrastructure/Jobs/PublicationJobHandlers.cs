using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Jobs;
using DailyMusings.Application.Notifications;
using DailyMusings.Application.Publishing;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Jobs;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Jobs;

/// <summary>
/// Performs one publication (docs/开发指导.md §11.1, §11.2).
/// <para>
/// Thin on purpose: what to do was decided by <c>PublicationPlanner</c> and is carried out by
/// <c>RunPublicationUseCase</c>, so this class only translates the job's identifiers and outcomes. The
/// publication's own status is kept in step with the job's, exactly as the transcription and generation handlers
/// do, so a crashed process never leaves a record that claims to be working when nothing is.
/// </para>
/// </summary>
public sealed class PublicationJobHandler : IJobHandler
{
    private readonly RunPublicationUseCase _run;
    private readonly ILogger<PublicationJobHandler> _logger;

    public PublicationJobHandler(RunPublicationUseCase run, ILogger<PublicationJobHandler> logger)
    {
        _run = run;
        _logger = logger;
    }

    public JobType JobType => JobType.Publication;

    public async Task<JobOutcome> ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!Guid.TryParse(job.TargetId, out var parsed))
        {
            throw new PermanentExternalFailureException(
                "publication.bad_target",
                "The job does not name a valid publication.");
        }

        var payload = PublicationPayload.FromJson(job.Payload);
        var result = await _run
            .ExecuteAsync(new PublicationId(parsed), payload, cancellationToken)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case PublicationRunOutcome.Uploaded:
                _logger.LogInformation("Uploaded a draft to a publish target.");
                return JobOutcome.Completed;

            case PublicationRunOutcome.Published:
                _logger.LogInformation("Published an article to a publish target.");
                return JobOutcome.Completed;

            case PublicationRunOutcome.Expired:
                // §11.1: the window elapsed, so nothing was published and nothing will be. Marked as completed
                // because the decision was carried out — what failed was the schedule, and the user has been told.
                _logger.LogWarning("A scheduled publication expired before it could run.");
                return JobOutcome.Completed;

            default:
                return JobOutcome.Skipped;
        }
    }
}

/// <summary>
/// Sends one already-composed notification (docs/开发指导.md §12).
/// <para>
/// Whatever happens here stays here. §12 requires that a mail failure cannot roll back an article, trigger a
/// regeneration or change a publication result — which is true precisely because the message was composed
/// elsewhere and this job owns nothing but the transport.
/// </para>
/// </summary>
public sealed class NotificationJobHandler : IJobHandler
{
    private readonly SendNotificationUseCase _send;
    private readonly ILogger<NotificationJobHandler> _logger;

    public NotificationJobHandler(SendNotificationUseCase send, ILogger<NotificationJobHandler> logger)
    {
        _send = send;
        _logger = logger;
    }

    public JobType JobType => JobType.Notification;

    public async Task<JobOutcome> ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var sent = await _send.ExecuteAsync(job.Payload, cancellationToken).ConfigureAwait(false);

        if (!sent)
        {
            // Nothing to send — an unreadable payload, or a recipient that was never configured. Retrying would
            // reproduce the same nothing, so it is success rather than a failure.
            _logger.LogWarning("A notification job carried no message to send.");
            return JobOutcome.Skipped;
        }

        return JobOutcome.Completed;
    }
}
