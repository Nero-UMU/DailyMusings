using DailyMusings.Application.Abstractions;

namespace DailyMusings.Infrastructure.Storage;

/// <summary>
/// Resolves secrets from three places, in this order: the encrypted store the admin page writes, a mounted
/// directory — Docker secrets by default — and finally environment variables for local development
/// (docs/开发指导.md §10.4, and the admin-page credential added later).
/// <para>
/// Only the <em>name</em> of a secret ever appears in configuration, logs or an export. This class never logs a
/// value, and returns <c>null</c> rather than throwing when a secret is missing, so a misconfigured model key
/// degrades one feature instead of preventing startup.
/// </para>
/// <para>
/// The admin-page store comes first because it is the most specific answer: an operator who typed a password
/// into the page means it, and an instance whose secrets directory is empty behaves exactly as it did before
/// this store existed.
/// </para>
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private const string EnvironmentPrefix = "DAILYMUSINGS_SECRET_";

    private readonly InstancePaths _paths;
    private readonly EncryptedUiSecretStore? _uiSecrets;

    public FileSecretStore(InstancePaths paths)
        : this(paths, uiSecrets: null)
    {
    }

    /// <param name="uiSecrets">
    /// Optional so that a test (or a deployment without DataProtection) can keep using this store on its own:
    /// with nothing there, resolution is exactly the two-step file/environment lookup it always was.
    /// </param>
    public FileSecretStore(InstancePaths paths, EncryptedUiSecretStore? uiSecrets)
    {
        _paths = paths;
        _uiSecrets = uiSecrets;
    }

    public string? TryGet(string name)
    {
        ValidateName(name);

        // The synchronous port is deliberate: every model call resolves a secret name through it, and the
        // admin-page store is a small file read plus a decrypt with nothing to await.
        //
        // A record in the admin-page store answers the question outright — including an empty one, which is what
        // "清除已保存的密码" writes. Falling through to the file in that case would resurrect the credential the
        // operator just removed, and with the SMTP rule that a password requires encryption, a stale file would
        // silently stop an otherwise working relay from sending.
        if (_uiSecrets is not null && _uiSecrets.TryGet(name, out var fromUi))
        {
            return string.IsNullOrEmpty(fromUi) ? null : fromUi;
        }

        var fromFile = TryReadFile(name);
        if (fromFile is not null)
        {
            return fromFile;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentPrefix + ToEnvironmentName(name));
        return string.IsNullOrEmpty(fromEnvironment) ? null : fromEnvironment;
    }

    public bool Exists(string name) => TryGet(name) is not null;

    /// <summary>
    /// Where the value would come from, asked without returning it. The admin page needs this to tell "you have
    /// not set a password" apart from "your password is in a file I can read" (§12).
    /// </summary>
    public SecretSource ResolveSource(string name)
    {
        ValidateName(name);

        if (_uiSecrets is not null && _uiSecrets.TryGet(name, out var fromUi))
        {
            // Same rule as TryGet: a record here is the answer, and an empty one means "there is no such secret".
            return string.IsNullOrEmpty(fromUi) ? SecretSource.None : SecretSource.Ui;
        }

        if (TryReadFile(name) is not null)
        {
            return SecretSource.File;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentPrefix + ToEnvironmentName(name));

        return string.IsNullOrEmpty(fromEnvironment) ? SecretSource.None : SecretSource.Environment;
    }

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
