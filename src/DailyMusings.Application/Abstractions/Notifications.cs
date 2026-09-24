using DailyMusings.Domain.Notifications;

namespace DailyMusings.Application.Abstractions;

/// <summary>
/// SMTP configuration (docs/开发指导.md §12). Generic on purpose: the guide explicitly does not want a
/// per-provider integration, and every provider this product will meet speaks plain SMTP.
/// <para>
/// The fields are the seven an operator is asked for — host, port, the sender's mailbox, the password (resolved by
/// name, never stored here), the two encryption switches and the recipient — plus two that never reach the form.
/// The shape follows the mail forms of other self-hosted software on purpose: an operator copying their provider's
/// settings has "SMTP address, port, sender mailbox, password, SSL, STARTTLS, recipient mailbox" in front of them
/// and must be able to type exactly that in.
/// </para>
/// <para>
/// <see cref="FromAddress"/> is also the SMTP username. That is what those other forms mean by "sender mailbox",
/// and it is what removes the second field an operator had to keep in step with the first.
/// </para>
/// <para>
/// The two switches are independent booleans rather than one "security" choice, because that is the pair the other
/// software shows and the pair operators actually compare:
/// <list type="bullet">
/// <item><see cref="UseSsl"/> — <em>implicit</em> TLS: connect, handshake, and only then read the greeting. Port 465.</item>
/// <item><see cref="UseStartTls"/> — <em>explicit</em> TLS: greet in the clear, then <c>STARTTLS</c> and start over. Port 587.</item>
/// <item>Both false — no encryption at all, which is only right for a relay on localhost.</item>
/// </list>
/// The two cannot both be true: a form that let that through would only fail later, inside the handshake, in a way
/// no operator can read. Saving such a pair is refused with <c>smtp.security.conflicting</c>.
/// </para>
/// </summary>
/// <param name="FromName">The display name on the message. Internal: the form does not ask, the deployment may override.</param>
/// <param name="Timeout">One deadline for the whole conversation, not per operation. Internal, same rule.</param>
public sealed record SmtpSettings(
    bool Enabled,
    string Host,
    int Port,
    string FromAddress,
    bool UseSsl,
    bool UseStartTls,
    string FromName,
    TimeSpan Timeout)
{
    /// <summary>Off until an operator configures a server: a fresh instance sends no mail at all.</summary>
    public static SmtpSettings Default { get; } = new(
        Enabled: false,
        Host: "127.0.0.1",
        Port: 1025,
        FromAddress: "dailymusings@localhost",
        UseSsl: false,
        UseStartTls: false,
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
