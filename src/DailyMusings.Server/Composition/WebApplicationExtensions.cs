using DailyMusings.Admin;
using DailyMusings.Infrastructure.Composition;
using DailyMusings.Infrastructure.Operations;
using DailyMusings.Server.Startup;

namespace DailyMusings.Server.Composition;

/// <summary>
/// The startup and request pipeline, expressed once so the host and the integration tests cannot drift apart.
/// <para>
/// A test that rebuilds its own pipeline proves the test's pipeline works, not the product's. Sharing these two
/// methods is what makes "the integration tests exercise the real application" a fact rather than a hope.
/// </para>
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Brings the instance to a servable state: any staged restore applied, schema migrated, administrator
    /// bootstrapped.
    /// <para>
    /// All three run before the first request so a failure aborts startup instead of leaving an instance that
    /// answers requests against a missing or half-built schema (§15.3). The restore comes first because it replaces
    /// the database the migration then upgrades — a backup taken by an older build has to be migrated like any other
    /// instance (§15.2 step 3).
    /// </para>
    /// </summary>
    public static async Task InitializeDailyMusingsAsync(
        this WebApplication app,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        var restored = await StagedRestoreStartupTask
            .ApplyAsync(app.Configuration, app.Logger, cancellationToken)
            .ConfigureAwait(false);

        if (restored is not null)
        {
            app.Logger.LogWarning("A staged restore was applied at startup: {RestoreSummary}", restored);
        }

        await app.Services.ApplyMigrationsAsync(cancellationToken).ConfigureAwait(false);
        await AdminBootstrap.RunAsync(app.Services, app.Logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Wires the middleware pipeline and the endpoints.</summary>
    public static WebApplication UseDailyMusings(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // First, so every later failure is answered with a stable error code instead of an empty 500.
        app.UseMiddleware<ExceptionHandlingMiddleware>();

        app.UseStaticFiles();

        app.UseAuthentication();
        app.UseMiddleware<CredentialChangeGateMiddleware>();
        app.UseAuthorization();

        // Required, not optional: Razor Components endpoints carry antiforgery metadata, and without this
        // middleware every Blazor page fails with "a middleware was not found that supports anti-forgery".
        // It runs after authentication because token validation reads the authenticated user's claims. The API
        // endpoints opt out individually with DisableAntiforgery, which is why their absence here went unnoticed
        // by API-only tests.
        app.UseAntiforgery();

        app.MapDailyMusingsEndpoints();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        return app;
    }
}
