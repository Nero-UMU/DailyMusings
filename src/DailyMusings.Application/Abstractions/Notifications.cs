using DailyMusings.Domain.Notifications;

namespace DailyMusings.Application.Abstractions;

/// <summary>
/// How the connection to the relay is protected (docs/开发指导.md §12).
/// <para>
/// Named after what the wire does rather than after a checkbox, because the two labels every mail form uses mean the
/// opposite of each other: "SSL: true" is <see cref="ImplicitTls"/> (a handshake from the very first byte, port 465),
/// while "STARTTLS: true" is <see cref="StartTls"/> (greet in the clear, then upgrade, port 587). An operator copying
/// a configuration from any other program needs both to be expressible, so both are.
/// </para>
/// </summary>
public enum SmtpSecurity
{
    /// <summary>No encryption at all. Only ever right for a relay on localhost (port 25 or a test sink on 1025).</summary>
    None = 0,

    /// <summary>Explicit TLS: connect in the clear, EHLO, then <c>STARTTLS</c>. What port 587 means.</summary>
    StartTls = 1,

    /// <summary>Implicit TLS: the connection <em>is</em> a TLS handshake, greeting included. What port 465 means.</summary>
    ImplicitTls = 2,
}

/// <summary>
/// SMTP configuration (docs/开发指导.md §12). Generic on purpose: the guide explicitly does not want a
/// per-provider integration, and every provider this product will meet speaks plain SMTP.
/// <para>
/// The password is referenced by <em>name</em> and resolved from the secret store at send time, so it never
/// reaches the settings table, an export or a backup (§10.4).
/// </para>
/// </summary>
public sealed record SmtpSettings(
    bool Enabled,
    string Host,
    int Port,
    SmtpSecurity Security,
    string? Username,
    string SecretName,
    string FromAddress,
    string FromName,
    TimeSpan Timeout)
{
    /// <summary>Off until an operator configures a server: a fresh instance sends no mail at all.</summary>
    public static SmtpSettings Default { get; } = new(
        Enabled: false,
        Host: "127.0.0.1",
        Port: 1025,
        Security: SmtpSecurity.None,
        Username: null,
        SecretName: "smtp-password",
        FromAddress: "dailymusings@localhost",
        FromName: "每日随想",
        Timeout: TimeSpan.FromSeconds(30));
}

public interface ISmtpSettingsProvider
{
    Task<SmtpSettings> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Which events are mailed, and where to point a reader at the instance (§12: 可独立启用的通知事件).
/// </summary>
public sealed record NotificationSettings(
    string ToAddress,

    /// <summary>
    /// The instance's own address as the recipient would reach it. Used to link back to the draft; when unset the
    /// mail simply says what happened without a link, because a link nobody can open is worse than none.
    /// </summary>
    string? InstanceUrl)
{
    public static NotificationSettings Default { get; } = new(ToAddress: string.Empty, InstanceUrl: null);

    /// <summary>Per-event switches. Absent from the dictionary means "off".</summary>
    public IReadOnlyDictionary<NotificationEvent, bool> Events { get; init; } =
        new Dictionary<NotificationEvent, bool>();

    public bool IsEnabled(NotificationEvent notificationEvent) =>
        Events.TryGetValue(notificationEvent, out var enabled) && enabled;

    /// <summary>True when at least one event is enabled, so the notifier knows whether it has anything to do.</summary>
    public bool AnyEventEnabled => Events.Values.Any(enabled => enabled);
}

public interface INotificationSettingsProvider
{
    Task<NotificationSettings> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The stable keys the notification settings are stored under.
/// <para>
/// Declared next to the settings themselves rather than inside whichever provider happens to read them: these
/// strings end up in the settings table, and therefore in exports and backups, so both the reader and the writer
/// have to agree on them by construction.
/// </para>
/// </summary>
public static class NotificationSettingKeys
{
    public const string DraftReady = "notification.draftReady";
    public const string JobFailed = "notification.jobFailed";
    public const string AutomaticPublication = "notification.publication";
    public const string ToAddress = "notification.to";
    public const string InstanceUrl = "notification.instanceUrl";
}

/// <summary>A message ready to send. Plain text: the product's mail carries facts, not the user's writing.</summary>
public sealed record EmailMessage(string To, string Subject, string Body);

/// <summary>
/// The mail transport (docs/开发指导.md §12). Nothing else in the product may depend on it: 邮件失败不能回滚文章、
/// 触发重复生成或改变发布结果, which is only true if sending mail is the last thing that happens and its failure
/// belongs to the notification job alone.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
