namespace DailyMusings.Application.Abstractions;

public sealed record HealthProbeResult(string Name, bool Healthy, string? Detail)
{
    public static HealthProbeResult Ok(string name, string? detail = null) => new(name, true, detail);

    public static HealthProbeResult Fail(string name, string detail) => new(name, false, detail);
}

/// <summary>
/// One basic health probe. docs/开发指导.md §16 limits basic health to what the instance itself controls:
/// database writability, media-directory writability and the background executor's state. External
/// services (models, SMTP) must never be probed here — they get their own "test connection"
/// action, so that a broken upstream cannot make the instance look unhealthy.
/// </summary>
public interface IHealthProbe
{
    /// <summary>Stable probe name, safe to log and to show (never contains private content).</summary>
    string Name { get; }

    Task<HealthProbeResult> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reports whether an optional capability is currently available, without probing anything.
/// <para>
/// Semantic search is the motivating case (§8.3, decision A.8): once the embedding configuration changes,
/// the index is considered invalid until a rebuild finishes, and callers must be able to ask that
/// question cheaply on every search rather than discovering it by calling the model.
/// </para>
/// </summary>
public interface ICapabilityStatus
{
    Task<bool> IsSemanticSearchAvailableAsync(CancellationToken cancellationToken);
}

/// <summary>The temporary, self-expiring debug switch of docs/开发指导.md §16.</summary>
public sealed record DiagnosticModeState(bool Enabled, DateTimeOffset? ExpiresAtUtc, string? EnabledBy)
{
    public static DiagnosticModeState Disabled { get; } = new(false, null, null);

    /// <summary>How long is left, floored at zero. Shown to the operator so "when does this stop" is answerable.</summary>
    public TimeSpan RemainingAt(DateTimeOffset nowUtc) =>
        Enabled && ExpiresAtUtc is { } expiry && expiry > nowUtc ? expiry - nowUtc : TimeSpan.Zero;
}

/// <summary>
/// The one question the pipelines ask before writing content to the log
/// (docs/开发指导.md §16: 只有用户主动开启临时调试模式时才能记录内容相关信息).
/// <para>
/// It exists so that "we log content only when asked" is a property of the code rather than of each call site's
/// memory. The switch expires on its own: a debug mode that stays on because nobody remembered to turn it off is
/// how a transcript ends up in a log file months later.
/// </para>
/// </summary>
public interface IDiagnosticMode
{
    Task<DiagnosticModeState> GetAsync(CancellationToken cancellationToken);

    /// <summary>Enables it for a bounded time, recording who did it.</summary>
    Task<DiagnosticModeState> EnableAsync(string enabledBy, TimeSpan duration, CancellationToken cancellationToken);

    Task DisableAsync(CancellationToken cancellationToken);

    /// <summary>The cheap form, called on the hot path of every transcription and generation.</summary>
    Task<bool> IsContentLoggingAllowedAsync(CancellationToken cancellationToken);
}

/// <summary>The external services §16 gives a "test connection" action.</summary>
public enum ExternalService
{
    Transcription = 0,
    Generation = 1,
    Embedding = 2,
    Smtp = 3,
}

/// <summary>
/// The verdict of a test-connection. <see cref="Code"/> is a stable identifier, never a message from the remote:
/// an upstream's error text can echo the request, which may contain the user's writing.
/// </summary>
public sealed record ProbeResult(bool Ok, string Code, string Detail)
{
    public static ProbeResult Success(string detail) => new(true, "probe.ok", detail);

    public static ProbeResult Failure(string code, string detail) => new(false, code, detail);
}

/// <summary>
/// Checks one external service on demand (docs/开发指导.md §16).
/// <para>
/// Deliberately not a health probe. §16 requires that a broken model endpoint, SMTP server or blog cannot make the
/// instance look unhealthy — the instance is still perfectly able to accept captures while they are down — so this
/// is only ever reached because a human pressed a button.
/// </para>
/// </summary>
public interface IExternalServiceProbe
{
    Task<ProbeResult> ProbeAsync(ExternalService service, CancellationToken cancellationToken);

    Task<ProbeResult> ProbeModelAsync(ModelEndpointProbeRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Values currently shown in the model form. A connection test must probe these values, even before they are saved.
/// <paramref name="ApiKey"/> is write-only UI input; when omitted, the configured secret is resolved by name.
/// </summary>
public sealed record ModelEndpointProbeRequest(
    ExternalService Service,
    string BaseUrl,
    string SecretName,
    string? ApiKey = null);

/// <summary>
/// One line kept by the in-memory log buffer. Deliberately a plain record with no exception object: the operator
/// asked to see what the instance has been saying, not to download the user's content (§16).
/// </summary>
public sealed record LogLine(DateTimeOffset TimestampUtc, string Level, string Category, string Message);

/// <summary>
/// Reads back the most recent log lines this process wrote (docs/开发指导.md §16).
/// <para>
/// A bounded in-memory ring rather than a log file: the product's default logging is stdout by design, the
/// container's own log handling is the operator's business, and writing a second copy of every line to the
/// instance volume would put diagnostics into the backup set. What this adds is a page in the admin UI that
/// answers "what has it been doing" without asking the operator to find the container logs — while keeping the
/// §16 promise that nothing but what was already logged is ever available.
/// </para>
/// </summary>
public interface IRecentLogReader
{
    /// <summary>The newest lines, oldest first. Returns fewer when the buffer holds fewer.</summary>
    IReadOnlyList<LogLine> Read(int lines);

    /// <summary>The level the buffer is configured to keep, so the page can say what it is looking at.</summary>
    string MinimumLevel { get; }
}
