using System.Globalization;
using System.Text;
using System.Text.Json;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Jobs;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Jobs;
using DailyMusings.Domain.Notifications;
using DailyMusings.Domain.Publishing;
using DailyMusings.Domain.Time;

namespace DailyMusings.Application.Notifications;

/// <summary>
/// Everything a notification job carries: the message, already composed.
/// <para>
/// Composing at enqueue time rather than at send time is deliberate. The message describes what happened — a
/// draft became ready, a job failed, an unattended upload expired — and that is true at the moment the event
/// occurs. Reconstructing it minutes later from whatever the database says by then is how a notification ends up
/// describing a state the user never saw.
/// </para>
/// </summary>
public sealed record NotificationPayload(string To, string Subject, string Body)
{
    public string ToJson() => JsonSerializer.Serialize(this);

    public static NotificationPayload? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<NotificationPayload>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Writes the mail itself (docs/开发指导.md §12).
/// <para>
/// Every message names what happened and points at the instance, and none of them carries the user's writing.
/// That is not an oversight: mail travels over an unencrypted channel by default, and the draft is the most
/// private thing this product holds, so an automatic copy of it in an inbox is a worse default than a short note
/// asking the user to open the app.
/// </para>
/// </summary>
public static class NotificationComposer
{
    public static EmailMessage DraftReady(ContentDate contentDate, string? title, string recipient, string? instanceUrl)
    {
        var subject = string.Create(CultureInfo.InvariantCulture, $"[每日随想] {contentDate} 的草稿等你确认");

        var body = new StringBuilder();
        body.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{contentDate} 的随想草稿已经生成，还没有确认。"));
        body.AppendLine();

        if (!string.IsNullOrWhiteSpace(title))
        {
            body.AppendLine(string.Create(CultureInfo.InvariantCulture, $"标题：{title.Trim()}"));
            body.AppendLine();
        }

        AppendLink(body, instanceUrl);
        body.AppendLine("正文不在邮件里，请到实例中查看与核验。");

        return new EmailMessage(recipient, subject, body.ToString().TrimEnd());
    }

    public static EmailMessage JobFailed(
        JobType jobType,
        string targetId,
        string? errorCode,
        string recipient,
        string? instanceUrl)
    {
        var subject = "[每日随想] 有一个任务最终失败了";

        var body = new StringBuilder();
        body.AppendLine("下面这个任务已经重试到上限，不会再自动重试：");
        body.AppendLine();
        body.AppendLine(string.Create(CultureInfo.InvariantCulture, $"任务类型：{jobType}"));
        body.AppendLine(string.Create(CultureInfo.InvariantCulture, $"对象：{targetId}"));

        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            body.AppendLine(string.Create(CultureInfo.InvariantCulture, $"错误码：{errorCode}"));
        }

        body.AppendLine();
        AppendLink(body, instanceUrl);
        body.AppendLine("修好配置后可以在任务列表里手动重试。");

        return new EmailMessage(recipient, subject, body.ToString().TrimEnd());
    }

    public static EmailMessage Publication(
        PublicationStatus status,
        string targetName,
        string? remoteId,
        string? errorCode,
        string recipient,
        string? instanceUrl)
    {
        var subject = status switch
        {
            PublicationStatus.Published => string.Create(CultureInfo.InvariantCulture, $"[每日随想] 已自动公开到 {targetName}"),
            PublicationStatus.DraftUploaded => string.Create(CultureInfo.InvariantCulture, $"[每日随想] 已上传草稿到 {targetName}"),
            PublicationStatus.Expired => "[每日随想] 自动发布已超时，没有执行",
            PublicationStatus.Failed => string.Create(CultureInfo.InvariantCulture, $"[每日随想] 向 {targetName} 发布失败"),
            _ => string.Create(CultureInfo.InvariantCulture, $"[每日随想] {targetName} 的发布状态更新"),
        };

        var body = new StringBuilder();

        switch (status)
        {
            case PublicationStatus.Published:
                body.AppendLine(string.Create(CultureInfo.InvariantCulture, $"稿件已导出为公开 Markdown：{targetName}。"));
                break;

            case PublicationStatus.DraftUploaded:
                body.AppendLine(string.Create(CultureInfo.InvariantCulture, $"稿件已导出为草稿 Markdown：{targetName}（draft: true）。"));
                break;

            case PublicationStatus.Expired:
                // The one message whose whole point is that nothing happened, so it says so plainly and says what
                // to do about it. §14: 不得在长时间后静默公开.
                body.AppendLine("计划的发布时间已经超过执行窗口，系统没有发布任何内容，也不会稍后补发。");
                body.AppendLine();
                body.AppendLine("如果仍要发布，请在应用里重新确认后手动重试。");
                break;

            case PublicationStatus.Failed:
                body.AppendLine(string.Create(CultureInfo.InvariantCulture, $"向 {targetName} 发布失败，已不再自动重试。"));
                break;

            default:
                body.AppendLine(string.Create(CultureInfo.InvariantCulture, $"目标：{targetName}；状态：{status}。"));
                break;
        }

        if (!string.IsNullOrWhiteSpace(remoteId))
        {
            body.AppendLine();
            body.AppendLine(string.Create(CultureInfo.InvariantCulture, $"导出文件：{remoteId}"));
        }

        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            body.AppendLine(string.Create(CultureInfo.InvariantCulture, $"错误码：{errorCode}"));
        }

        body.AppendLine();
        AppendLink(body, instanceUrl);

        return new EmailMessage(recipient, subject, body.ToString().TrimEnd());
    }

    private static void AppendLink(StringBuilder body, string? instanceUrl)
    {
        if (!string.IsNullOrWhiteSpace(instanceUrl))
        {
            body.AppendLine(string.Create(CultureInfo.InvariantCulture, $"打开实例：{instanceUrl.TrimEnd('/')}"));
            body.AppendLine();
        }
    }
}

/// <summary>
/// Decides whether an event should be mailed, and if so puts one notification job on the queue
/// (docs/开发指导.md §12, §14).
/// <para>
/// Nothing here runs inline with the work that caused it, and that separation is the point: 邮件失败不能回滚文章、
/// 触发重复生成或改变发布结果. A generation that finishes stays finished whether or not the mail server exists.
/// </para>
/// </summary>
public sealed class QueueNotificationUseCase
{
    private readonly INotificationSettingsProvider _settings;
    private readonly ISmtpSettingsProvider _smtp;
    private readonly JobEnqueuer _jobs;

    public QueueNotificationUseCase(
        INotificationSettingsProvider settings,
        ISmtpSettingsProvider smtp,
        JobEnqueuer jobs)
    {
        _settings = settings;
        _smtp = smtp;
        _jobs = jobs;
    }

    public Task<bool> QueueDraftReadyAsync(
        ContentDate contentDate,
        Domain.Common.ReflectionVersionId version,
        string? title,
        CancellationToken cancellationToken) =>
        QueueAsync(
            NotificationEvent.DraftReady,
            NotificationKeys.ForDraftReady(contentDate, version),
            contentDate.ToString(),
            (settings, recipient) => NotificationComposer.DraftReady(contentDate, title, recipient, settings.InstanceUrl),
            cancellationToken);

    public Task<bool> QueueJobFailedAsync(
        JobId jobId,
        JobType jobType,
        string targetId,
        string? errorCode,
        CancellationToken cancellationToken) =>
        QueueAsync(
            NotificationEvent.JobFailed,
            NotificationKeys.ForFailedJob(jobId),
            jobId.ToString(),
            (settings, recipient) => NotificationComposer.JobFailed(jobType, targetId, errorCode, recipient, settings.InstanceUrl),
            cancellationToken);

    public Task<bool> QueuePublicationAsync(
        PublicationId publicationId,
        PublicationStatus status,
        string targetName,
        string? remoteId,
        string? errorCode,
        CancellationToken cancellationToken) =>
        QueueAsync(
            NotificationEvent.AutomaticPublication,
            NotificationKeys.ForPublication(publicationId),
            publicationId.ToString(),
            (settings, recipient) => NotificationComposer.Publication(
                status,
                targetName,
                remoteId,
                errorCode,
                recipient,
                settings.InstanceUrl),
            cancellationToken);

    private async Task<bool> QueueAsync(
        NotificationEvent notificationEvent,
        string idempotencyKey,
        string targetId,
        Func<NotificationSettings, string, EmailMessage> compose,
        CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var smtp = await _smtp.GetAsync(cancellationToken).ConfigureAwait(false);

        if (!smtp.Enabled || !settings.IsEnabled(notificationEvent) || string.IsNullOrWhiteSpace(settings.ToAddress))
        {
            // Silence is the default: a fresh instance, or one with an event switched off, sends nothing at all
            // rather than queueing work that could only fail.
            return false;
        }

        var message = compose(settings, settings.ToAddress.Trim());
        var payload = new NotificationPayload(message.To, message.Subject, message.Body);

        await _jobs.EnsureAsync(
            JobType.Notification,
            targetId,
            idempotencyKey,
            payload.ToJson(),
            requeueFailed: false,
            cancellationToken).ConfigureAwait(false);

        return true;
    }
}

/// <summary>
/// Sends one already-composed notification (docs/开发指导.md §12).
/// <para>
/// The only thing that can fail here is the mail server, and when it does, §14 is explicit that the damage stays
/// inside the notification: the article, the draft and the publication record are all untouched.
/// </para>
/// </summary>
public sealed class SendNotificationUseCase
{
    private readonly IEmailSender _sender;

    public SendNotificationUseCase(IEmailSender sender) => _sender = sender;

    public async Task<bool> ExecuteAsync(string? payloadJson, CancellationToken cancellationToken)
    {
        var payload = NotificationPayload.FromJson(payloadJson);

        if (payload is null || string.IsNullOrWhiteSpace(payload.To))
        {
            // A notification whose message cannot be read has nothing to send. Retrying would rebuild the same
            // unreadable payload, so it is reported as nothing-to-do rather than as a failure.
            return false;
        }

        await _sender
            .SendAsync(new EmailMessage(payload.To, payload.Subject, payload.Body), cancellationToken)
            .ConfigureAwait(false);

        return true;
    }
}
