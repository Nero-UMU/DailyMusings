using System.Security.Claims;
using DailyMusings.Application.Abstractions;
using DailyMusings.Application.Admin;
using DailyMusings.Application.Devices;
using DailyMusings.Application.System;
using DailyMusings.Contracts;
using DailyMusings.Domain.Common;
using DailyMusings.Domain.Identity;
using DailyMusings.Server.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DailyMusings.Server.Composition;

/// <summary>
/// The HTTP surface. Phase one covers the endpoints §18 assigns to it: administrator sign-in and credential
/// change, device pairing, device administration and health. Everything else in §13 arrives with the phase
/// that defines its behaviour.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDailyMusingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        MapAdminEndpoints(endpoints);
        MapPairingEndpoints(endpoints);
        MapDeviceEndpoints(endpoints);
        MapSystemEndpoints(endpoints);

        return endpoints;
    }

    private static void MapAdminEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Antiforgery is disabled on these two form posts on purpose: sign-in carries credentials in the body,
        // and the credential change additionally requires the current password. A cross-site request cannot
        // forge either. This is a deliberate trade-off, not an oversight.
        endpoints.MapPost(ApiRoutes.AdminSignIn, SignInAsync)
            .AllowAnonymous()
            .DisableAntiforgery();

        endpoints.MapPost(ApiRoutes.AdminSignOut, SignOutAsync)
            .AllowAnonymous()
            .DisableAntiforgery();

        endpoints.MapPost(ApiRoutes.AdminChangeCredentials, ChangeCredentialsAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly)
            .DisableAntiforgery();
    }

    private static void MapPairingEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(ApiRoutes.PairingCodes, IssuePairingCodeAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        // Anonymous by necessity: the caller is a device that has no token yet. The code is the credential —
        // single-use, ten minutes, and hashed at rest.
        endpoints.MapPost(ApiRoutes.PairingRedeem, RedeemPairingCodeAsync)
            .AllowAnonymous()
            .DisableAntiforgery();
    }

    private static void MapDeviceEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ApiRoutes.Devices, ListDevicesAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        endpoints.MapGet(ApiRoutes.DeviceSelf, DeviceSelfAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.DeviceOnly);

        endpoints.MapDelete("/api/devices/{id}", RevokeDeviceAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);

        endpoints.MapPost("/api/devices/{id}/rotate", RotateDeviceTokenAsync)
            .RequireAuthorization(ServerAuthenticationPolicies.AdminOnly);
    }

    private static void MapSystemEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Anonymous so a container healthcheck needs no credential; details are shown only to an authenticated
        // administrator, since they can name internal paths.
        endpoints.MapGet(ApiRoutes.Health, HealthAsync).AllowAnonymous();
    }

    private static async Task<IResult> SignInAsync(
        HttpContext context,
        AdminSignInUseCase signIn,
        ILoggerFactory loggerFactory)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var username = form["username"].ToString();
        var password = form["password"].ToString();

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return Results.Redirect("/login?error=required");
        }

        // §10.3: on a plain HTTP connection the operator must actively acknowledge the interception risk before
        // the credentials are accepted. The acknowledgement is a gate, not a mitigation — it is not recorded as
        // making the connection safe anywhere.
        var acknowledgedRisk = string.Equals(
            form["acknowledgeRisk"].ToString(),
            "yes",
            StringComparison.Ordinal);

        if (!context.Request.IsHttps && !acknowledgedRisk)
        {
            return Results.Redirect("/login?error=risk");
        }

        var result = await signIn
            .ExecuteAsync(username, password, context.RequestAborted)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // Only the outcome is logged, never the attempted credentials (§16).
            loggerFactory.CreateLogger("DailyMusings.Server.Admin")
                .LogWarning("Administrator sign-in failed with outcome {Outcome}.", result.Outcome);

            return Results.Redirect(result.Outcome == SignInOutcome.NotInitialized
                ? "/login?error=notinitialized"
                : "/login?error=invalid");
        }

        var account = result.Account!;
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, account.Username),
            new Claim(ClaimTypes.Role, ServerAuthenticationPolicies.AdminRole),
        ], ServerAuthenticationPolicies.AdminCookie);

        await context.SignInAsync(
            ServerAuthenticationPolicies.AdminCookie,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false }).ConfigureAwait(false);

        return Results.Redirect(account.MustChangePassword ? "/change-credentials" : "/");
    }

    /// <summary>
    /// Signs out and redirects. Returns <see cref="Task"/> rather than <c>Task&lt;IResult&gt;</c> on purpose: a
    /// handler whose only parameter is <see cref="HttpContext"/> is treated as a raw request delegate, whose
    /// return value is discarded — so the redirect has to be written to the response explicitly.
    /// </summary>
    private static async Task SignOutAsync(HttpContext context)
    {
        await context.SignOutAsync(ServerAuthenticationPolicies.AdminCookie).ConfigureAwait(false);
        context.Response.Redirect("/login");
    }

    private static async Task<IResult> ChangeCredentialsAsync(
        HttpContext context,
        ChangeAdminCredentialsUseCase changeCredentials)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var current = form["currentPassword"].ToString();
        var newUsername = form["newUsername"].ToString();
        var newPassword = form["newPassword"].ToString();
        var confirm = form["confirmPassword"].ToString();

        if (string.IsNullOrEmpty(current) || string.IsNullOrWhiteSpace(newUsername) ||
            string.IsNullOrEmpty(newPassword) || string.IsNullOrEmpty(confirm))
        {
            return Results.Redirect("/change-credentials?error=required");
        }

        if (!string.Equals(newPassword, confirm, StringComparison.Ordinal))
        {
            return Results.Redirect("/change-credentials?error=mismatch");
        }

        try
        {
            await changeCredentials
                .ExecuteAsync(current, newUsername, newPassword, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (UseCaseException exception)
        {
            var flag = exception.Code switch
            {
                "admin.current_password.invalid" => "invalid",
                _ => "required",
            };

            return Results.Redirect($"/change-credentials?error={flag}");
        }
        catch (DomainException exception)
        {
            var flag = exception.Code switch
            {
                "admin.credentials.unchanged" => "unchanged",
                "admin.password.too_short" or "admin.password.too_long" or
                    "admin.username.charset" or "admin.username.length" => "weak",
                _ => "required",
            };

            return Results.Redirect($"/change-credentials?error={flag}");
        }

        // The username changed, so the existing cookie carries a stale name and the forced-change flag must be
        // recomputed from storage. Re-issuing the cookie is the honest way to do both.
        await context.SignOutAsync(ServerAuthenticationPolicies.AdminCookie).ConfigureAwait(false);

        return Results.Redirect("/login?changed=true");
    }

    private static async Task<IResult> IssuePairingCodeAsync(
        IssuePairingCodeUseCase issuePairingCode,
        CancellationToken cancellationToken)
    {
        var issued = await issuePairingCode.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(new PairingCodeResponse(
            issued.Code,
            issued.ExpiresAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static async Task<IResult> RedeemPairingCodeAsync(
        RedeemPairingCodeRequest? request,
        RedeemPairingCodeUseCase redeemPairingCode,
        CancellationToken cancellationToken)
    {
        if (!RequestValidation.TryValidateDeviceRegistration(request, out var failure))
        {
            return Results.Json(
                new ApiError(ApiErrorCodes.ValidationFailed, failure!),
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var device = await redeemPairingCode
                .ExecuteAsync(request!.Code, request.DeviceName, request.Platform, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new RedeemPairingCodeResponse(
                device.DeviceId.ToString(),
                device.DeviceName,
                device.Token));
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
    }

    private static async Task<IResult> ListDevicesAsync(
        ListDevicesUseCase listDevices,
        CancellationToken cancellationToken)
    {
        var devices = await listDevices.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(devices.Select(ToDto).ToArray());
    }

    private static async Task<IResult> DeviceSelfAsync(
        HttpContext context,
        ListDevicesUseCase listDevices,
        CancellationToken cancellationToken)
    {
        var claimed = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (claimed is null || !Guid.TryParse(claimed, out var deviceId))
        {
            return Results.Json(
                new ApiError(ApiErrorCodes.Unauthenticated, "The device token is not valid."),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var devices = await listDevices.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var match = devices.FirstOrDefault(device => device.Id.Value == deviceId);

        return match is null
            ? Results.NotFound(new ApiError(ApiErrorCodes.NotFound, "The device no longer exists."))
            : Results.Ok(ToDto(match));
    }

    private static async Task<IResult> RevokeDeviceAsync(
        string id,
        RevokeDeviceUseCase revokeDevice,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return Results.Json(
                new ApiError(ApiErrorCodes.NotFound, "No device with that identifier."),
                statusCode: StatusCodes.Status404NotFound);
        }

        try
        {
            await revokeDevice
                .ExecuteAsync(new DeviceId(parsed), cancellationToken)
                .ConfigureAwait(false);

            return Results.NoContent();
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
    }

    private static async Task<IResult> RotateDeviceTokenAsync(
        string id,
        RotateDeviceTokenUseCase rotateToken,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return Results.Json(
                new ApiError(ApiErrorCodes.NotFound, "No device with that identifier."),
                statusCode: StatusCodes.Status404NotFound);
        }

        try
        {
            var rotated = await rotateToken
                .ExecuteAsync(new DeviceId(parsed), cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new RotateDeviceTokenResponse(rotated.DeviceId.ToString(), rotated.Token));
        }
        catch (DomainException exception)
        {
            return MapDomainFailure(exception);
        }
    }

    private static async Task<IResult> HealthAsync(
        HttpContext context,
        SystemHealthUseCase systemHealth,
        CancellationToken cancellationToken)
    {
        var report = await systemHealth.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        var isAdministrator = context.User.Identity?.IsAuthenticated == true;

        var probes = report.Probes
            .Select(probe => new HealthProbeDto(
                probe.Name,
                probe.Healthy,
                isAdministrator ? probe.Detail : null))
            .ToArray();

        return Results.Json(
            new HealthResponse(
                report.Healthy,
                report.CheckedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                probes),
            statusCode: report.Healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    private static DeviceDto ToDto(Device device) => new(
        device.Id.ToString(),
        device.Name,
        device.Platform,
        device.CreatedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        device.LastSeenAtUtc?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        device.IsRevoked);

    /// <summary>
    /// Maps a domain rule violation onto HTTP. The stable domain code travels to the client; the message is
    /// developer-facing and only ever surfaced for client-safe failures.
    /// </summary>
    private static IResult MapDomainFailure(DomainException exception)
    {
        var status = exception.Code switch
        {
            "pairing.code.unknown" => StatusCodes.Status404NotFound,
            "device.unknown" => StatusCodes.Status404NotFound,
            "pairing.code.already_used" => StatusCodes.Status409Conflict,
            "pairing.code.expired" => StatusCodes.Status410Gone,
            _ => StatusCodes.Status400BadRequest,
        };

        return Results.Json(new ApiError(exception.Code, exception.Message), statusCode: status);
    }
}
