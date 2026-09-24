using System.Globalization;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Configuration;
using DailyMusings.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Notifications;

/// <summary>
/// Reads the SMTP and notification configuration (docs/开发指导.md §12).
/// <para>
/// Generic SMTP only, exactly as the guide asks: no provider-specific integration, no OAuth flow, and the password
/// resolved from the secret store by name so it never reaches the settings table, an export or a backup (§10.4).
/// The two encryption switches are read as booleans; the single "security" spelling they replaced is still honoured
/// for existing deployments — see <see cref="StoredSettings.Ssl"/>.
/// </para>
/// </summary>
public sealed class ConfigurationSmtpSettingsProvider : ISmtpSettingsProvider
{
    public const string SectionName = "Smtp";

    /// <summary>The deployment configuration keys this section used to have, still read so an upgrade does not stop mail.</summary>
    private const string LegacySecurityKey = "Security";

    private const string LegacyUseStartTlsKey = "UseStartTls";

    private readonly IConfiguration _configuration;
    private readonly IAppSettingStore _settings;

    public ConfigurationSmtpSettingsProvider(IConfiguration configuration, IAppSettingStore settings)
    {
        _configuration = configuration;
        _settings = settings;
    }

    public async Task<SmtpSettings> GetAsync(CancellationToken cancellationToken)
    {
        // Settings table first (the admin page writes there), deployment configuration second, defaults last, and a
        // read per call so a saved change is used by the next mail rather than the next restart.
        var stored = await _settings.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var section = _configuration.GetSection(SectionName);
        var defaults = SmtpSettings.Default;

        // Read once: both readers need the old Smtp__Security token to map it onto their own boolean.
        var legacySecurity = section.GetValue<string?>(LegacySecurityKey);

        return new SmtpSettings(
            Enabled: StoredSettings.Boolean(stored, SmtpSettingKeys.Enabled, section.GetValue("Enabled", defaults.Enabled)),
            Host: StoredSettings.String(stored, SmtpSettingKeys.Host, section.GetValue("Host", defaults.Host)) ?? defaults.Host,
            Port: StoredSettings.Integer(stored, SmtpSettingKeys.Port, section.GetValue("Port", defaults.Port)),

            // §12: the sender's mailbox is the SMTP username, which is why the old Smtp:Username key is not read at
            // all any more. An instance that had one stored still sends, because the address was already stored too.
            FromAddress: StoredSettings.String(stored, SmtpSettingKeys.FromAddress, section.GetValue("FromAddress", defaults.FromAddress))
                ?? defaults.FromAddress,

            UseSsl: StoredSettings.Ssl(
                stored,
                SmtpSettingKeys.Ssl,
                StoredSettings.ConfiguredBoolean(section.GetValue<string?>("Ssl")),
                legacySecurity,
                defaults.UseSsl),

            UseStartTls: StoredSettings.StartTls(
                stored,
                SmtpSettingKeys.StartTls,
                StoredSettings.ConfiguredBoolean(section.GetValue<string?>("StartTls")),
                StoredSettings.ConfiguredBoolean(section.GetValue<string?>(LegacyUseStartTlsKey)),
                legacySecurity,
                defaults.UseStartTls),

            // The display name is deliberately NOT read from the settings table. The form no longer asks for one
            // (the field set is the seven the operator sees), so nothing can write it any more — and a value left
            // there from an earlier version is exactly what produced `From: ???? <…>` on a real message. The
            // deployment may still override it, and anything unreadable falls back to the default.
            FromName: SmtpSettings.UsableFromName(section.GetValue<string?>("FromName")),
            Timeout: TimeSpan.FromSeconds(StoredSettings.Integer(
                stored,
                SmtpSettingKeys.TimeoutSeconds,
                section.GetValue("TimeoutSeconds", (int)defaults.Timeout.TotalSeconds))));
    }
}

/// <summary>
/// Which events are mailed, and where the instance lives (§12).
/// <para>
/// Read from the settings table first, then the deployment configuration, then the defaults — the same order the
/// model endpoints and SMTP use. It used to be the other way round, which meant a deployment that set
/// <c>Notification:To</c> or pinned an event switch silently overrode whatever the administrator saved in the
/// admin page: they would turn a notification off and keep receiving it, with nothing to indicate why.
/// </para>
/// </summary>
public sealed class ConfigurationNotificationSettingsProvider : INotificationSettingsProvider
{
    public const string SectionName = "Notification";

    private readonly IConfiguration _configuration;
    private readonly IAppSettingStore _settings;

    public ConfigurationNotificationSettingsProvider(IConfiguration configuration, IAppSettingStore settings)
    {
        _configuration = configuration;
        _settings = settings;
    }

    public async Task<NotificationSettings> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await _settings.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var section = _configuration.GetSection(SectionName);

        var events = new Dictionary<Domain.Notifications.NotificationEvent, bool>
        {
            [Domain.Notifications.NotificationEvent.DraftReady] =
                ReadSwitch(NotificationSettingKeys.DraftReady, "DraftReady"),
            [Domain.Notifications.NotificationEvent.JobFailed] =
                ReadSwitch(NotificationSettingKeys.JobFailed, "JobFailed"),
            [Domain.Notifications.NotificationEvent.AutomaticPublication] =
                ReadSwitch(NotificationSettingKeys.AutomaticPublication, "AutomaticPublication"),
        };

        return new NotificationSettings(
            ToAddress: Read(stored, NotificationSettingKeys.ToAddress)
                ?? section.GetValue<string?>("To")
                ?? string.Empty,
            InstanceUrl: Read(stored, NotificationSettingKeys.InstanceUrl)
                ?? section.GetValue<string?>("InstanceUrl"))
        {
            Events = events,
        };

        bool ReadSwitch(string settingKey, string configurationKey) =>
            bool.TryParse(Read(stored, settingKey), out var fromSettings)
                ? fromSettings
                : section.GetValue<bool?>(configurationKey) ?? false;
    }

    private static string? Read(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

/// <summary>
/// Sends mail over plain SMTP (docs/开发指导.md §12).
/// <para>
/// The protocol lives in <see cref="SmtpMailTransport"/>; this type is the part that knows where the settings and the
/// password come from (§10.4) and turns "no relay configured" into a failure the job can act on. It replaced the
/// framework's <c>SmtpClient</c>, which cannot do the implicit TLS that port 465 needs — see the transport for why.
/// </para>
/// <para>
/// The message never contains the user's writing: see <c>NotificationComposer</c>. Failures are classified like
/// every other external call, so a refused relay backs off and gives up rather than hammering.
/// </para>
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly ISmtpSettingsProvider _settings;
    private readonly ISecretStore _secrets;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(
        ISmtpSettingsProvider settings,
        ISecretStore secrets,
        ILogger<SmtpEmailSender> logger)
    {
        _settings = settings;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        if (!settings.Enabled)
        {
            throw new PermanentExternalFailureException(
                "notification.smtp_disabled",
                "No SMTP server is configured.");
        }

        // The secret is resolved here, at send time, and handed to the transport — it never reaches the settings
        // table, an export or a backup (§10.4). A password that is not provisioned is not an error any more: a relay
        // on localhost authenticates nobody, and the transport simply does not offer credentials. The username is the
        // sender's mailbox, so there is no second field to fall out of step with it.
        var password = _secrets.TryGet(SmtpSettingKeys.PasswordSecretName);

        var transport = new SmtpMailTransport(settings, password);

        // The transport reports its own failures, already classified as permanent or transient (§14), and the
        // addresses it was given are refused before anything reaches the wire if they are malformed.
        await transport.SendAsync(message, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Sent a notification through {Host}:{Port} using {Security}.",
            settings.Host,
            settings.Port,
            TransportName(settings));
    }

    /// <summary>What the wire did, in one word, for a log line: never the password, never the message (§16).</summary>
    private static string TransportName(SmtpSettings settings) =>
        settings.UseSsl ? "ssl (implicit, 465)"
        : settings.UseStartTls ? "starttls (explicit, 587)"
        : "no encryption";
}
