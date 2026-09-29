using System.Globalization;
using DailyMusings.Infrastructure.Configuration;
using DailyMusings.Infrastructure.Storage;
using DailyMusings.Server.Composition;

var builder = WebApplication.CreateBuilder(args);

// The listening port is the one setting that cannot come from the settings table: it decides which port the
// settings API is reachable on, so it has to be known before the host exists. It is read from a small file on
// disk (§8.1), which the admin page writes; everything else the admin page saves is read per call and applies
// without a restart.
var paths = InstancePaths.FromConfiguration(builder.Configuration);
var overrides = RuntimeOverridesFile.Read(paths.RuntimeConfigPath);

if (overrides.ListeningPort is { } port)
{
    // Explicit, so it wins over ASPNETCORE_URLS from the deployment configuration.
    builder.WebHost.UseUrls($"http://+:{port}");
}

builder.Services.AddDailyMusingsServer(builder.Configuration);
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

if (overrides.ListeningPort is { } effectivePort)
{
    app.Logger.LogInformation(
        "Listening on port {Port} because of the override in {RuntimeConfigPath} (set by {UpdatedBy} at "
        + "{UpdatedAtUtc}). Set {IgnoreVariable}=1 to ignore it.",
        effectivePort,
        paths.RuntimeConfigPath,
        overrides.UpdatedBy ?? "an administrator",
        // Format it here: an override written by hand may carry no timestamp, and "(null)" in a startup log is
        // the kind of detail that makes an operator doubt the rest of the line.
        overrides.UpdatedAtUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "an unknown time",
        RuntimeOverridesFile.IgnoreVariableName);
}
else if (RuntimeOverridesFile.IsListeningPortLocked && File.Exists(paths.RuntimeConfigPath))
{
    // The lock makes the file's contents moot, and silence here would leave an operator staring at a port override
    // that is visibly ignored. Say which switch did it, and where the port is actually decided.
    app.Logger.LogInformation(
        "{LockVariable}=1, so this deployment owns the listening port and the override in {RuntimeConfigPath} is "
        + "ignored. Change the port where the deployment defines it, not here.",
        RuntimeOverridesFile.ListeningPortLockVariableName,
        paths.RuntimeConfigPath);
}
else if (RuntimeOverridesFile.IsIgnored && File.Exists(paths.RuntimeConfigPath))
{
    app.Logger.LogWarning(
        "{IgnoreVariable}=1, so the runtime overrides in {RuntimeConfigPath} are being ignored.",
        RuntimeOverridesFile.IgnoreVariableName,
        paths.RuntimeConfigPath);
}

// Migrate and bootstrap before the first request is served, then wire the pipeline. Both steps live in
// WebApplicationExtensions so the integration tests run exactly this configuration.
await app.InitializeDailyMusingsAsync();
app.UseDailyMusings();

app.Run();
