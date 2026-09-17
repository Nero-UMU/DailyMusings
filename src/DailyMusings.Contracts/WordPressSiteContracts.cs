namespace DailyMusings.Contracts;

/// <summary>
/// A WordPress target's site settings (docs/开发指导.md §8.1): both what the admin page has overridden and what the
/// instance will actually use, so the page can show the difference instead of making the operator guess which layer
/// won.
/// </summary>
public sealed record WordPressSiteDto(
    bool Overridden,
    string? OverrideBaseUrl,
    string? OverrideUsername,
    string? OverrideSecretName,
    int? OverrideTimeoutSeconds,
    string EffectiveBaseUrl,
    string EffectiveUsername,
    string EffectiveSecretName,
    int EffectiveTimeoutSeconds);

/// <summary>
/// Replaces a target's override. A blank field clears that field's override, so the deployment configuration takes
/// over again; a request with every field blank clears the override entirely.
/// </summary>
public sealed record UpdateWordPressSiteOverrideRequest(
    string? BaseUrl,
    string? Username,
    string? SecretName,
    int? TimeoutSeconds);
