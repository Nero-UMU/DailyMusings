using System.Security.Cryptography;
using System.Text;
using DailyMusings.Application.Abstractions;

namespace DailyMusings.Infrastructure.Storage;

/// <summary>
/// Credentials typed into the admin page, stored encrypted outside the instance root (docs/开发指导.md §10.4 as
/// revised).
/// <para>
/// One file per name under <see cref="InstancePaths.UiSecretsPath"/>, encrypted with AES-GCM and a key that
/// lives beside them in the key ring directory. Three properties fall out of that placement and all three are
/// the point: the file never contains the plaintext, so a <c>grep</c> of a backup finds nothing; the directory
/// is not under the instance root, so the value is not in a backup or a readable export at all; and the key is
/// in the volume the backup deliberately excludes (decision A.14), so restoring an old database cannot hand
/// anybody a working credential.
/// </para>
/// <para>
/// Deliberately not the framework's DataProtection, which the host uses for the admin cookie: pulling the web
/// framework into this storage library to encrypt one small file would couple the persistence layer to the
/// host. What DataProtection would have added on top is key rotation and isolation, and neither is worth that
/// coupling for a value the operator can simply retype — the property that matters here is "not in the backup",
/// and that comes from where the file lives, not from which cipher wrote it.
/// </para>
/// <para>
/// The cost is honest: a value written here does not survive deleting the key ring, and it is not portable to
/// another instance. That is the same trade A.13 and A.14 already made for device tokens and the admin cookie —
/// credentials are not content, and backups carry content.
/// </para>
/// </summary>
public sealed class EncryptedUiSecretStore : IUiSecretStore
{
    /// <summary>
    /// The file format's first byte. A stored value is re-read by a later version of this code, so the layout is
    /// versioned from the start: a future change can add a second format instead of guessing what it is looking
    /// at.
    /// </summary>
    private const byte FormatVersion = 1;

    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly InstancePaths _paths;
    private readonly Lock _keyGate = new();
    private byte[]? _key;

    public EncryptedUiSecretStore(InstancePaths paths) => _paths = paths;

    public Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Reading and decrypting a small file has nothing to await; the async shape comes from the port, which
        // is async because callers on the request path should not be forced to block on a hypothetical store.
        return Task.FromResult(TryRead(name, out var value) ? value : null);
    }

    public Task SetAsync(string name, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_paths.UiSecretsPath);

        var plaintext = Encoding.UTF8.GetBytes(value);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        // The name is authenticated data, so a ciphertext cannot be renamed to another secret's file: swapping
        // two credentials would otherwise decrypt cleanly and quietly send mail as the wrong account.
        using (var aes = new AesGcm(Key(), TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(name));
        }

        var payload = new byte[1 + NonceBytes + TagBytes + ciphertext.Length];
        payload[0] = FormatVersion;
        nonce.CopyTo(payload, 1);
        tag.CopyTo(payload, 1 + NonceBytes);
        ciphertext.CopyTo(payload, 1 + NonceBytes + TagBytes);

        // Write-then-rename, like every other file this instance owns: a half-written credential file after a
        // crash would be unreadable, and an unreadable credential looks exactly like a wrong password.
        var path = PathFor(name);
        var temporary = path + ".tmp";

        File.WriteAllBytes(temporary, payload);
        File.Move(temporary, path, overwrite: true);

        CryptographicOperations.ZeroMemory(plaintext);

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();

        var path = PathFor(name);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken) =>
        await GetAsync(name, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// The synchronous form, used by <see cref="FileSecretStore"/>, whose port is synchronous by design because
    /// every model call resolves a secret name through it. Nothing here blocks on I/O that another thread owns:
    /// it is a small file read plus a decrypt.
    /// </summary>
    internal bool TryGet(string name, out string? value)
    {
        if (TryRead(name, out var found))
        {
            value = found;
            return true;
        }

        value = null;
        return false;
    }

    private bool TryRead(string name, out string? value)
    {
        value = null;

        var path = PathFor(name);

        if (!File.Exists(path))
        {
            return false;
        }

        byte[] payload;

        try
        {
            payload = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return false;
        }

        if (payload.Length <= 1 + NonceBytes + TagBytes || payload[0] != FormatVersion)
        {
            return false;
        }

        var plaintext = new byte[payload.Length - 1 - NonceBytes - TagBytes];

        try
        {
            using var aes = new AesGcm(Key(), TagBytes);

            aes.Decrypt(
                payload.AsSpan(1, NonceBytes),
                payload.AsSpan(1 + NonceBytes + TagBytes),
                payload.AsSpan(1 + NonceBytes, TagBytes),
                plaintext,
                AssociatedData(name));
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // A key ring that no longer matches — after a restore, or a deleted volume — makes the value
            // undecryptable. Reporting "not provisioned" is the honest answer: there is no value this instance
            // can use, and the admin page then says the password is missing instead of failing every send with a
            // cryptographic error nobody can act on.
            _ = exception;
            return false;
        }

        var text = Encoding.UTF8.GetString(plaintext);
        CryptographicOperations.ZeroMemory(plaintext);

        if (text.Length == 0)
        {
            return false;
        }

        value = text;
        return true;
    }

    /// <summary>
    /// The key, created on first use beside the values it protects. 256 random bits from the platform's
    /// cryptographic generator, written with owner-only permissions where the platform has them — the file is
    /// the whole of this store's confidentiality, so anything weaker would make the encryption decorative.
    /// </summary>
    private byte[] Key()
    {
        if (_key is { } cached)
        {
            return cached;
        }

        lock (_keyGate)
        {
            if (_key is { } second)
            {
                return second;
            }

            Directory.CreateDirectory(_paths.UiSecretsPath);

            var path = Path.Combine(_paths.UiSecretsPath, "ui-secrets.key");

            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);

                if (existing.Length != 32)
                {
                    throw new InvalidOperationException(
                        $"{path} is not a usable encryption key (expected 32 bytes). Delete it to start over; "
                        + "any password stored from the admin page will have to be entered again.");
                }

                return _key = existing;
            }

            var created = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(path, created);
            RestrictToOwner(path);

            return _key = created;
        }
    }

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // No portable equivalent, and the deployment target is Linux (decision A.10's compose images); the
            // containing volume is already documented as credential-equivalent material.
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The key is still usable; a filesystem that cannot express the mode is not a reason to refuse to
            // store a credential the operator just typed.
            _ = exception;
        }
    }

    private static byte[] AssociatedData(string name) => Encoding.UTF8.GetBytes(name);

    /// <summary>
    /// Maps a secret name to a file name. The name comes from a settings row, so it is validated the same way
    /// <see cref="FileSecretStore"/> validates one: a name containing a path separator would otherwise decide
    /// where this instance writes.
    /// </summary>
    private string PathFor(string name)
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

        return Path.Combine(_paths.UiSecretsPath, name);
    }
}
