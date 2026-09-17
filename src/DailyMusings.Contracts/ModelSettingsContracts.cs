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
    int? Dimensions);

public sealed record ModelEndpointListResponse(IReadOnlyList<ModelEndpointDto> Items);

public sealed record UpdateModelEndpointRequest(
    bool? Enabled,
    string? BaseUrl,
    string? Model,
    string? SecretName,
    int? TimeoutSeconds,
    int? Dimensions);

/// <summary>What a client may see about the instance's models (§8.1): a name, and whether it is switched on.</summary>
public sealed record ModelNameDto(string Service, string Model, bool Enabled);

public sealed record ModelNameListResponse(IReadOnlyList<ModelNameDto> Items);

public sealed record SmtpSettingsDto(
    bool Enabled,
    string Host,
    int Port,
    bool UseStartTls,
    string? Username,

    /// <summary>The password's <em>name</em>, never its value (§10.4).</summary>
    string SecretName,
    string FromAddress,
    string FromName,
    int TimeoutSeconds);

public sealed record UpdateSmtpSettingsRequest(
    bool? Enabled,
    string? Host,
    int? Port,
    bool? UseStartTls,
    string? Username,
    string? SecretName,
    string? FromAddress,
    string? FromName,
    int? TimeoutSeconds);
