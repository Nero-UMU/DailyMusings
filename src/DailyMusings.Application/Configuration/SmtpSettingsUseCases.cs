using System.Globalization;
using DailyMusings.Application.Abstractions;

namespace DailyMusings.Application.Configuration;

/// <summary>
/// The SMTP server as the admin page shows it (docs/开发指导.md §12, §8.1).
/// <para>
/// Same rule as the model endpoints: the password's <em>value</em> is never returned. What changed with this
/// feature is that a password can be <em>written</em> from here — so the form additionally has to say where
/// the effective password would come from and whether there is one at all, because "saved and switched on" and
/// "will actually send" stopped being the same statement. It still never leaves the instance: the encrypted
/// store lives outside the instance root, so no export or backup can carry it (§10.4).
/// </para>
/// <para>
/// The fields are the ones the form has, in the form's order: host, port, sender, the two switches, recipient.
/// <see cref="TimeoutSeconds"/> and <see cref="FromName"/> are reported so the page can show what the instance
/// would actually use, but nothing here writes them — they come from the deployment configuration or the built-in
/// defaults.
/// </para>
/// </summary>
public sealed record SmtpSettingsView(
    bool Enabled,
    string Host,
    int Port,
    string FromAddress,
    bool UseSsl,
    bool UseStartTls,
    string ToAddress,
    bool HasPassword,
    SecretSource PasswordSource,
    int TimeoutSeconds,
    string FromName);

/// <summary>
/// One save from the mail form. Every field is optional and <c>null</c> means "leave what is stored alone" — the
/// page sends the whole form, but a second caller (a script, a future client) should not have to restate a field
/// to change another.
/// </summary>
public sealed record SmtpSettingsUpdate(
    bool? Enabled,
    string? Host,
    int? Port,

    /// <summary>The sender's mailbox, which is also the SMTP username.</summary>
    string? FromAddress,

    /// <summary>Implicit TLS: handshake first, then the greeting. Port 465.</summary>
    bool? UseSsl,

    /// <summary>Explicit TLS: greet in the clear, then <c>STARTTLS</c>. Port 587.</summary>
    bool? UseStartTls,

    /// <summary>Write-only. Non-empty means "encrypt this and use it from now on".</summary>
    string? Password = null,

    /// <summary>
    /// Turns the password off. Written as an explicit empty record rather than a deletion, so that it also
    /// overrides a password that exists as a secret file — otherwise an operator could not act on the
    /// 「请清空密码」 message this same rule produces.
    /// </summary>
    bool? ClearPassword = null,

    /// <summary>Notification recipient, stored as <c>notification.to</c> (§12).</summary>
    string? ToAddress = null);

public static class SmtpSettingKeys
{
    public const string Enabled = "smtp.enabled";
    public const string Host = "smtp.host";
    public const string Port = "smtp.port";

    /// <summary>Implicit TLS (465) as a boolean. The old <c>smtp.security</c> token is still read; see StoredSettings.</summary>
    public const string Ssl = "smtp.ssl";

    /// <summary>Explicit TLS (587) as a boolean. The old <c>smtp.useStartTls</c> flag is still read; see StoredSettings.</summary>
    public const string StartTls = "smtp.starttls";

    public const string FromAddress = "smtp.fromAddress";

    /// <summary>Internal: not on the form, may be set from the deployment configuration.</summary>
    // The old form's fields. They are gone from the settings this product writes, and `0008_smtp_settings_cleanup`
    // deletes whatever an older version left behind — a stored name that nothing can edit any more is how
    // `From: ???? <…>` reached a real mailbox.

    /// <summary>Internal: not on the form, may be set from the deployment configuration.</summary>
    public const string TimeoutSeconds = "smtp.timeoutSeconds";

    /// <summary>
    /// The one name the SMTP password is filed under, in the encrypted UI store, in the secrets directory and in
    /// the environment. It used to be configurable, which bought nothing and cost an operator a field to keep in
    /// step with the file they had mounted.
    /// </summary>
    public const string PasswordSecretName = "smtp-password";
}

public sealed class ReadSmtpSettingsUseCase
{
    private readonly ISmtpSettingsProvider _smtp;
    private readonly ISecretStore _secrets;
    private readonly INotificationSettingsProvider _notifications;

    public ReadSmtpSettingsUseCase(
        ISmtpSettingsProvider smtp,
        ISecretStore secrets,
        INotificationSettingsProvider notifications)
    {
        _smtp = smtp;
        _secrets = secrets;
        _notifications = notifications;
    }

    public async Task<SmtpSettingsView> ExecuteAsync(CancellationToken cancellationToken)
    {
        var settings = await _smtp.GetAsync(cancellationToken).ConfigureAwait(false);

        // The recipient lives with the notification preferences, so it is read from there rather than duplicated
        // into the SMTP settings: two copies of "where does mail go" is one copy too many.
        var notifications = await _notifications.GetAsync(cancellationToken).ConfigureAwait(false);

        var source = _secrets.ResolveSource(SmtpSettingKeys.PasswordSecretName);

        return new SmtpSettingsView(
            settings.Enabled,
            settings.Host,
            settings.Port,
            settings.FromAddress,
            settings.UseSsl,
            settings.UseStartTls,
            notifications.ToAddress,
            source != SecretSource.None,
            source,
            (int)settings.Timeout.TotalSeconds,
            settings.FromName);
    }
}

/// <summary>
/// Writes the SMTP settings. A server that cannot be addressed is refused here rather than at three in the morning,
/// when the first notification tries to leave.
/// <para>
/// Two refusals exist because the form can now express two configurations that would only fail later, and in a way
/// no operator can read: both encryption switches ticked (there is no such transport), and a password on a
/// connection with neither switch (the password would travel in the clear). Both are refused at save time with a
/// stable code, which is where an operator can still do something about them.
/// </para>
/// </summary>
public sealed class UpdateSmtpSettingsUseCase
{
    private readonly IAppSettingStore _settings;
    private readonly IUiSecretStore _uiSecrets;
    private readonly ISecretStore _secrets;

    public UpdateSmtpSettingsUseCase(IAppSettingStore settings, IUiSecretStore uiSecrets, ISecretStore secrets)
    {
        _settings = settings;
        _uiSecrets = uiSecrets;
        _secrets = secrets;
    }

    public async Task ExecuteAsync(SmtpSettingsUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        // Validated before anything is written: a save that is going to be refused must not leave half of itself
        // in the settings table.
        var host = update.Host is null ? null : ValidateHost(update.Host);
        int? port = update.Port is { } submittedPort ? ValidatePort(submittedPort) : null;
        var fromAddress = update.FromAddress is null ? null : ValidateAddress(update.FromAddress, "smtp.from_address.invalid", "发件人邮箱");
        var toAddress = update.ToAddress is null ? null : ValidateRecipient(update.ToAddress);

        // The switches are read as a pair, because they mean something together and because "leave it alone" has to
        // be resolved against what is stored before either can be checked.
        var useSsl = await EffectiveAsync(update.UseSsl, SmtpSettingKeys.Ssl, cancellationToken).ConfigureAwait(false);
        var useStartTls = await EffectiveAsync(update.UseStartTls, SmtpSettingKeys.StartTls, cancellationToken).ConfigureAwait(false);

        if (useSsl && useStartTls)
        {
            // Deliberately refused rather than stored: the other program's form allows both boxes, and the result is
            // a send that dies inside the handshake with a message about protos and records.
            throw new UseCaseException(
                "smtp.security.conflicting",
                "SSL 与 STARTTLS 只能选一个：SSL 是连上就握手（465），STARTTLS 是先打招呼再升级（587）。");
        }

        // "Will there be a password after this save?" — either one was just typed, or one already resolves and this
        // save is not clearing it. A missing password is a legal configuration (a relay on localhost), which is why
        // the check is about a password that exists rather than about encryption being on.
        var passwordInUse =
            !string.IsNullOrEmpty(update.Password) ||
            (update.ClearPassword != true && _secrets.ResolveSource(SmtpSettingKeys.PasswordSecretName) != SecretSource.None);

        if (passwordInUse && !useSsl && !useStartTls)
        {
            throw new UseCaseException(
                "smtp.security.credentials_in_clear",
                "填了密码却不加密会把密码明文发出去，所以不能保存：请勾选 SSL（465）或 STARTTLS（587），或者清空密码。");
        }

        if (host is not null)
        {
            await SetAsync(SmtpSettingKeys.Host, host, cancellationToken).ConfigureAwait(false);
        }

        if (port is not null)
        {
            await SetAsync(SmtpSettingKeys.Port, port.Value.ToString(CultureInfo.InvariantCulture), cancellationToken)
                .ConfigureAwait(false);
        }

        if (fromAddress is not null)
        {
            await SetAsync(SmtpSettingKeys.FromAddress, fromAddress, cancellationToken).ConfigureAwait(false);
        }

        if (update.UseSsl is not null || update.UseStartTls is not null)
        {
            // Both keys are written even when only one box was touched: they are one decision with two halves, and a
            // half-written pair is how a stored "leave it alone" turns into a stored "no".
            await SetAsync(SmtpSettingKeys.Ssl, Bool(useSsl), cancellationToken).ConfigureAwait(false);
            await SetAsync(SmtpSettingKeys.StartTls, Bool(useStartTls), cancellationToken).ConfigureAwait(false);
        }

        if (update.Enabled is { } enabled)
        {
            await SetAsync(SmtpSettingKeys.Enabled, Bool(enabled), cancellationToken).ConfigureAwait(false);
        }

        // §12: the recipient is part of the mail configuration as far as the operator is concerned, so it is
        // accepted here — but it is stored under the key the notifier already reads, so there is still exactly
        // one place that decides where a message goes. A blank value clears it.
        if (toAddress is not null)
        {
            await SetAsync(NotificationSettingKeys.ToAddress, toAddress, cancellationToken).ConfigureAwait(false);
        }

        // The password itself. This is the one write in the product that puts a credential in a place the admin
        // page can reach, so it is stored through the encrypted store (outside the instance root, and therefore
        // outside every export and backup) rather than in the settings table.
        //
        // Clearing writes an explicit empty record rather than deleting the record: the page has to be able to say
        // "the operator cleared this" as opposed to "nothing was ever stored here", and the refusal message that
        // demands a password must have a way out. Since appendix A.27 there is no file underneath to fall back to,
        // but the distinction is still what the view reports (SecretSource.None on both counts, an empty record on
        // the cleared one), and "有密码就必须加密" is decided by whether a value exists.
        if (update.ClearPassword == true)
        {
            await _uiSecrets.SetAsync(SmtpSettingKeys.PasswordSecretName, string.Empty, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(update.Password))
        {
            await _uiSecrets.SetAsync(SmtpSettingKeys.PasswordSecretName, update.Password, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>The value a switch will have after this save: what was submitted, else what is stored, else false.</summary>
    private async Task<bool> EffectiveAsync(bool? submitted, string key, CancellationToken cancellationToken)
    {
        if (submitted is { } value)
        {
            return value;
        }

        var stored = await _settings.GetAsync(key, cancellationToken).ConfigureAwait(false);

        return bool.TryParse(stored, out var parsed) && parsed;
    }

    private Task SetAsync(string key, string value, CancellationToken cancellationToken) =>
        _settings.SetAsync(key, value, cancellationToken);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string ValidateHost(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length is 0 or > 255 || trimmed.Contains(' ', StringComparison.Ordinal))
        {
            throw new UseCaseException("smtp.host.invalid", "SMTP 地址不能为空，也不能包含空格。");
        }

        return trimmed;
    }

    private static int ValidatePort(int port) =>
        port is < 1 or > 65535
            ? throw new UseCaseException("smtp.port.out_of_range", "端口需要介于 1 与 65535 之间。")
            : port;

    /// <summary>A mailbox that has to be there.</summary>
    private static string ValidateAddress(string value, string code, string what)
    {
        var trimmed = value.Trim();

        if (trimmed.Length == 0 || !IsMailbox(trimmed))
        {
            throw new UseCaseException(code, $"{what}需要是一个邮箱地址。");
        }

        return trimmed;
    }

    /// <summary>
    /// A mailbox that may be empty. Empty is stored as empty — it clears the recipient, and the page says what that
    /// means: nothing will be sent. Mail that could not be addressed is refused here rather than by the relay.
    /// </summary>
    private static string ValidateRecipient(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (!IsMailbox(trimmed))
        {
            throw new UseCaseException("smtp.to_address.invalid", "收件人邮箱需要是一个邮箱地址。");
        }

        return trimmed;
    }

    /// <summary>
    /// The same shallow check the transport makes before anything reaches the wire: an <c>@</c> and no spaces. It is
    /// deliberately not a full RFC 5322 parser — the relay is the authority on whether an address exists, and a
    /// stricter check here would refuse addresses that work.
    /// </summary>
    private static bool IsMailbox(string value) =>
        !value.Contains(' ', StringComparison.Ordinal) && value.Contains('@', StringComparison.Ordinal);
}
