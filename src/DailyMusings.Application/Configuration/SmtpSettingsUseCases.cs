using System.Globalization;
using DailyMusings.Application.Abstractions;

namespace DailyMusings.Application.Configuration;

/// <summary>
/// The SMTP server as the admin page shows it (docs/开发指导.md §12, §8.1).
/// <para>
/// Same rule as the model endpoints: only the password's <em>name</em> is stored, never its value (§10.4). The
/// password itself stays a file under the secrets directory, which is why this form is safe to serve over plain HTTP
/// on a LAN — the one thing it could leak is where mail is sent from.
/// </para>
/// </summary>
public sealed record SmtpSettingsView(
    bool Enabled,
    string Host,
    int Port,
    SmtpSecurity Security,
    string? Username,
    string SecretName,
    string FromAddress,
    string FromName,
    int TimeoutSeconds);

public sealed record SmtpSettingsUpdate(
    bool? Enabled,
    string? Host,
    int? Port,
    SmtpSecurity? Security,
    string? Username,
    string? SecretName,
    string? FromAddress,
    string? FromName,
    int? TimeoutSeconds);

public static class SmtpSettingKeys
{
    public const string Enabled = "smtp.enabled";
    public const string Host = "smtp.host";
    public const string Port = "smtp.port";

    /// <summary>One of <see cref="SmtpSecurityNames"/>: <c>none</c>, <c>starttls</c> or <c>ssl</c>.</summary>
    public const string Security = "smtp.security";

    /// <summary>
    /// The boolean this setting used to be, kept readable (and kept in step when written) so an instance that saved
    /// "use STARTTLS" before implicit TLS existed keeps sending, and a rollback to the previous image still works.
    /// </summary>
    public const string UseStartTls = "smtp.useStartTls";

    public const string Username = "smtp.username";
    public const string SecretName = "smtp.secretName";
    public const string FromAddress = "smtp.fromAddress";
    public const string FromName = "smtp.fromName";
    public const string TimeoutSeconds = "smtp.timeoutSeconds";
}

/// <summary>
/// The spelling of <see cref="SmtpSecurity"/> in the settings table, the deployment configuration and the API.
/// <para>
/// Short tokens rather than enum names because they are what an operator types into an environment variable
/// (<c>Smtp__Security=ssl</c>), and <c>ssl</c> rather than <c>implicit</c> because that is the word on every other
/// mail form — the point of this setting is that a configuration can be copied from one program to another.
/// </para>
/// </summary>
public static class SmtpSecurityNames
{
    public const string None = "none";
    public const string StartTls = "starttls";
    public const string Ssl = "ssl";

    /// <summary>The stored/configured spelling of a mode.</summary>
    public static string ToToken(SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => StartTls,
        SmtpSecurity.ImplicitTls => Ssl,
        _ => None,
    };

    /// <summary>
    /// Reads a mode. Beyond the three tokens it accepts the spellings other software's dump uses, because refusing
    /// "tls" or "implicit" would send an operator back to a form that cannot say what their old program said.
    /// </summary>
    public static bool TryParse(string? value, out SmtpSecurity security)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case None:
            case "off":
            case "false":
            case "plain":
                security = SmtpSecurity.None;
                return true;
            case StartTls:
            case "starttlsrequired":
            case "explicit":
                security = SmtpSecurity.StartTls;
                return true;
            case Ssl:
            case "tls":
            case "implicit":
            case "smtps":
            case "true":
                security = SmtpSecurity.ImplicitTls;
                return true;
            default:
                security = SmtpSecurity.None;
                return false;
        }
    }

    /// <summary>Parses or throws the validation failure the API and the admin page both report.</summary>
    public static SmtpSecurity Parse(string? value) =>
        TryParse(value, out var security)
            ? security
            : throw new UseCaseException(
                "smtp.security.invalid",
                "安全方式只能是 none（不加密）、starttls（587）或 ssl（465）。");
}

public sealed class ReadSmtpSettingsUseCase
{
    private readonly ISmtpSettingsProvider _smtp;

    public ReadSmtpSettingsUseCase(ISmtpSettingsProvider smtp) => _smtp = smtp;

    public async Task<SmtpSettingsView> ExecuteAsync(CancellationToken cancellationToken)
    {
        var settings = await _smtp.GetAsync(cancellationToken).ConfigureAwait(false);

        return new SmtpSettingsView(
            settings.Enabled,
            settings.Host,
            settings.Port,
            settings.Security,
            settings.Username,
            settings.SecretName,
            settings.FromAddress,
            settings.FromName,
            (int)settings.Timeout.TotalSeconds);
    }
}

/// <summary>
/// Writes the SMTP settings. A server that cannot be addressed is refused here rather than at three in the morning,
/// when the first notification tries to leave.
/// </summary>
public sealed class UpdateSmtpSettingsUseCase
{
    private const int MinimumTimeoutSeconds = 5;
    private const int MaximumTimeoutSeconds = 300;

    private readonly IAppSettingStore _settings;

    public UpdateSmtpSettingsUseCase(IAppSettingStore settings) => _settings = settings;

    public async Task ExecuteAsync(SmtpSettingsUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (update.Host is { } host)
        {
            await SetAsync(SmtpSettingKeys.Host, ValidateHost(host), cancellationToken).ConfigureAwait(false);
        }

        if (update.Port is { } port)
        {
            await SetAsync(SmtpSettingKeys.Port, ValidatePort(port).ToString(CultureInfo.InvariantCulture), cancellationToken)
                .ConfigureAwait(false);
        }

        if (update.SecretName is { } secretName)
        {
            await SetAsync(SmtpSettingKeys.SecretName, ValidateSecretName(secretName), cancellationToken).ConfigureAwait(false);
        }

        if (update.FromAddress is { } fromAddress)
        {
            await SetAsync(SmtpSettingKeys.FromAddress, ValidateAddress(fromAddress), cancellationToken).ConfigureAwait(false);
        }

        if (update.FromName is { } fromName)
        {
            await SetAsync(SmtpSettingKeys.FromName, fromName.Trim(), cancellationToken).ConfigureAwait(false);
        }

        if (update.TimeoutSeconds is { } timeout)
        {
            await SetAsync(
                SmtpSettingKeys.TimeoutSeconds,
                ValidateTimeout(timeout).ToString(CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
        }

        // A username is optional: a relay on localhost needs none, and clearing it has to be possible, so an empty
        // string is stored as "no username" rather than refused.
        if (update.Username is { } username)
        {
            await SetAsync(SmtpSettingKeys.Username, username.Trim(), cancellationToken).ConfigureAwait(false);
        }

        if (update.Security is { } security)
        {
            await SetAsync(SmtpSettingKeys.Security, SmtpSecurityNames.ToToken(security), cancellationToken).ConfigureAwait(false);

            // The old boolean is written alongside so that an instance rolled back to the previous image — which only
            // knows "use STARTTLS" — still reads a setting that matches what the administrator just chose.
            await SetAsync(
                SmtpSettingKeys.UseStartTls,
                security == SmtpSecurity.None ? "false" : "true",
                cancellationToken).ConfigureAwait(false);
        }

        if (update.Enabled is { } enabled)
        {
            await SetAsync(SmtpSettingKeys.Enabled, enabled ? "true" : "false", cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SetAsync(string key, string value, CancellationToken cancellationToken) =>
        _settings.SetAsync(key, value, cancellationToken);

    private static string ValidateHost(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length is 0 or > 255 || trimmed.Contains(' ', StringComparison.Ordinal))
        {
            throw new UseCaseException("smtp.host.invalid", "SMTP 主机名不能为空，也不能包含空格。");
        }

        return trimmed;
    }

    private static int ValidatePort(int port) =>
        port is < 1 or > 65535
            ? throw new UseCaseException("smtp.port.out_of_range", "端口需要介于 1 与 65535 之间。")
            : port;

    private static string ValidateSecretName(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length is 0 or > 100 ||
            trimmed.Contains('/', StringComparison.Ordinal) ||
            trimmed.Contains('\\', StringComparison.Ordinal) ||
            trimmed is "." or "..")
        {
            throw new UseCaseException("smtp.secret_name.invalid", "Secret 名只能是文件名（不含路径分隔符）。");
        }

        return trimmed;
    }

    private static string ValidateAddress(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length == 0 || trimmed.Contains(' ', StringComparison.Ordinal) || !trimmed.Contains('@', StringComparison.Ordinal))
        {
            throw new UseCaseException("smtp.from_address.invalid", "发件地址需要是一个邮箱地址。");
        }

        return trimmed;
    }

    private static int ValidateTimeout(int seconds) =>
        seconds is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds
            ? throw new UseCaseException(
                "smtp.timeout.out_of_range",
                $"超时需要介于 {MinimumTimeoutSeconds} 与 {MaximumTimeoutSeconds} 秒之间。")
            : seconds;
}
