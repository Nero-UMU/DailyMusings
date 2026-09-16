using DailyMusings.Application.Admin;
using DailyMusings.Application.Devices;
using DailyMusings.Application.Inputs;
using DailyMusings.Application.Jobs;
using DailyMusings.Application.System;
using DailyMusings.Contracts;
using DailyMusings.Infrastructure.Composition;
using DailyMusings.Infrastructure.Storage;
using DailyMusings.Server.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;

namespace DailyMusings.Server.Composition;

/// <summary>
/// Registers everything the server needs. Extracted from <c>Program</c> so the integration tests can stand up
/// the real application — the same registrations, the same endpoints — against a throwaway database instead of
/// reimplementing a test-only host that drifts from production.
/// </summary>
public static class ServerServiceCollectionExtensions
{
    /// <summary>
    /// Largest accepted audio upload. A short voice note is far below this; the cap exists so that a hostile or
    /// broken client cannot fill the media volume with one request.
    /// </summary>
    public const long MaxAudioUploadBytes = 25L * 1024 * 1024;

    public static IServiceCollection AddDailyMusingsServer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDailyMusingsInfrastructure(configuration);

        // Resolved from configuration rather than from the container, so the key ring location is known before
        // the provider exists.
        var paths = InstancePaths.FromConfiguration(configuration);

        // Use cases. Scoped, because each holds repositories that hold a scoped connection.
        services.AddScoped<InitializeAdminUseCase>();
        services.AddScoped<AdminSignInUseCase>();
        services.AddScoped<ChangeAdminCredentialsUseCase>();
        services.AddScoped<IssuePairingCodeUseCase>();
        services.AddScoped<RedeemPairingCodeUseCase>();
        services.AddScoped<ListDevicesUseCase>();
        services.AddScoped<RevokeDeviceUseCase>();
        services.AddScoped<RotateDeviceTokenUseCase>();
        services.AddScoped<AuthenticateDeviceUseCase>();
        services.AddScoped<SystemHealthUseCase>();

        // The capture loop (§8.2, §9.2).
        services.AddScoped<IngestVoiceInputUseCase>();
        services.AddScoped<IngestTextInputUseCase>();
        services.AddScoped<ListInputsUseCase>();
        services.AddScoped<GetInputUseCase>();
        services.AddScoped<ReviseTranscriptUseCase>();
        services.AddScoped<DeleteInputAudioUseCase>();
        services.AddScoped<DeleteInputUseCase>();
        services.AddScoped<RetryTranscriptionUseCase>();
        services.AddScoped<ListJobsUseCase>();
        services.AddScoped<RetryJobUseCase>();

        // Upload limits. A short voice note is measured in seconds, so an unbounded body is pure risk.
        services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = MaxAudioUploadBytes;
            options.ValueLengthLimit = 8 * 1024;
        });

        services
            .AddAuthentication(ServerAuthenticationPolicies.AdminCookie)
            .AddCookie(ServerAuthenticationPolicies.AdminCookie, options =>
            {
                options.LoginPath = "/login";
                options.AccessDeniedPath = "/login";
                options.Cookie.Name = ServerAuthenticationPolicies.AdminCookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;

                // §10.3 permits plain HTTP on a trusted network, so the cookie must not demand HTTPS — but the
                // UI keeps telling the operator the connection is insecure.
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);

                // A cookie handler's default reaction to an unauthenticated request is an HTTP redirect, which
                // is right for a browser page and wrong for the API: an API client must be told 401/403, not
                // silently handed a 302 to an HTML login form.
                options.Events.OnRedirectToLogin = context => RespondToApiAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    ApiErrorCodes.Unauthenticated,
                    "Authentication is required.");

                options.Events.OnRedirectToAccessDenied = context => RespondToApiAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    ApiErrorCodes.Forbidden,
                    "This credential is not allowed to perform that operation.");
            })
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>(
                ServerAuthenticationPolicies.DeviceToken,
                _ => { });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(ServerAuthenticationPolicies.AdminOnly, policy => policy
                .AddAuthenticationSchemes(ServerAuthenticationPolicies.AdminCookie)
                .RequireAuthenticatedUser());

            options.AddPolicy(ServerAuthenticationPolicies.DeviceOnly, policy => policy
                .AddAuthenticationSchemes(ServerAuthenticationPolicies.DeviceToken)
                .RequireAuthenticatedUser());

            options.AddPolicy(ServerAuthenticationPolicies.DeviceOrAdmin, policy => policy
                .AddAuthenticationSchemes(
                    ServerAuthenticationPolicies.DeviceToken,
                    ServerAuthenticationPolicies.AdminCookie)
                .RequireAuthenticatedUser());
        });

        services.AddCascadingAuthenticationState();
        services.AddAntiforgery();
        services.AddRazorComponents().AddInteractiveServerComponents();

        // Persist the key ring outside the instance root. Without this, ASP.NET Core keeps the keys inside the
        // container's writable layer, so every recreate invalidates all administrator sessions — and, worse, any
        // fix that moved them into the instance root would put session-forging material into every backup
        // (§10.4, decision A.13).
        services
            .AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(paths.KeyRingPath))
            .SetApplicationName("DailyMusings");

        return services;
    }

    /// <summary>Answers an API request with a status code, and a browser request with the usual redirect.</summary>
    private static Task RespondToApiAsync(
        RedirectContext<CookieAuthenticationOptions> context,
        int statusCode,
        string code,
        string message)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = statusCode;
            return context.Response.WriteAsJsonAsync(new ApiError(code, message));
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }
}
