namespace DailyMusings.Application.Abstractions;

/// <summary>
/// The handful of settings that have to be known <em>before</em> the web host exists, and that an administrator
/// can nevertheless change from the admin page (docs/开发指导.md §8.1).
/// <para>
/// These cannot live in the settings table: the listening port decides which port the settings API is even
/// reachable on, so it has to be read from a file on disk during startup. Everything that can wait until a
/// request arrives belongs in <c>IAppSettingStore</c> instead — this record is deliberately tiny.
/// </para>
/// </summary>
/// <param name="ListeningPort">
/// The port the instance listens on, or <c>null</c> to fall back to the deployment configuration. Takes effect
/// at the next start: a container's published port mapping is fixed when the container starts, so the operator
/// has to recreate it (the admin page spells out the exact command).
/// </param>
public sealed record RuntimeOverrides(
    int? ListeningPort,
    DateTimeOffset? UpdatedAtUtc,
    string? UpdatedBy)
{
    /// <summary>Nothing overridden: deployment configuration and built-in defaults decide everything.</summary>
    public static RuntimeOverrides None { get; } = new(null, null, null);
}

/// <summary>Reads and writes the bootstrap overrides of <see cref="RuntimeOverrides"/>.</summary>
public interface IRuntimeOverridesStore
{
    /// <summary>
    /// Where the overrides are kept. Reported to the administrator, because recovering from a port that does not
    /// work means editing or deleting this file, and a path they cannot see is a path they cannot fix.
    /// </summary>
    string ConfigPath { get; }

    /// <summary>
    /// True when the deployment owns the listening port, so nothing here can change it.
    /// <para>
    /// A container's published mapping is fixed when the container starts: an override saved from the admin page
    /// could only point that mapping at a port nothing is listening on. When this is true the page must not offer
    /// the edit and the API must refuse it, rather than storing a value that breaks the next restart.
    /// </para>
    /// </summary>
    bool IsListeningPortLocked { get; }

    /// <summary>
    /// The overrides currently on disk. Never throws: a missing, unreadable or malformed file means "no
    /// overrides", because an instance that refuses to start over a bad port override is worse than one that
    /// starts on the configured port.
    /// </summary>
    RuntimeOverrides Read();

    /// <summary>Replaces the overrides. Pass <c>null</c> for the port to clear the override.</summary>
    void Write(int? listeningPort, string? updatedBy, DateTimeOffset atUtc);
}
