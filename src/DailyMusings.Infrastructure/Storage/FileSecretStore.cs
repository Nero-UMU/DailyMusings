using DailyMusings.Application.Abstractions;

namespace DailyMusings.Infrastructure.Storage;

/// <summary>
/// Resolves secrets from a mounted directory — Docker secrets by default — falling back to environment
/// variables for local development (docs/开发指导.md §10.4).
/// <para>
/// Only the <em>name</em> of a secret ever appears in configuration, logs or an export. This class never
/// logs a value, and returns <c>null</c> rather than throwing when a secret is missing, so a misconfigured
/// model key degrades one feature instead of preventing startup.
/// </para>
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private const string EnvironmentPrefix = "DAILYMUSINGS_SECRET_";

    private readonly InstancePaths _paths;

    public FileSecretStore(InstancePaths paths) => _paths = paths;

    public string? TryGet(string name)
    {
        ValidateName(name);

        var fromFile = TryReadFile(name);
        if (fromFile is not null)
        {
            return fromFile;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentPrefix + ToEnvironmentName(name));
        return string.IsNullOrEmpty(fromEnvironment) ? null : fromEnvironment;
    }

    public bool Exists(string name) => TryGet(name) is not null;

    public IReadOnlyList<string> ListNames()
    {
        if (!Directory.Exists(_paths.SecretsPath))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(_paths.SecretsPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!;
    }

    private string? TryReadFile(string name)
    {
        if (!Directory.Exists(_paths.SecretsPath))
        {
            return null;
        }

        var path = Path.Combine(_paths.SecretsPath, name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            // Docker writes a trailing newline; a secret containing one would silently break auth headers.
            var value = File.ReadAllText(path).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rejects anything that is not a bare file name. Without this, a secret name coming from configuration
    /// could escape the secrets directory.
    /// </summary>
    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains("..", StringComparison.Ordinal) ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                "A secret name must be a plain file name without any path component.",
                nameof(name));
        }
    }

    private static string ToEnvironmentName(string name) =>
        name.Replace('.', '_').Replace('-', '_').ToUpperInvariant();
}
