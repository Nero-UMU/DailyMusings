using System.Text.Json;
using System.Text.Json.Serialization;
using DailyMusings.Application.Abstractions;

namespace DailyMusings.Infrastructure.Configuration;

/// <summary>
/// The on-disk form of <see cref="RuntimeOverrides"/> (docs/开发指导.md §8.1).
/// <para>
/// Static and synchronous on purpose: the server reads it in <c>Program</c> before the web host is built, so
/// there is no container to resolve anything from yet. Writes go through a temporary file and a rename, so a
/// crash midway leaves the previous value rather than a truncated one — the same rule the export, Markdown and
/// backup writers follow.
/// </para>
/// <para>
/// The file is deliberately <em>not</em> part of the backup set. Restoring a backup should restore the user's
/// thoughts, not silently move the instance to whatever port that backup happened to be running on.
/// </para>
/// </summary>
public static class RuntimeOverridesFile
{
    /// <summary>
    /// Ports below 1024 are rejected: the container runs as an unprivileged user, so binding one would fail and
    /// the instance would come back up unreachable.
    /// </summary>
    public const int MinimumPort = 1024;

    public const int MaximumPort = 65535;

    /// <summary>Set to <c>1</c> to ignore the file entirely — the way out if a bad override locks the operator out.</summary>
    public const string IgnoreVariableName = "DAILYMUSINGS_IGNORE_RUNTIME_OVERRIDES";

    /// <summary>
    /// Set to <c>1</c> when the deployment itself owns the listening port (the shipped Compose file does).
    /// <para>
    /// A container's published mapping is fixed when the container starts, so a port override on disk can only
    /// aim that mapping at a port nothing is listening on. This switch makes "change the port" a one-place edit
    /// (the deployment's own port variable) and turns the admin page's port card into a read-only statement of
    /// fact. It also ignores any override left behind by an earlier start, so switching this on cannot strand a
    /// running instance on the old port.
    /// </para>
    /// <para>
    /// Read as a string and compared against <c>"1"</c> rather than bound as a boolean: a deployment value that
    /// cannot be parsed has to mean "not locked", never an exception before the host exists.
    /// </para>
    /// </summary>
    public const string ListeningPortLockVariableName = "DM_LOCK_LISTENING_PORT";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>True when the operator has asked this start to ignore the overrides file.</summary>
    public static bool IsIgnored => IsOn(IgnoreVariableName);

    /// <summary>True when the deployment owns the listening port, so the override file has no say in it.</summary>
    public static bool IsListeningPortLocked => IsOn(ListeningPortLockVariableName);

    /// <summary>
    /// "Set to 1" — a string comparison on purpose, never a boolean binding. A deployment value that cannot be
    /// parsed has to mean "off" (§8.1), not an exception thrown before the web host exists.
    /// </summary>
    private static bool IsOn(string variableName) =>
        string.Equals(
            Environment.GetEnvironmentVariable(variableName),
            "1",
            StringComparison.Ordinal);

    public static RuntimeOverrides Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsIgnored || IsListeningPortLocked)
        {
            return RuntimeOverrides.None;
        }

        try
        {
            if (!File.Exists(path))
            {
                return RuntimeOverrides.None;
            }

            var persisted = JsonSerializer.Deserialize<PersistedRuntimeOverrides>(
                File.ReadAllText(path),
                SerializerOptions);

            if (persisted is null)
            {
                return RuntimeOverrides.None;
            }

            // A value outside the bindable range is treated as absent rather than as a reason to fail: the file is
            // the last thing that should stop an instance from serving.
            var port = persisted.Port is >= MinimumPort and <= MaximumPort ? persisted.Port : null;

            return new RuntimeOverrides(port, persisted.UpdatedAtUtc, persisted.UpdatedBy);
        }
        catch (IOException)
        {
            return RuntimeOverrides.None;
        }
        catch (UnauthorizedAccessException)
        {
            return RuntimeOverrides.None;
        }
        catch (JsonException)
        {
            return RuntimeOverrides.None;
        }
    }

    public static void Write(string path, RuntimeOverrides overrides)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(overrides);

        if (overrides.ListeningPort is { } port && port is < MinimumPort or > MaximumPort)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overrides),
                port,
                $"The listening port must be between {MinimumPort} and {MaximumPort}.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write beside the target, then rename: a reader either sees the old file or the new one.
        var staging = path + ".partial";
        File.WriteAllText(
            staging,
            JsonSerializer.Serialize(
                new PersistedRuntimeOverrides(overrides.ListeningPort, overrides.UpdatedAtUtc, overrides.UpdatedBy),
                SerializerOptions));

        File.Move(staging, path, overwrite: true);
    }

    public static void Delete(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The wire form. Named explicitly so the file reads <c>"port": 18321</c> — the compose healthcheck parses it
    /// with <c>sed</c>, and <c>listeningPort</c> would not match.
    /// </summary>
    internal sealed record PersistedRuntimeOverrides(
        [property: JsonPropertyName("port")] int? Port,
        [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset? UpdatedAtUtc,
        [property: JsonPropertyName("updatedBy")] string? UpdatedBy);
}

/// <summary>The container-resolved implementation of <see cref="IRuntimeOverridesStore"/>.</summary>
public sealed class FileRuntimeOverridesStore : IRuntimeOverridesStore
{
    private readonly Storage.InstancePaths _paths;

    public FileRuntimeOverridesStore(Storage.InstancePaths paths) => _paths = paths;

    public string ConfigPath => _paths.RuntimeConfigPath;

    public bool IsListeningPortLocked => RuntimeOverridesFile.IsListeningPortLocked;

    public RuntimeOverrides Read() => RuntimeOverridesFile.Read(_paths.RuntimeConfigPath);

    public void Write(int? listeningPort, string? updatedBy, DateTimeOffset atUtc) =>
        RuntimeOverridesFile.Write(
            _paths.RuntimeConfigPath,
            new RuntimeOverrides(listeningPort, atUtc, updatedBy));
}
