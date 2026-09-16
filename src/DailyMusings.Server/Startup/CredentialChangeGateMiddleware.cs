using DailyMusings.Application.Abstractions;
using DailyMusings.Contracts;

namespace DailyMusings.Server.Startup;

/// <summary>
/// Enforces the forced credential change from §10.1 on every request, not just on the first one.
/// <para>
/// The flag is read from storage rather than from a claim, so it cannot be bypassed by a cookie issued before
/// the requirement was set, and it stops applying the instant the credentials are actually changed.
/// </para>
/// </summary>
public sealed class CredentialChangeGateMiddleware
{
    /// <summary>
    /// Paths that must keep working while the change is pending: the change form itself, the sign-out route,
    /// the Blazor framework files and the health endpoint (a container healthcheck must not depend on an
    /// operator having completed a login flow).
    /// </summary>
    private static readonly string[] ExemptPrefixes =
    [
        "/change-credentials",
        "/login",
        "/api/admin",
        "/api/system/health",
        "/_blazor",
        "/_framework",
        "/_content",
        "/favicon",
    ];

    private readonly RequestDelegate _next;
    private readonly ILogger<CredentialChangeGateMiddleware> _logger;

    public CredentialChangeGateMiddleware(RequestDelegate next, ILogger<CredentialChangeGateMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IAdminAccountRepository accounts)
    {
        if (context.User.Identity?.IsAuthenticated == true && !IsExempt(context.Request.Path))
        {
            var account = await accounts.GetAsync(context.RequestAborted).ConfigureAwait(false);

            if (account?.MustChangePassword == true)
            {
                _logger.LogInformation("Blocked a request pending the mandatory credential change.");

                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(
                        new ApiError(
                            ApiErrorCodes.CredentialsRequired,
                            "The administrator must change the initial credentials before using the API."),
                        context.RequestAborted).ConfigureAwait(false);

                    return;
                }

                context.Response.Redirect("/change-credentials");
                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsExempt(PathString path) =>
        ExemptPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}
