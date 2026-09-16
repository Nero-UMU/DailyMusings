using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using DailyMusings.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Notifications;

/// <summary>
/// Reads the SMTP and notification configuration (docs/开发指导.md §12).
/// <para>
/// Generic SMTP only, exactly as the guide asks: no provider-specific integration, no OAuth flow, and the password
/// referenced by name so it never reaches the settings table, an export or a backup (§10.4).
/// </para>
/// </summary>
public sealed class ConfigurationSmtpSettingsProvider : ISmtpSettingsProvider
{
    public const string SectionName = "Smtp";

    private readonly SmtpSettings _settings;

    public ConfigurationSmtpSettingsProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var defaults = SmtpSettings.Default;

        _settings = new SmtpSettings(
            Enabled: section.GetValue("Enabled", defaults.Enabled),
            Host: section.GetValue("Host", defaults.Host) ?? defaults.Host,
            Port: section.GetValue("Port", defaults.Port),
            UseStartTls: section.GetValue("UseStartTls", defaults.UseStartTls),
            Username: section.GetValue<string?>("Username"),
            SecretName: section.GetValue("SecretName", defaults.SecretName) ?? defaults.SecretName,
            FromAddress: section.GetValue("FromAddress", defaults.FromAddress) ?? defaults.FromAddress,
            FromName: section.GetValue("FromName", defaults.FromName) ?? defaults.FromName,
            Timeout: TimeSpan.FromSeconds(section.GetValue("TimeoutSeconds", (int)defaults.Timeout.TotalSeconds)));
    }

    public Task<SmtpSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_settings);
}

/// <summary>
/// Which events are mailed, and where the instance lives (§12).
/// <para>
/// Read from the settings table as well as configuration, because these are the switches an operator flips while
/// looking at the instance rather than at a compose file. Configuration wins when it is present, so a deployment
/// that wants to pin an event on can do so.
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
            ToAddress: section.GetValue<string?>("To") ?? Read(stored, NotificationSettingKeys.ToAddress) ?? string.Empty,
            InstanceUrl: section.GetValue<string?>("InstanceUrl") ?? Read(stored, NotificationSettingKeys.InstanceUrl))
        {
            Events = events,
        };

        bool ReadSwitch(string settingKey, string configurationKey)
        {
            var configured = section.GetValue<bool?>(configurationKey);
            if (configured is not null)
            {
                return configured.Value;
            }

            return bool.TryParse(Read(stored, settingKey), out var parsed) && parsed;
        }
    }

    private static string? Read(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

/// <summary>
/// Sends mail over plain SMTP (docs/开发指导.md §12).
/// <para>
/// <see cref="SmtpClient"/> is the framework's own client and is used deliberately: it covers STARTTLS and
/// username/password authentication, which is everything a generic SMTP setting needs, and §20 asks this project
/// not to add machinery before it is needed. A personal instance talking to a relay does not need the features a
/// third-party library would add.
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

        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.UseStartTls,
            Timeout = (int)settings.Timeout.TotalMilliseconds,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            var password = _secrets.TryGet(settings.SecretName)
                ?? throw new PermanentExternalFailureException(
                    "notification.secret_missing",
                    $"The secret '{settings.SecretName}' is not provisioned.");

            client.UseDefaultCredentials = false;
            client.Credentials = new NetworkCredential(settings.Username, password);
        }

        using var mail = new MailMessage
        {
            From = new MailAddress(settings.FromAddress, settings.FromName, Encoding.UTF8),
            Subject = message.Subject,
            SubjectEncoding = Encoding.UTF8,
            Body = message.Body,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };

        mail.To.Add(message.To);

        try
        {
            await client.SendMailAsync(mail, cancellationToken).ConfigureAwait(false);
        }
        catch (SmtpException exception)
        {
            // The status code only — never the message body, which names the user's draft (§16).
            _logger.LogWarning(
                "SMTP server answered {StatusCode}.",
                exception.StatusCode);

            // A refused credential or a malformed address will be refused again; a connection problem will not.
            throw exception.StatusCode is SmtpStatusCode.ClientNotPermitted
                    or SmtpStatusCode.MustIssueStartTlsFirst
                ? new PermanentExternalFailureException("notification.rejected", "The SMTP server rejected the message.")
                : new TransientExternalFailureException("notification.smtp_failed", "The SMTP server could not be reached.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new PermanentExternalFailureException(
                "notification.invalid_message",
                $"The notification could not be addressed: {exception.Message}");
        }

        _logger.LogInformation("Sent a notification.");
    }
}
