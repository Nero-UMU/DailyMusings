namespace DailyMusings.Application.Abstractions;

public sealed record HealthProbeResult(string Name, bool Healthy, string? Detail)
{
    public static HealthProbeResult Ok(string name, string? detail = null) => new(name, true, detail);

    public static HealthProbeResult Fail(string name, string detail) => new(name, false, detail);
}

/// <summary>
/// One basic health probe. docs/开发指导.md §16 limits basic health to what the instance itself controls:
/// database writability, media-directory writability and the background executor's state. External
/// services (models, SMTP, WordPress) must never be probed here — they get their own "test connection"
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
