using System.Security.Claims;
using System.Text.Encodings.Web;
using DailyMusings.Application.Devices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DailyMusings.Server.Authentication;

/// <summary>
/// Authenticates a client device from its bearer token (docs/开发指导.md §10.2).
/// <para>
/// The token is hashed and looked up; the plaintext is never stored, compared in memory beyond this call, or
/// logged. A revoked device fails here, which is what makes revocation take effect on the very next request.
/// </para>
/// </summary>
public sealed class DeviceTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DeviceToken";

    private const string BearerPrefix = "Bearer ";

    private readonly AuthenticateDeviceUseCase _authenticateDevice;

    public DeviceTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        AuthenticateDeviceUseCase authenticateDevice)
        : base(options, loggerFactory, encoder)
    {
        _authenticateDevice = authenticateDevice;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var values))
        {
            return AuthenticateResult.NoResult();
        }

        var header = values.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var presented = header[BearerPrefix.Length..].Trim();
        if (presented.Length == 0)
        {
            return AuthenticateResult.NoResult();
        }

        var device = await _authenticateDevice
            .ExecuteAsync(presented, Context.RequestAborted)
            .ConfigureAwait(false);

        if (device is null)
        {
            // The failure message must not distinguish "unknown token" from "revoked device".
            return AuthenticateResult.Fail("The device token is not valid.");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, device.Id.ToString()),
            new Claim(ClaimTypes.Name, device.Name),
            new Claim(ClaimTypes.Role, ServerAuthenticationPolicies.DeviceRole),
        ], SchemeName);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

/// <summary>Scheme names and authorization policies, kept in one place so the UI and the API cannot drift.</summary>
public static class ServerAuthenticationPolicies
{
    public const string AdminCookie = "AdminCookie";
    public const string DeviceToken = DeviceTokenAuthenticationHandler.SchemeName;

    /// <summary>The administrator's auth cookie. Named here so tests can assert on session persistence.</summary>
    public const string AdminCookieName = "dailymusings.admin";

    public const string AdminRole = "admin";
    public const string DeviceRole = "device";

    public const string AdminOnly = "AdminOnly";
    public const string DeviceOnly = "DeviceOnly";
    public const string DeviceOrAdmin = "DeviceOrAdmin";
}

/// <summary>Names of claims the admin UI relies on. The forced-change flag is read from storage, not from here.</summary>
public static class ServerClaimTypes
{
    public const string Username = ClaimTypes.Name;
}
