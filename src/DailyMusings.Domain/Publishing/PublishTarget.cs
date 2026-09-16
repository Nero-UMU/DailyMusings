using DailyMusings.Domain.Common;

namespace DailyMusings.Domain.Publishing;

public enum PublishTargetType
{
    WordPress = 0,

    /// <summary>Hexo-style Markdown written into a mounted directory (§11.2).</summary>
    Markdown = 1,
}

/// <summary>
/// A named destination an instance can publish to (docs/开发指导.md §6.7). Several may exist at once.
/// </summary>
public sealed class PublishTarget
{
    private PublishTarget(
        PublishTargetId id,
        string name,
        PublishTargetType type,
        string? destinationReference)
    {
        Id = id;
        Name = name;
        Type = type;
        DestinationReference = destinationReference;
    }

    public PublishTargetId Id { get; }

    public string Name { get; private set; }

    public PublishTargetType Type { get; }

    /// <summary>
    /// Where the target writes: a configuration key for the remote site, or a mount path for Markdown.
    /// Never a secret — credentials live in Docker secrets and are referenced by name (§10.4).
    /// </summary>
    public string? DestinationReference { get; private set; }

    /// <summary>
    /// Off by default, and only a human may turn it on (§11.1). The default behaviour of the product is
    /// to upload a private draft and wait.
    /// </summary>
    public bool AutomaticPublishEnabled { get; private set; }

    /// <summary>Which administrator opted in. Non-empty by construction, so the switch is always attributable.</summary>
    public string? AutomaticPublishEnabledBy { get; private set; }

    public DateTimeOffset? AutomaticPublishEnabledAtUtc { get; private set; }

    public static PublishTarget Create(
        PublishTargetId id,
        string name,
        PublishTargetType type,
        string? destinationReference = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new PublishTarget(id, name.Trim(), type, destinationReference?.Trim());
    }

    public static PublishTarget Rehydrate(
        PublishTargetId id,
        string name,
        PublishTargetType type,
        string? destinationReference,
        bool automaticPublishEnabled,
        string? automaticPublishEnabledBy,
        DateTimeOffset? automaticPublishEnabledAtUtc) =>
        new(id, name, type, destinationReference)
        {
            AutomaticPublishEnabled = automaticPublishEnabled,
            AutomaticPublishEnabledBy = automaticPublishEnabledBy,
            AutomaticPublishEnabledAtUtc = automaticPublishEnabledAtUtc,
        };

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public void SetDestinationReference(string? destinationReference) =>
        DestinationReference = destinationReference?.Trim();

    /// <summary>
    /// Enables unattended publishing. The admin UI must re-authenticate and show the risk notice before
    /// calling this (§11.1); the domain's part of that bargain is refusing to record the switch without
    /// naming the administrator who made it.
    /// </summary>
    public void EnableAutomaticPublish(string enabledBy, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enabledBy);

        AutomaticPublishEnabled = true;
        AutomaticPublishEnabledBy = enabledBy.Trim();
        AutomaticPublishEnabledAtUtc = at;
    }

    public void DisableAutomaticPublish()
    {
        AutomaticPublishEnabled = false;
    }
}
