using System.Globalization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Common;

namespace DailyMusings.Application.Notifications;

/// <summary>
/// The outcome of a test mail. <see cref="Code"/> is a stable identifier and <see cref="Detail"/> is a sentence
/// for the operator; neither ever carries the relay's raw reply, which can echo the message back (§16).
/// </summary>
public sealed record SmtpTestResult(bool Sent, string? Code, string? Detail);

/// <summary>
/// Sends one real message so the operator learns whether mail actually works (docs/开发指导.md §12, §16).
/// <para>
/// Deliberately not the same thing as the existing "test connection", which only reads the server's greeting.
/// That check passes against a relay that will reject every message for want of credentials, and §12's whole
/// promise is that the user is told when something happened — so the honest test is to send one.
/// </para>
/// <para>
/// It bypasses the per-event switches on purpose: those decide whether <em>this instance</em> mails about a
/// draft or a failure, and a test the operator asked for is neither. It does obey the master <c>Enabled</c>
/// switch, because a disabled SMTP configuration genuinely cannot send, and pretending otherwise would be the
/// one lie that matters here.
/// </para>
/// </summary>
public sealed class SendTestEmailUseCase
{
    private readonly INotificationSettingsProvider _notifications;
    private readonly IEmailSender _sender;
    private readonly IClock _clock;

    public SendTestEmailUseCase(
        INotificationSettingsProvider notifications,
        IEmailSender sender,
        IClock clock)
    {
        _notifications = notifications;
        _sender = sender;
        _clock = clock;
    }

    public async Task<SmtpTestResult> ExecuteAsync(string? toAddress, CancellationToken cancellationToken)
    {
        var settings = await _notifications.GetAsync(cancellationToken).ConfigureAwait(false);

        var recipient = string.IsNullOrWhiteSpace(toAddress) ? settings.ToAddress : toAddress.Trim();

        if (string.IsNullOrWhiteSpace(recipient))
        {
            // A refusal, not a failure: nothing was attempted, and the fix is a field on the form.
            return new SmtpTestResult(false, "smtp.test.no_recipient", "还没有填写收件地址。");
        }

        var now = _clock.UtcNow;

        var message = new EmailMessage(
            recipient,
            "[每日随想] 测试邮件",
            BuildBody(now, settings.InstanceUrl));

        try
        {
            await _sender.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (TransientExternalFailureException exception)
        {
            // The classified failures already say what is wrong in a way the operator can act on, so the code and
            // the message travel through unchanged rather than being flattened into "failed".
            return new SmtpTestResult(false, exception.Code, exception.Message);
        }
        catch (PermanentExternalFailureException exception)
        {
            return new SmtpTestResult(false, exception.Code, exception.Message);
        }
        catch (DomainException exception)
        {
            return new SmtpTestResult(false, exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Anything else is a defect. Only the exception's type is reported: the stack and the message can
            // name internal paths or the recipient's server, and the operator cannot act on either.
            return new SmtpTestResult(false, "smtp.test.failed", $"发送时出现未预期的错误（{exception.GetType().Name}）。");
        }

        return new SmtpTestResult(true, null, $"测试邮件已发送到 {recipient}。");
    }

    /// <summary>
    /// The message body. Facts only, exactly like every other mail this product sends: no draft text, no titles,
    /// nothing the user wrote (§16) — a test mail in particular must be safe to send to an address that is not
    /// the user's own.
    /// </summary>
    private static string BuildBody(DateTimeOffset nowUtc, string? instanceUrl)
    {
        var lines = new List<string>
        {
            "收到这封邮件说明 SMTP 配置可用。",
            string.Empty,
            string.Create(
                CultureInfo.InvariantCulture,
                $"发信时间：{nowUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}（{nowUtc:yyyy-MM-dd HH:mm:ss} UTC）"),
        };

        if (!string.IsNullOrWhiteSpace(instanceUrl))
        {
            lines.Add($"实例地址：{instanceUrl.Trim()}");
        }

        lines.Add(string.Empty);
        lines.Add("这封邮件由管理页的「发送测试邮件」触发，不含任何随想内容。");

        return string.Join(Environment.NewLine, lines);
    }
}
