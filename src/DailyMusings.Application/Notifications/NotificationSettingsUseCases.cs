using DailyMusings.Application.Abstractions;
using DailyMusings.Domain.Notifications;

namespace DailyMusings.Application.Notifications;

/// <summary>
/// The notification preferences as the admin page and the API read them (docs/开发指导.md §12).
/// <para>
/// Extracted from the endpoint so the Blazor page and the API answer identically: this instance has one recipient
/// address, three event switches and one content switch, and two places writing them from two copies of the logic
/// is how they drift.
/// </para>
/// </summary>
public sealed record NotificationSettingsView(
    bool SmtpConfigured,
    string ToAddress,
    string? InstanceUrl,
    bool DraftReady,
    bool JobFailed,
    bool AutomaticPublication,
    bool IncludeContent);

/// <summary>Reads the preferences, with the SMTP state the client needs to explain whether mail can be sent at all.</summary>
public sealed class ReadNotificationSettingsUseCase
{
    private readonly ISmtpSettingsProvider _smtp;
    private readonly INotificationSettingsProvider _notifications;

    public ReadNotificationSettingsUseCase(ISmtpSettingsProvider smtp, INotificationSettingsProvider notifications)
    {
        _smtp = smtp;
        _notifications = notifications;
    }

    public async Task<NotificationSettingsView> ExecuteAsync(CancellationToken cancellationToken)
    {
        var smtp = await _smtp.GetAsync(cancellationToken).ConfigureAwait(false);
        var settings = await _notifications.GetAsync(cancellationToken).ConfigureAwait(false);

        return new NotificationSettingsView(
            SmtpConfigured: smtp.Enabled,
            ToAddress: settings.ToAddress,
            InstanceUrl: settings.InstanceUrl,
            DraftReady: settings.IsEnabled(NotificationEvent.DraftReady),
            JobFailed: settings.IsEnabled(NotificationEvent.JobFailed),
            AutomaticPublication: settings.IsEnabled(NotificationEvent.AutomaticPublication),
            IncludeContent: settings.IncludeContent);
    }
}

/// <summary>
/// Writes the preferences. Every field is optional: a client that changes one switch should not have to echo the
/// others back, and a field left out is a field left alone.
/// </summary>
public sealed class UpdateNotificationSettingsUseCase
{
    private readonly IAppSettingStore _settings;

    public UpdateNotificationSettingsUseCase(IAppSettingStore settings) => _settings = settings;

    public async Task ExecuteAsync(
        string? toAddress,
        string? instanceUrl,
        bool? draftReady,
        bool? jobFailed,
        bool? automaticPublication,
        bool? includeContent,
        CancellationToken cancellationToken)
    {
        if (toAddress is not null)
        {
            await _settings
                .SetAsync(NotificationSettingKeys.ToAddress, toAddress.Trim(), cancellationToken)
                .ConfigureAwait(false);
        }

        if (instanceUrl is not null)
        {
            await _settings
                .SetAsync(NotificationSettingKeys.InstanceUrl, instanceUrl.Trim(), cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var (key, value) in new (string Key, bool? Value)[]
                 {
                     (NotificationSettingKeys.DraftReady, draftReady),
                     (NotificationSettingKeys.JobFailed, jobFailed),
                     (NotificationSettingKeys.AutomaticPublication, automaticPublication),
                     (NotificationSettingKeys.IncludeContent, includeContent),
                 })
        {
            if (value is not null)
            {
                await _settings
                    .SetAsync(key, value.Value ? "true" : "false", cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
