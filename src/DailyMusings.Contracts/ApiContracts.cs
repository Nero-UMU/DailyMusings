namespace DailyMusings.Contracts;

/// <summary>
/// The wire contract between the server and its clients (docs/开发指导.md §5).
/// <para>
/// Deliberately dependency-free so the (future) MAUI clients can compile against the same types. Nothing in
/// this assembly may carry a server secret: §8.1 lets a client see a model <em>name</em> and a health status,
/// and nothing more.
/// </para>
/// </summary>
public static class ApiRoutes
{
    public const string AdminSignIn = "/api/admin/sign-in";
    public const string AdminSignOut = "/api/admin/sign-out";
    public const string AdminChangeCredentials = "/api/admin/credentials";

    public const string PairingCodes = "/api/pairing/codes";
    public const string PairingRedeem = "/api/pairing/redeem";

    public const string Devices = "/api/devices";
    public const string DeviceSelf = "/api/devices/me";

    public const string Health = "/api/system/health";
}

/// <summary>Stable, non-localized failure identifiers. Clients branch on these, never on messages.</summary>
public static class ApiErrorCodes
{
    public const string Unauthenticated = "auth.unauthenticated";
    public const string Forbidden = "auth.forbidden";
    public const string CredentialsRequired = "admin.credentials_change_required";
    public const string ValidationFailed = "request.invalid";
    public const string NotFound = "request.not_found";
    public const string Unexpected = "server.unexpected";
}

public sealed record ApiError(string Code, string Message);

public sealed record HealthProbeDto(string Name, bool Healthy, string? Detail);

public sealed record HealthResponse(bool Healthy, string CheckedAtUtc, IReadOnlyList<HealthProbeDto> Probes);

/// <summary>
/// Redeeming a pairing code. The code itself is the idempotency key (§13): it is single-use and short-lived,
/// so a retried request either succeeds once or fails as already-used — it can never create two devices.
/// </summary>
public sealed record RedeemPairingCodeRequest(string Code, string DeviceName, string? Platform);

public sealed record RedeemPairingCodeResponse(string DeviceId, string DeviceName, string Token);

public sealed record PairingCodeResponse(string Code, string ExpiresAtUtc);

public sealed record DeviceDto(
    string Id,
    string Name,
    string? Platform,
    string CreatedAtUtc,
    string? LastSeenAtUtc,
    bool Revoked);

public sealed record RotateDeviceTokenResponse(string DeviceId, string Token);

public sealed record AdminCredentialsRequest(string CurrentPassword, string NewUsername, string NewPassword);
