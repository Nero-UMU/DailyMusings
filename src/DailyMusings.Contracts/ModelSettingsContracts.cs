namespace DailyMusings.Contracts;

/// <summary>
/// The instance's model and mail configuration (docs/开发指导.md §8.1, §12, §10.4).
/// <para>
/// Two shapes on purpose. An <see cref="ModelEndpointDto"/> is what an administrator edits — including the Base URL
/// and the secret's name — and a <see cref="ModelNameDto"/> is everything a client is allowed to know: §8.1 says the
/// client may see the model name and the health status, and nothing about an instance's internal endpoints or where
/// its credentials are filed belongs on a phone.
/// </para>
/// </summary>
public sealed record ModelEndpointDto(
    string Service,
    bool Enabled,
    string BaseUrl,
    string Model,

    /// <summary>The secret's <em>name</em>. The value never leaves the secret store (§10.4).</summary>
    string SecretName,
    int TimeoutSeconds,
    int? Dimensions,

    /// <summary>One of <c>SecretSourceNames</c>: where this endpoint's key comes from.</summary>
    string PasswordSource,

    /// <summary>Whether a key resolves at all. The value itself never leaves the instance.</summary>
    bool HasPassword);

public sealed record ModelEndpointListResponse(IReadOnlyList<ModelEndpointDto> Items);

public sealed record UpdateModelEndpointRequest(
    bool? Enabled,
    string? BaseUrl,
    string? Model,
    string? SecretName,
    int? TimeoutSeconds,
    int? Dimensions,

    /// <summary>Write-only. Stored encrypted outside the backup set, exactly like the SMTP password.</summary>
    string? Password = null,

    /// <summary>Removes the stored key, falling back to the secret file or the environment.</summary>
    bool? ClearPassword = null);

/// <summary>What a client may see about the instance's models (§8.1): a name, and whether it is switched on.</summary>
public sealed record ModelNameDto(string Service, string Model, bool Enabled);

public sealed record ModelNameListResponse(IReadOnlyList<ModelNameDto> Items);

/// <summary>
/// Where the SMTP password the instance would actually send with comes from. Reported rather than inferred, so
/// the admin page can say "还差密码" instead of leaving the operator to guess why nothing is delivered.
/// </summary>
public static class SecretSourceNames
{
    /// <summary>Typed into the admin page and stored encrypted outside the backup set.</summary>
    public const string Ui = "ui";

    /// <summary>A file under the secrets directory (Docker secrets, §10.4).</summary>
    public const string SecretFile = "secret-file";

    /// <summary>An environment variable.</summary>
    public const string Environment = "environment";

    /// <summary>Nothing provisioned: the password is missing.</summary>
    public const string None = "none";
}

public sealed record SmtpSettingsDto(
    bool Enabled,
    string Host,
    int Port,

    /// <summary>
    /// <c>none</c>, <c>starttls</c> (587) or <c>ssl</c> (465) — the three spellings
    /// <c>SmtpSecurityNames</c> accepts, so a configuration copied from another program can be typed in as-is.
    /// </summary>
    string Security,
    string? Username,

    /// <summary>The password's <em>name</em>, never its value (§10.4).</summary>
    string SecretName,
    string FromAddress,
    string FromName,
    int TimeoutSeconds,

    /// <summary>Where notification mail is sent. Kept beside the server settings so "mail" is one form, not two.</summary>
    string ToAddress,

    /// <summary>One of <c>SecretSourceNames</c>.</summary>
    string PasswordSource,

    /// <summary>Whether a password resolves at all. The value itself never leaves the instance.</summary>
    bool HasPassword);

public sealed record UpdateSmtpSettingsRequest(
    bool? Enabled,
    string? Host,
    int? Port,
    string? Security,
    string? Username,
    string? SecretName,
    string? FromAddress,
    string? FromName,
    int? TimeoutSeconds,

    /// <summary>Write-only. The value is encrypted at rest and never read back (§10.4).</summary>
    string? Password = null,

    /// <summary>Removes the stored password, falling back to the secret file or the environment.</summary>
    bool? ClearPassword = null,

    /// <summary>Notification recipient. Stored as <c>notification.to</c>, the key the notifier already reads.</summary>
    string? ToAddress = null);

/// <summary>
/// A real test message (docs/开发指导.md §16's "测试连接", upgraded): §12's whole point is that the user is
/// told when something happened, and a greeting that never authenticates does not prove mail can be sent.
/// </summary>
public sealed record SmtpTestRequest(string? ToAddress);

/// <summary>
/// <c>Code</c> is a stable identifier (never the relay's own text, which can echo the message). A failure is
/// reported rather than thrown: the operator pressed a button and needs a sentence, not a 500.
/// </summary>
public sealed record SmtpTestResponse(bool Sent, string? Code, string? Detail);
